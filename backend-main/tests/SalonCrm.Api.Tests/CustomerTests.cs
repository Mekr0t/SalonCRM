using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class CustomerTests(ApiFactory factory)
{
    private static string Token() => Guid.NewGuid().ToString("N")[..12];

    private async Task<(HttpClient Admin, Guid ServiceId, Worker A, Worker B)> SetupAsync()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var a = await factory.CreateWorkerAsync(admin, "Aaa", serviceId);
        var b = await factory.CreateWorkerAsync(admin, "Bbb", serviceId);
        return (admin, serviceId, a, b);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string search, string extra = "")
    {
        var response = await client.GetAsync($"/api/customers?search={Uri.EscapeDataString(search)}{extra}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    // ---------- scoping ----------

    [Fact]
    public async Task Customer_served_only_by_another_user_is_404_and_omitted_everywhere()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var token = Token();
        var customerId = await factory.InsertCustomerAsync($"Cudzí {token}");
        await factory.InsertAppointmentAsync(b.Id, serviceId, StaffTestHelpers.MinutesAgo(120), customerId: customerId);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/customers/{customerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PatchJsonAsync($"/api/customers/{customerId}", new { notes = "x" })).StatusCode);
        Assert.Empty((await SearchAsync(a.Client, token)).GetProperty("items").EnumerateArray());
        var lookup = await a.Client.PostAsJsonAsync("/api/customers/lookup", new { ids = new[] { customerId, Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Assert.Empty((await lookup.ReadJsonAsync()).EnumerateArray());

        // owner and admin see it
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync($"/api/customers/{customerId}")).StatusCode);
        Assert.Single((await SearchAsync(admin, token)).GetProperty("items").EnumerateArray());
        var adminLookup = await (await admin.PostAsJsonAsync("/api/customers/lookup", new { ids = new[] { customerId } })).ReadJsonAsync();
        Assert.Equal(customerId, adminLookup[0].GetProperty("id").GetGuid());
        Assert.Equal($"Cudzí {token}", adminLookup[0].GetProperty("fullName").GetString());
        Assert.True(adminLookup[0].TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Shared_customer_shows_each_user_only_his_own_appointments_and_totals()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var token = Token();
        var customerId = await factory.InsertCustomerAsync($"Spoločný {token}");
        var t = StaffTestHelpers.MinutesAgo(600);
        var (a1, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, t, status: AppointmentStatus.Completed, customerId: customerId, paidAmount: 40m);
        var (b1, _) = await factory.InsertAppointmentAsync(b.Id, serviceId, t.AddHours(-3), status: AppointmentStatus.Completed, customerId: customerId, paidAmount: 100m);
        var (b2, _) = await factory.InsertAppointmentAsync(b.Id, serviceId, t.AddHours(-6), status: AppointmentStatus.Completed, customerId: customerId, paidAmount: 20m);

        var detailA = await (await a.Client.GetAsync($"/api/customers/{customerId}")).ReadJsonAsync();
        Assert.Equal([a1], detailA.GetProperty("appointments").Ids());
        Assert.Equal(1, detailA.GetProperty("visitCount").GetInt32());
        Assert.Equal(40m, detailA.GetProperty("totalSpent").GetDecimal());

        var detailB = await (await b.Client.GetAsync($"/api/customers/{customerId}")).ReadJsonAsync();
        Assert.Equal([b1, b2], detailB.GetProperty("appointments").Ids()); // newest first
        Assert.Equal(2, detailB.GetProperty("visitCount").GetInt32());
        Assert.Equal(120m, detailB.GetProperty("totalSpent").GetDecimal());

        var detailAdmin = await (await admin.GetAsync($"/api/customers/{customerId}")).ReadJsonAsync();
        Assert.Equal(3, detailAdmin.GetProperty("appointments").GetArrayLength());
        Assert.Equal(3, detailAdmin.GetProperty("visitCount").GetInt32());
        Assert.Equal(160m, detailAdmin.GetProperty("totalSpent").GetDecimal());

        // list counts are scoped the same way
        var itemA = (await SearchAsync(a.Client, token)).GetProperty("items")[0];
        Assert.Equal(1, itemA.GetProperty("visitCount").GetInt32());
        Assert.Equal(t, itemA.GetProperty("lastVisitAt").GetDateTime());
        var itemAdmin = (await SearchAsync(admin, token)).GetProperty("items")[0];
        Assert.Equal(3, itemAdmin.GetProperty("visitCount").GetInt32());
    }

    // ---------- list ----------

    [Fact]
    public async Task Search_matches_name_email_and_phone_case_insensitively()
    {
        var (admin, _, _, _) = await SetupAsync();
        var token = Token();
        var byName = await factory.InsertCustomerAsync($"Ľubica Šimková {token}");
        var byEmail = await factory.InsertCustomerAsync("Iný Zákazník", $"mail-{token}@Example.COM");
        var byPhone = await factory.InsertCustomerAsync("Ďalší Zákazník", phone: $"+421{token}");
        await factory.InsertCustomerAsync("Nezhoda");

        async Task<List<Guid>> Find(string s) => (await SearchAsync(admin, s)).GetProperty("items").Ids();

        Assert.Equal([byName], await Find($"šimková {token}".ToUpperInvariant()));
        Assert.Equal([byEmail], await Find($"MAIL-{token}@example"));
        Assert.Equal([byPhone], await Find($"+421{token.ToUpperInvariant()}"));
        Assert.Equal(3, (await Find(token)).Count);
    }

    [Fact]
    public async Task Search_treats_like_wildcards_literally()
    {
        var (admin, _, _, _) = await SetupAsync();
        var token = Token();
        var percent = await factory.InsertCustomerAsync($"Ab%{token}");
        await factory.InsertCustomerAsync($"AbX{token}");
        var underscore = await factory.InsertCustomerAsync($"Cd_{token}");
        await factory.InsertCustomerAsync($"CdY{token}");
        var backslash = await factory.InsertCustomerAsync($"Ef\\{token}");
        await factory.InsertCustomerAsync($"Ef{token}");

        Assert.Equal([percent], (await SearchAsync(admin, $"b%{token}")).GetProperty("items").Ids());
        Assert.Equal([underscore], (await SearchAsync(admin, $"d_{token}")).GetProperty("items").Ids());
        Assert.Equal([backslash], (await SearchAsync(admin, $"f\\{token}")).GetProperty("items").Ids());
    }

    [Fact]
    public async Task Paging_returns_total_count_and_ordered_pages()
    {
        var (admin, _, _, _) = await SetupAsync();
        var token = Token();
        var ids = new List<Guid>();
        foreach (var letter in "EBDAC")
        {
            ids.Add(await factory.InsertCustomerAsync($"{letter}{letter}-{token}"));
        }

        var page2 = await SearchAsync(admin, token, "&page=2&pageSize=2");

        Assert.Equal(5, page2.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal(2, page2.GetProperty("pageSize").GetInt32());
        Assert.Equal(["CC-" + token, "DD-" + token], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()).ToList());
        var last = await SearchAsync(admin, token, "&page=3&pageSize=2");
        Assert.Equal(1, last.GetProperty("items").GetArrayLength());
        var beyond = await SearchAsync(admin, token, "&page=4&pageSize=2");
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
        Assert.Equal(5, beyond.GetProperty("totalCount").GetInt32());

        var defaults = await SearchAsync(admin, token);
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(20, defaults.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=abc")]
    public async Task Invalid_paging_is_400(string query)
    {
        var (admin, _, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/customers?{query}")).StatusCode);
    }

    [Fact]
    public async Task Search_longer_than_100_characters_is_400()
    {
        var (admin, _, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/customers?search={new string('a', 101)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/customers?search={new string('a', 100)}")).StatusCode);
    }

    [Fact]
    public async Task List_counts_visit_count_last_visit_and_next_appointment_with_user_scoping()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var token = Token();
        var customerId = await factory.InsertCustomerAsync($"Počty {token}");
        var emptyId = await factory.InsertCustomerAsync($"Bez návštev {token}");
        var older = StaffTestHelpers.MinutesAgo(60 * 24 * 10);
        var newer = StaffTestHelpers.MinutesAgo(60 * 24 * 3);
        var nextA = StaffTestHelpers.FutureMonday(10);
        await factory.InsertAppointmentAsync(a.Id, serviceId, older, status: AppointmentStatus.Completed, customerId: customerId);
        await factory.InsertAppointmentAsync(a.Id, serviceId, newer, status: AppointmentStatus.Completed, customerId: customerId);
        await factory.InsertAppointmentAsync(a.Id, serviceId, newer.AddHours(-5), status: AppointmentStatus.Cancelled, customerId: customerId);
        await factory.InsertAppointmentAsync(a.Id, serviceId, nextA.AddDays(7), customerId: customerId);
        await factory.InsertAppointmentAsync(a.Id, serviceId, nextA.AddDays(14), customerId: customerId);
        // B: a completed visit that is the most recent overall and a Scheduled one that is the soonest overall
        var bLast = StaffTestHelpers.MinutesAgo(60 * 24);
        await factory.InsertAppointmentAsync(b.Id, serviceId, bLast, status: AppointmentStatus.Completed, customerId: customerId);
        await factory.InsertAppointmentAsync(b.Id, serviceId, nextA, customerId: customerId);

        var adminItem = (await SearchAsync(admin, $"Počty {token}")).GetProperty("items")[0];
        Assert.Equal(customerId, adminItem.GetProperty("id").GetGuid());
        Assert.Equal(3, adminItem.GetProperty("visitCount").GetInt32());
        Assert.Equal(bLast, adminItem.GetProperty("lastVisitAt").GetDateTime());
        Assert.Equal(nextA, adminItem.GetProperty("nextAppointmentAt").GetDateTime());

        var itemA = (await SearchAsync(a.Client, $"Počty {token}")).GetProperty("items")[0];
        Assert.Equal(2, itemA.GetProperty("visitCount").GetInt32());
        Assert.Equal(newer, itemA.GetProperty("lastVisitAt").GetDateTime());
        Assert.Equal(nextA.AddDays(7), itemA.GetProperty("nextAppointmentAt").GetDateTime());

        var itemB = (await SearchAsync(b.Client, $"Počty {token}")).GetProperty("items")[0];
        Assert.Equal(1, itemB.GetProperty("visitCount").GetInt32());
        Assert.Equal(bLast, itemB.GetProperty("lastVisitAt").GetDateTime());
        Assert.Equal(nextA, itemB.GetProperty("nextAppointmentAt").GetDateTime());

        var empty = (await SearchAsync(admin, $"Bez návštev {token}")).GetProperty("items")[0];
        Assert.Equal(emptyId, empty.GetProperty("id").GetGuid());
        Assert.Equal(0, empty.GetProperty("visitCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastVisitAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextAppointmentAt").ValueKind);
    }

    // ---------- detail / patch ----------

    [Fact]
    public async Task Detail_contains_profile_history_and_payments()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var customerId = await factory.InsertCustomerAsync("Detail Zákazníčka", phone: "+421900000001");
        var (paid, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.MinutesAgo(500),
            status: AppointmentStatus.Completed, customerId: customerId, paidAmount: 35.5m);
        await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.MinutesAgo(300),
            status: AppointmentStatus.NoShow, customerId: customerId);

        var body = await (await a.Client.GetAsync($"/api/customers/{customerId}")).ReadJsonAsync();

        Assert.Equal("Detail Zákazníčka", body.GetProperty("fullName").GetString());
        Assert.Equal("+421900000001", body.GetProperty("phone").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, body.GetProperty("gdprConsentAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, body.GetProperty("createdAt").ValueKind);
        Assert.Equal(1, body.GetProperty("visitCount").GetInt32());
        Assert.Equal(35.5m, body.GetProperty("totalSpent").GetDecimal());
        var items = body.GetProperty("appointments").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("NoShow", items[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("payment").ValueKind);
        var paidItem = items.Single(i => i.GetProperty("id").GetGuid() == paid);
        Assert.Equal(35.5m, paidItem.GetProperty("payment").GetProperty("amount").GetDecimal());
        Assert.Equal("Cash", paidItem.GetProperty("payment").GetProperty("method").GetString());
        Assert.Equal(serviceId, paidItem.GetProperty("service").GetProperty("id").GetGuid());
        Assert.Equal(a.Id, paidItem.GetProperty("employee").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/customers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Patch_updates_only_sent_fields_and_empty_string_clears_phone_and_notes()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var customerId = await factory.InsertCustomerAsync("Pôvodné Meno", phone: "+421900000002");
        await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.MinutesAgo(120), customerId: customerId);
        var url = $"/api/customers/{customerId}";

        var first = await a.Client.PatchJsonAsync(url, new { notes = "suchá pleť" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.ReadJsonAsync();
        Assert.Equal("suchá pleť", body.GetProperty("notes").GetString());
        Assert.Equal("Pôvodné Meno", body.GetProperty("fullName").GetString());
        Assert.Equal("+421900000002", body.GetProperty("phone").GetString());

        await a.Client.PatchRawAsync(url, """{"fullName":null,"phone":null,"notes":null}""");
        var kept = await (await a.Client.GetAsync(url)).ReadJsonAsync();
        Assert.Equal("suchá pleť", kept.GetProperty("notes").GetString());
        Assert.Equal("+421900000002", kept.GetProperty("phone").GetString());

        var renamed = await a.Client.PatchRawAsync(url, """{"fullName":"  Nové Meno  ","phone":"","notes":""}""");
        var renamedBody = await renamed.ReadJsonAsync();
        Assert.Equal("Nové Meno", renamedBody.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, renamedBody.GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, renamedBody.GetProperty("notes").ValueKind);
    }

    [Fact]
    public async Task Patch_validates_name_and_lengths()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var customerId = await factory.InsertCustomerAsync("Meno");
        await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.MinutesAgo(120), customerId: customerId);
        var url = $"/api/customers/{customerId}";

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PatchRawAsync(url, """{"fullName":""}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PatchRawAsync(url, """{"fullName":"   "}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PatchJsonAsync(url, new { fullName = new string('x', 201) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PatchJsonAsync(url, new { phone = new string('1', 51) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PatchJsonAsync(url, new { notes = new string('n', 4001) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.PatchJsonAsync(url, new { notes = new string('n', 4000) })).StatusCode);
        Assert.Equal("Meno", (await (await a.Client.GetAsync(url)).ReadJsonAsync()).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Admin_may_patch_any_customer()
    {
        var (admin, _, _, _) = await SetupAsync();
        var customerId = await factory.InsertCustomerAsync("Bez termínov");

        var response = await admin.PatchJsonAsync($"/api/customers/{customerId}", new { notes = "admin poznámka" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- lookup ----------

    [Fact]
    public async Task Lookup_validates_ids()
    {
        var (admin, _, _, _) = await SetupAsync();

        var tooMany = await admin.PostAsJsonAsync("/api/customers/lookup", new { ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        var max = await admin.PostAsJsonAsync("/api/customers/lookup", new { ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.OK, max.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/customers/lookup", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsync("/api/customers/lookup", new StringContent("""{"ids":null}""", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        var empty = await admin.PostAsJsonAsync("/api/customers/lookup", new { ids = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await empty.ReadJsonAsync()).EnumerateArray());
    }
}
