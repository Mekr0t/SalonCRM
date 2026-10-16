using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class AppointmentTests(ApiFactory factory)
{
    private const string NotStarted = "Termín ešte nezačal.";
    private const string WrongState = "Termín v tomto stave už nie je možné zmeniť.";
    private const string AdminOnlyCancel = "Termín, ktorý už začal, môže zrušiť iba administrátor.";
    private const string SlotTaken = "Vybraný termín už nie je voľný.";
    private const string InvalidTime = "Neplatný čas termínu.";

    private async Task<(HttpClient Admin, Guid ServiceId, Worker A, Worker B)> SetupAsync(
        int duration = 60, decimal price = 40m)
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(duration, price);
        var a = await factory.CreateWorkerAsync(admin, "Aaa", serviceId);
        var b = await factory.CreateWorkerAsync(admin, "Bbb", serviceId);
        return (admin, serviceId, a, b);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? detail = null)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        if (detail is not null)
        {
            Assert.Equal(detail, problem.GetProperty("detail").GetString());
        }
    }

    private static object Complete(string method = "Cash", decimal? amount = null) =>
        new { paymentMethod = method, amount };

    // ---------- scoping ----------

    [Fact]
    public async Task User_gets_404_for_a_foreign_appointment_on_every_endpoint()
    {
        var (_, serviceId, a, b) = await SetupAsync();
        var (foreign, _) = await factory.InsertAppointmentAsync(b.Id, serviceId, MinutesAgo(180));
        var url = $"/api/appointments/{foreign}";

        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PatchJsonAsync(url, new { staffNote = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsJsonAsync(url + "/complete", Complete())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsync(url + "/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsync(url + "/no-show", null)).StatusCode);

        var row = await factory.LoadAppointmentAsync(foreign);
        Assert.Equal(AppointmentStatus.Scheduled, row.Status);
        Assert.Null(row.StaffNote);
        // the owner still sees it
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync(url)).StatusCode);
    }

    private static DateTime MinutesAgo(int minutes) => StaffTestHelpers.MinutesAgo(minutes);

    [Fact]
    public async Task List_is_scoped_and_foreign_employee_filter_is_403_for_user_but_fine_for_admin()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        var (idA, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        var (idB, _) = await factory.InsertAppointmentAsync(b.Id, serviceId, start);
        var from = start.AddHours(-1);
        var to = start.AddHours(3);

        var own = await (await a.Client.ListAppointmentsAsync(from, to)).ReadJsonAsync();
        Assert.Equal([idA], own.Ids());

        var self = await a.Client.ListAppointmentsAsync(from, to, $"&employeeId={a.Id}");
        Assert.Equal([idA], (await self.ReadJsonAsync()).Ids());

        await AssertProblemAsync(await a.Client.ListAppointmentsAsync(from, to, $"&employeeId={b.Id}"), HttpStatusCode.Forbidden);

        var all = (await (await admin.ListAppointmentsAsync(from, to)).ReadJsonAsync()).Ids();
        Assert.Contains(idA, all);
        Assert.Contains(idB, all);
        var onlyB = await (await admin.ListAppointmentsAsync(from, to, $"&employeeId={b.Id}")).ReadJsonAsync();
        Assert.Equal([idB], onlyB.Ids());
    }

    [Fact]
    public async Task User_cannot_create_or_reassign_for_another_employee()
    {
        var (_, serviceId, a, b) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);

        var create = await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start, b.Id));
        await AssertProblemAsync(create, HttpStatusCode.Forbidden);

        var (own, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        var patch = await a.Client.PatchJsonAsync($"/api/appointments/{own}", new { employeeId = b.Id });
        await AssertProblemAsync(patch, HttpStatusCode.Forbidden);
        Assert.Equal(a.Id, (await factory.LoadAppointmentAsync(own)).EmployeeId);

        // naming himself is fine
        var self = await a.Client.PatchJsonAsync($"/api/appointments/{own}", new { employeeId = a.Id });
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
    }

    // ---------- transitions ----------

    [Fact]
    public async Task Complete_future_appointment_is_409_not_started()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.FutureMonday(10));

        var response = await a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, NotStarted);
        Assert.Equal(AppointmentStatus.Scheduled, (await factory.LoadAppointmentAsync(id)).Status);
        Assert.False(await factory.WithDbAsync(db => db.Payments.AnyAsync(p => p.AppointmentId == id)));
    }

    [Fact]
    public async Task Complete_past_appointment_records_payment_with_default_amount_and_recorder()
    {
        var (_, serviceId, a, _) = await SetupAsync(price: 55m);
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120), price: 55m);

        var response = await a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete("Card"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);
        var payment = body.GetProperty("payment");
        Assert.Equal(55m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("Card", payment.GetProperty("method").GetString());
        Assert.Equal(a.Id, payment.GetProperty("recordedBy").GetProperty("id").GetGuid());

        var row = await factory.LoadAppointmentAsync(id);
        Assert.Equal(AppointmentStatus.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
        var dbPayment = await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.AppointmentId == id));
        Assert.Equal(55m, dbPayment.Amount);
        Assert.Equal(PaymentMethod.Card, dbPayment.Method);
        Assert.Equal(a.Id, dbPayment.RecordedByUserId);
        Assert.Equal(row.CompletedAt, dbPayment.PaidAt);
    }

    [Fact]
    public async Task Complete_accepts_a_custom_amount()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var response = await a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete("Cash", 30.5m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(30.5m, (await response.ReadJsonAsync()).GetProperty("payment").GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10000.001")]
    [InlineData("1.234")]
    [InlineData("10000.01")]
    public async Task Complete_rejects_invalid_amounts_with_400_and_changes_nothing(string amount)
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var response = await a.Client.PostAsync($"/api/appointments/{id}/complete",
            new StringContent($$"""{"paymentMethod":"Cash","amount":{{amount}}}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AppointmentStatus.Scheduled, (await factory.LoadAppointmentAsync(id)).Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10000")]
    [InlineData("12.34")]
    public async Task Complete_accepts_boundary_amounts(string amount)
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var response = await a.Client.PostAsync($"/api/appointments/{id}/complete",
            new StringContent($$"""{"paymentMethod":"Cash","amount":{{amount}}}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"paymentMethod":null}""")]
    [InlineData("""{"paymentMethod":"Bitcoin"}""")]
    [InlineData("""{"paymentMethod":1}""")]
    public async Task Complete_requires_a_valid_payment_method(string json)
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var response = await a.Client.PostAsync($"/api/appointments/{id}/complete",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AppointmentStatus.Scheduled, (await factory.LoadAppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task Second_complete_is_409_and_creates_no_second_payment()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));
        Assert.Equal(HttpStatusCode.OK, (await a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete())).StatusCode);

        var again = await a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete("Card", 1m));

        await AssertProblemAsync(again, HttpStatusCode.Conflict, WrongState);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Payments.CountAsync(p => p.AppointmentId == id)));
    }

    [Fact]
    public async Task Cancel_future_by_user_is_200_and_past_by_user_is_409_but_admin_may()
    {
        var (admin, serviceId, a, _) = await SetupAsync();
        var (future, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.FutureMonday(10));
        var (past, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var cancelled = await a.Client.PostAsync($"/api/appointments/{future}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var body = await cancelled.ReadJsonAsync();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("cancelledAt").ValueKind);

        await AssertProblemAsync(await a.Client.PostAsync($"/api/appointments/{past}/cancel", null),
            HttpStatusCode.Conflict, AdminOnlyCancel);
        Assert.Equal(AppointmentStatus.Scheduled, (await factory.LoadAppointmentAsync(past)).Status);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/appointments/{past}/cancel", null)).StatusCode);
        var row = await factory.LoadAppointmentAsync(past);
        Assert.Equal(AppointmentStatus.Cancelled, row.Status);
        Assert.NotNull(row.CancelledAt);
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_for_a_new_booking()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        await AssertProblemAsync(
            await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start)),
            HttpStatusCode.Conflict, SlotTaken);

        await a.Client.PostAsync($"/api/appointments/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Created,
            (await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start))).StatusCode);
    }

    [Fact]
    public async Task No_show_future_is_409_and_past_is_200()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (future, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.FutureMonday(10));
        var (past, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        await AssertProblemAsync(await a.Client.PostAsync($"/api/appointments/{future}/no-show", null),
            HttpStatusCode.Conflict, NotStarted);
        var response = await a.Client.PostAsync($"/api/appointments/{past}/no-show", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NoShow", (await response.ReadJsonAsync()).GetProperty("status").GetString());
        Assert.Equal(AppointmentStatus.NoShow, (await factory.LoadAppointmentAsync(past)).Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    public async Task Every_transition_out_of_a_final_state_is_409(AppointmentStatus final)
    {
        var (admin, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120), status: final);

        await AssertProblemAsync(await admin.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete()),
            HttpStatusCode.Conflict, WrongState);
        await AssertProblemAsync(await admin.PostAsync($"/api/appointments/{id}/cancel", null),
            HttpStatusCode.Conflict, WrongState);
        await AssertProblemAsync(await admin.PostAsync($"/api/appointments/{id}/no-show", null),
            HttpStatusCode.Conflict, WrongState);
        Assert.Equal(final, (await factory.LoadAppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task Parallel_complete_and_cancel_yield_exactly_one_winner_and_a_consistent_state()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (admin1, _) = await factory.LoginAdminAsync();
        var (admin2, _) = await factory.LoginAdminAsync();

        for (var i = 0; i < 6; i++)
        {
            var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120 * (i + 1)));

            var complete = Task.Run(() => admin1.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete()));
            var cancel = Task.Run(() => admin2.PostAsync($"/api/appointments/{id}/cancel", null));
            var responses = await Task.WhenAll(complete, cancel);

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var row = await factory.LoadAppointmentAsync(id);
            var payments = await factory.WithDbAsync(db => db.Payments.CountAsync(p => p.AppointmentId == id));
            if (row.Status == AppointmentStatus.Completed)
            {
                Assert.Equal(1, payments);
                Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            }
            else
            {
                Assert.Equal(AppointmentStatus.Cancelled, row.Status);
                Assert.Equal(0, payments);
                Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
            }
        }
    }

    [Fact]
    public async Task Parallel_completes_create_exactly_one_payment()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => a.Client.PostAsJsonAsync($"/api/appointments/{id}/complete", Complete()))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Payments.CountAsync(p => p.AppointmentId == id)));
    }

    // ---------- staff booking ----------

    [Fact]
    public async Task User_books_for_himself_with_source_staff()
    {
        var (_, serviceId, a, _) = await SetupAsync(price: 33m);
        var start = StaffTestHelpers.FutureMonday(10);
        var email = $"phase-{Guid.NewGuid():N}@salon.test";

        var response = await a.Client.PostAsJsonAsync("/api/appointments",
            StaffTestHelpers.StaffBody(serviceId, start, email: email, staffNote: "interná"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(a.Id, body.GetProperty("employee").GetProperty("id").GetGuid());
        Assert.Equal("Staff", body.GetProperty("source").GetString());
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal(33m, body.GetProperty("price").GetDecimal());
        Assert.Equal(start, body.GetProperty("startsAt").GetDateTime());
        Assert.Equal(start.AddHours(1), body.GetProperty("endsAt").GetDateTime());
        Assert.Equal(email, body.GetProperty("customer").GetProperty("email").GetString());
        Assert.Equal("poznámka zákazníka", body.GetProperty("customerNote").GetString());
        Assert.Equal("interná", body.GetProperty("staffNote").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("payment").ValueKind);
        Assert.EndsWith(body.GetProperty("id").GetGuid().ToString(), response.Headers.Location!.ToString());
        // visible via detail
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync($"/api/appointments/{body.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task User_booking_without_employee_id_always_goes_to_himself()
    {
        var (_, serviceId, a, b) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        await factory.InsertAppointmentAsync(a.Id, serviceId, start.AddHours(-2)); // A is busier that day

        var response = await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(a.Id, (await response.ReadJsonAsync()).GetProperty("employee").GetProperty("id").GetGuid());
        Assert.NotEqual(b.Id, a.Id);
    }

    [Fact]
    public async Task User_who_does_not_perform_the_service_gets_409()
    {
        var (admin, serviceId, _, _) = await SetupAsync();
        var other = await factory.CreateWorkerAsync(admin, "Ccc"); // no services

        var response = await other.Client.PostAsJsonAsync("/api/appointments",
            StaffTestHelpers.StaffBody(serviceId, StaffTestHelpers.FutureMonday(10)));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, SlotTaken);
    }

    [Fact]
    public async Task Admin_books_for_a_chosen_employee()
    {
        var (admin, serviceId, _, b) = await SetupAsync();

        var response = await admin.PostAsJsonAsync("/api/appointments",
            StaffTestHelpers.StaffBody(serviceId, StaffTestHelpers.FutureMonday(10), b.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(b.Id, body.GetProperty("employee").GetProperty("id").GetGuid());
        // and B sees it
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync($"/api/appointments/{id}")).StatusCode);
    }

    [Fact]
    public async Task Admin_without_employee_id_gets_the_least_busy_employee_by_the_anyone_rule()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        await factory.InsertAppointmentAsync(a.Id, serviceId, start.AddHours(-2)); // A has 1 that day, B none

        var first = await admin.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(b.Id, (await first.ReadJsonAsync()).GetProperty("employee").GetProperty("id").GetGuid());

        // now both have one: tie goes to the name that sorts first (Aaa)
        var second = await admin.PostAsJsonAsync("/api/appointments",
            StaffTestHelpers.StaffBody(serviceId, start.AddHours(3)));
        Assert.Equal(a.Id, (await second.ReadJsonAsync()).GetProperty("employee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Staff_booking_validates_slot_taken_misaligned_and_past()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        await factory.InsertAppointmentAsync(a.Id, serviceId, start);

        await AssertProblemAsync(
            await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start.AddMinutes(30))),
            HttpStatusCode.Conflict, SlotTaken);
        await AssertProblemAsync(
            await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start.AddHours(2).AddMinutes(7))),
            HttpStatusCode.BadRequest, InvalidTime);
        var longAgo = TestSalon.LocalToUtc(new DateOnly(2020, 1, 6), 10);
        await AssertProblemAsync(
            await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, longAgo)),
            HttpStatusCode.BadRequest, InvalidTime);
    }

    [Fact]
    public async Task Staff_booking_requires_gdpr_consent_and_valid_input()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start, gdprConsent: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start, gdprConsent: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.PostAsJsonAsync("/api/appointments", new { startsAt = start, fullName = "X", email = "a@b.sk", gdprConsent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, start, email: "not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(Guid.NewGuid(), start))).StatusCode);
    }

    [Fact]
    public async Task Staff_booking_reuses_an_existing_customer_by_email()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var email = $"same-{Guid.NewGuid():N}@salon.test";
        var customerId = await factory.InsertCustomerAsync("Pôvodné Meno", email);

        var response = await a.Client.PostAsJsonAsync("/api/appointments",
            StaffTestHelpers.StaffBody(serviceId, StaffTestHelpers.FutureMonday(10), email: email.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var customer = (await response.ReadJsonAsync()).GetProperty("customer");
        Assert.Equal(customerId, customer.GetProperty("id").GetGuid());
        Assert.Equal("Pôvodné Meno", customer.GetProperty("fullName").GetString());
        Assert.Equal("+421900111222", customer.GetProperty("phone").GetString()); // filled in, was missing
    }

    [Fact]
    public async Task Walk_in_within_the_public_lead_time_is_allowed_for_staff_only()
    {
        // Monday 2026-11-02 08:00 UTC = 09:00 local. Public earliest = 10:00; staff earliest = now.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
        using var fresh = await factory.CreateFreshHostAsync(clock: clock);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var worker = await fresh.CreateWorkerAsync(admin, "Aaa", serviceId);
        var day = new DateOnly(2026, 11, 2);
        var walkIn = TestSalon.LocalToUtc(day, 9, 30);

        using var anon = fresh.NewClient();
        await AssertProblemAsync(
            await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, walkIn)),
            HttpStatusCode.BadRequest, InvalidTime);
        var staff = await worker.Client.PostAsJsonAsync("/api/appointments", StaffTestHelpers.StaffBody(serviceId, walkIn));
        Assert.Equal(HttpStatusCode.Created, staff.StatusCode);

        // but not into the past
        await AssertProblemAsync(
            await worker.Client.PostAsJsonAsync("/api/appointments",
                StaffTestHelpers.StaffBody(serviceId, TestSalon.LocalToUtc(day, 8, 30))),
            HttpStatusCode.BadRequest, InvalidTime);
    }

    // ---------- reschedule / notes ----------

    [Fact]
    public async Task Reschedule_overlapping_its_own_old_interval_updates_ends_at()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        var before = (await factory.LoadAppointmentAsync(id)).UpdatedAt;

        var response = await a.Client.PatchJsonAsync($"/api/appointments/{id}", new { startsAt = start.AddMinutes(15) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(start.AddMinutes(15), body.GetProperty("startsAt").GetDateTime());
        Assert.Equal(start.AddMinutes(75), body.GetProperty("endsAt").GetDateTime());
        var row = await factory.LoadAppointmentAsync(id);
        Assert.Equal(start.AddMinutes(75), row.EndsAt);
        Assert.True(row.UpdatedAt > before);
    }

    [Fact]
    public async Task Reschedule_validates_taken_and_misaligned_slots()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        await factory.InsertAppointmentAsync(a.Id, serviceId, start.AddHours(4)); // 14:00-15:00

        await AssertProblemAsync(
            await a.Client.PatchJsonAsync($"/api/appointments/{id}", new { startsAt = start.AddMinutes(210) }),
            HttpStatusCode.Conflict, SlotTaken);
        await AssertProblemAsync(
            await a.Client.PatchJsonAsync($"/api/appointments/{id}", new { startsAt = start.AddMinutes(7) }),
            HttpStatusCode.BadRequest, InvalidTime);
        Assert.Equal(start, (await factory.LoadAppointmentAsync(id)).StartsAt);
    }

    [Fact]
    public async Task Reschedule_of_a_non_scheduled_appointment_is_409_but_staff_note_is_editable()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, MinutesAgo(120), status: AppointmentStatus.Completed);

        await AssertProblemAsync(
            await a.Client.PatchJsonAsync($"/api/appointments/{id}", new { startsAt = StaffTestHelpers.FutureMonday(10) }),
            HttpStatusCode.Conflict, WrongState);

        var note = await a.Client.PatchJsonAsync($"/api/appointments/{id}", new { staffNote = "po návšteve" });
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.Equal("po návšteve", (await note.ReadJsonAsync()).GetProperty("staffNote").GetString());
    }

    [Fact]
    public async Task Patch_semantics_absent_and_null_keep_empty_string_clears()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10);
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        var url = $"/api/appointments/{id}";

        await a.Client.PatchJsonAsync(url, new { staffNote = "A" });
        await a.Client.PatchRawAsync(url, "{}");
        Assert.Equal("A", (await factory.LoadAppointmentAsync(id)).StaffNote);
        await a.Client.PatchRawAsync(url, """{"staffNote":null,"startsAt":null,"employeeId":null}""");
        var kept = await factory.LoadAppointmentAsync(id);
        Assert.Equal("A", kept.StaffNote);
        Assert.Equal(start, kept.StartsAt);

        var cleared = await a.Client.PatchRawAsync(url, """{"staffNote":""}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.ReadJsonAsync()).GetProperty("staffNote").ValueKind);
        Assert.Null((await factory.LoadAppointmentAsync(id)).StaffNote);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.PatchJsonAsync(url, new { staffNote = new string('x', 2001) })).StatusCode);
    }

    [Fact]
    public async Task Admin_reassigns_to_an_eligible_employee_but_not_to_a_non_eligible_one()
    {
        var (admin, serviceId, a, b) = await SetupAsync();
        var outsider = await factory.CreateWorkerAsync(admin, "Ccc");
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, StaffTestHelpers.FutureMonday(10));

        await AssertProblemAsync(
            await admin.PatchJsonAsync($"/api/appointments/{id}", new { employeeId = outsider.Id }),
            HttpStatusCode.Conflict, SlotTaken);
        Assert.Equal(a.Id, (await factory.LoadAppointmentAsync(id)).EmployeeId);

        var response = await admin.PatchJsonAsync($"/api/appointments/{id}", new { employeeId = b.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(b.Id, (await response.ReadJsonAsync()).GetProperty("employee").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/appointments/{id}")).StatusCode);
    }

    // ---------- list / detail ----------

    [Fact]
    public async Task List_requires_from_and_to_and_limits_the_range()
    {
        var (_, _, a, _) = await SetupAsync();
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.GetAsync("/api/appointments")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.GetAsync($"/api/appointments?from={StaffTestHelpers.Iso(from)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.Client.GetAsync($"/api/appointments?to={StaffTestHelpers.Iso(from)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.ListAppointmentsAsync(from, from)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.ListAppointmentsAsync(from, from.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.ListAppointmentsAsync(from, from.AddDays(92).AddMinutes(1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.ListAppointmentsAsync(from, from.AddDays(92))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.ListAppointmentsAsync(from, from.AddDays(1), "&status=Nope")).StatusCode);
    }

    [Fact]
    public async Task List_uses_overlap_semantics_at_the_boundaries()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(10); // [10:00, 11:00) local
        var (id, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);

        async Task<bool> Listed(DateTime from, DateTime to) =>
            (await (await a.Client.ListAppointmentsAsync(from, to)).ReadJsonAsync()).Ids().Contains(id);

        Assert.False(await Listed(start.AddHours(1), start.AddHours(2)));   // from == ends_at
        Assert.False(await Listed(start.AddHours(-1), start));              // to == starts_at
        Assert.True(await Listed(start.AddMinutes(30), start.AddMinutes(45))); // started before from
        Assert.True(await Listed(start.AddMinutes(-1), start.AddMinutes(1)));
        Assert.True(await Listed(start.AddMinutes(59), start.AddMinutes(120)));
    }

    [Fact]
    public async Task List_filters_by_status_orders_by_start_and_includes_names()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var start = StaffTestHelpers.FutureMonday(9);
        var customerId = await factory.InsertCustomerAsync("Zoznamová Zákazníčka");
        var (later, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start.AddHours(3), customerId: customerId);
        var (earlier, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start);
        var (cancelled, _) = await factory.InsertAppointmentAsync(a.Id, serviceId, start, status: AppointmentStatus.Cancelled);

        var all = await (await a.Client.ListAppointmentsAsync(start.AddHours(-1), start.AddHours(8))).ReadJsonAsync();
        var ids = all.Ids();
        Assert.Equal(3, ids.Count);
        Assert.Equal(later, ids[^1]);
        var onlyCancelled = await (await a.Client.ListAppointmentsAsync(start.AddHours(-1), start.AddHours(8), "&status=Cancelled")).ReadJsonAsync();
        Assert.Equal([cancelled], onlyCancelled.Ids());
        var scheduled = await (await a.Client.ListAppointmentsAsync(start.AddHours(-1), start.AddHours(8), "&status=Scheduled")).ReadJsonAsync();
        Assert.Equal([earlier, later], scheduled.Ids());

        var item = all.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == later);
        Assert.Equal("Zoznamová Zákazníčka", item.GetProperty("customer").GetProperty("fullName").GetString());
        Assert.Equal(customerId, item.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal(a.Name, item.GetProperty("employee").GetProperty("fullName").GetString());
        var service = item.GetProperty("service");
        Assert.Equal(serviceId, service.GetProperty("id").GetGuid());
        Assert.Equal("Skin", service.GetProperty("category").GetString());
        Assert.Equal(60, service.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Staff", item.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Detail_contains_customer_notes_timestamps_and_payment()
    {
        var (_, serviceId, a, _) = await SetupAsync();
        var customerId = await factory.InsertCustomerAsync("Detailná Zákazníčka", phone: "+421911000111");
        var (id, _) = await factory.InsertAppointmentAsync(
            a.Id, serviceId, MinutesAgo(300), status: AppointmentStatus.Completed, customerId: customerId, paidAmount: 40m);

        var response = await a.Client.GetAsync($"/api/appointments/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var customer = body.GetProperty("customer");
        Assert.Equal("Detailná Zákazníčka", customer.GetProperty("fullName").GetString());
        Assert.Equal("+421911000111", customer.GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancelledAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("createdAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("updatedAt").ValueKind);
        Assert.Equal(40m, body.GetProperty("payment").GetProperty("amount").GetDecimal());
        Assert.Equal(a.Name, body.GetProperty("payment").GetProperty("recordedBy").GetProperty("fullName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/appointments/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_are_401()
    {
        using var anon = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/appointments?from=2030-01-01T00:00:00Z&to=2030-01-02T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/appointments/{Guid.NewGuid()}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/customers")).StatusCode);
    }
}
