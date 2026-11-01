using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class RetentionActionTests(ApiFactory factory)
{
    private static string Url(Guid customerId) => $"/api/customers/{customerId}/retention-actions";

    private async Task<(HttpClient Admin, Guid AdminId, Guid CustomerId)> SetupAsync()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var me = await (await admin.GetAsync("/api/auth/me")).ReadJsonAsync();
        var customerId = await factory.InsertCustomerAsync();
        return (admin, me.GetProperty("id").GetGuid(), customerId);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var customerId = await factory.InsertCustomerAsync();
        using var anon = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Url(customerId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync(Url(customerId), new { channel = "Phone", note = "x" })).StatusCode);
    }

    [Fact]
    public async Task User_role_is_403_on_get_and_post()
    {
        var (admin, _, customerId) = await SetupAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var worker = await factory.CreateWorkerAsync(admin, "Aaa", serviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.Client.GetAsync(Url(customerId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.Client.PostAsJsonAsync(Url(customerId), new { channel = "Phone", note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Admin_post_returns_201_with_trimmed_note_rounded_probability_and_creator()
    {
        var (admin, adminId, customerId) = await SetupAsync();
        var before = DateTime.UtcNow.AddMinutes(-1);
        var response = await admin.PostAsJsonAsync(Url(customerId),
            new { channel = "InPerson", note = "  Zavolať  s ponukou \n", churnProbability = 0.12345m });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"{Url(customerId)}/{id}", response.Headers.Location!.ToString());
        Assert.Equal("InPerson", body.GetProperty("channel").GetString());
        Assert.Equal("Zavolať  s ponukou", body.GetProperty("note").GetString());
        Assert.Equal(0.123m, body.GetProperty("churnProbability").GetDecimal());
        Assert.True(body.GetProperty("createdAt").GetDateTime() >= before);
        Assert.Equal(adminId, body.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("createdBy").GetProperty("fullName").GetString()));

        var stored = await factory.WithDbAsync(db => db.RetentionActions.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(0.123m, stored.ChurnProbability);
        Assert.Equal(adminId, stored.CreatedByUserId);
    }

    [Fact]
    public async Task Post_without_probability_stores_null()
    {
        var (admin, _, customerId) = await SetupAsync();
        var response = await admin.PostAsJsonAsync(Url(customerId), new { channel = "Sms", note = "ahoj" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await response.ReadJsonAsync()).GetProperty("churnProbability").ValueKind);
    }

    [Fact]
    public async Task Probability_boundaries_0_and_1_are_accepted()
    {
        var (admin, _, customerId) = await SetupAsync();
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync(Url(customerId), new { channel = "Email", note = "a", churnProbability = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync(Url(customerId), new { channel = "Email", note = "b", churnProbability = 1 })).StatusCode);
    }

    [Fact]
    public async Task Get_returns_newest_first_with_expected_shape()
    {
        var (admin, adminId, customerId) = await SetupAsync();
        var first = (await (await admin.PostAsJsonAsync(Url(customerId), new { channel = "Phone", note = "prvá" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        await Task.Delay(20);
        var second = (await (await admin.PostAsJsonAsync(Url(customerId), new { channel = "Email", note = "druhá", churnProbability = 0.5 })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var otherCustomer = await factory.InsertCustomerAsync();
        await admin.PostAsJsonAsync(Url(otherCustomer), new { channel = "Sms", note = "iný zákazník" });

        var response = await admin.GetAsync(Url(customerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.ReadJsonAsync();
        Assert.Equal([second, first], list.Ids());
        var item = list[0];
        Assert.Equal("Email", item.GetProperty("channel").GetString());
        Assert.Equal("druhá", item.GetProperty("note").GetString());
        Assert.Equal(0.5m, item.GetProperty("churnProbability").GetDecimal());
        Assert.Equal(adminId, item.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.True(item.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Get_with_no_actions_is_empty_array()
    {
        var (admin, _, customerId) = await SetupAsync();
        Assert.Empty((await (await admin.GetAsync(Url(customerId))).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Unknown_customer_is_404_on_get_and_post()
    {
        var (admin, _, _) = await SetupAsync();
        var unknown = Guid.NewGuid();
        var get = await admin.GetAsync(Url(unknown));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(404, (await get.ReadJsonAsync()).GetProperty("status").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(Url(unknown), new { channel = "Phone", note = "x" })).StatusCode);
    }

    public static TheoryData<object> InvalidBodies => new()
    {
        new { channel = "Pigeon", note = "x" },
        new { note = "x" },
        new { channel = "Phone", note = "" },
        new { channel = "Phone", note = "   \t " },
        new { channel = "Phone" },
        new { channel = "Phone", note = new string('a', 2001) },
        new { channel = "Phone", note = "x", churnProbability = -0.001 },
        new { channel = "Phone", note = "x", churnProbability = 1.001 },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_body_is_400_and_nothing_is_stored(object body)
    {
        var (admin, _, customerId) = await SetupAsync();
        var response = await admin.PostAsJsonAsync(Url(customerId), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await response.ReadJsonAsync()).GetProperty("status").GetInt32());
        Assert.Empty((await (await admin.GetAsync(Url(customerId))).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Note_of_exactly_2000_chars_after_trim_is_accepted()
    {
        var (admin, _, customerId) = await SetupAsync();
        var note = new string('a', 2000);
        var response = await admin.PostAsJsonAsync(Url(customerId), new { channel = "Phone", note = "  " + note + "  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(note, (await response.ReadJsonAsync()).GetProperty("note").GetString());
    }
}
