using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

// Clock that the test moves forward explicitly (end-to-end flow: booking, then time passes, then completion).
internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

[Collection("api")]
public class StatsTests(ApiFactory factory)
{
    // Today (local) is Saturday 2026-10-17; October 2026 is CEST until 25 Oct, then CET.
    private static readonly FixedTimeProvider Oct17 = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));

    private static DateTime At(int year, int month, int day, int hour, int minute = 0) =>
        TestSalon.LocalToUtc(new DateOnly(year, month, day), hour, minute);

    private static string Period(DateOnly from, DateOnly to) => $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

    private static decimal? Dec(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Overview_counts_revenue_visits_no_shows_and_new_customers_for_the_period()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 40m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-Ov", serviceId);

        var regular = await fresh.InsertCustomerAsync(); // first visit 2026-09-20, visits again in the period: NOT new
        var discounted = await fresh.InsertCustomerAsync(); // first visit in the period, paid 25 instead of 40: new
        var newcomer = await fresh.InsertCustomerAsync(); // first visit in the period: new
        var noShowOnly = await fresh.InsertCustomerAsync(); // only NoShow: not new
        var cancelledOnly = await fresh.InsertCustomerAsync(); // only Cancelled: not new
        var earlier = await fresh.InsertCustomerAsync(); // first visit 2026-10-04 23:30 (before the period): not new
        var later = await fresh.InsertCustomerAsync(); // first visit 2026-10-12 (after the period): not new

        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 9, 20, 10), customerId: regular,
            status: AppointmentStatus.Completed, paidAmount: 40m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 6, 10), customerId: regular,
            status: AppointmentStatus.Completed, paidAmount: 40m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 6, 15), customerId: discounted,
            status: AppointmentStatus.Completed, paidAmount: 25m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 7, 10), customerId: newcomer,
            status: AppointmentStatus.Completed, paidAmount: 40m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 8, 10), customerId: noShowOnly,
            status: AppointmentStatus.NoShow);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 9, 10), customerId: cancelledOnly,
            status: AppointmentStatus.Cancelled);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 10, 10), status: AppointmentStatus.Scheduled);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 4, 23, 30), customerId: earlier,
            status: AppointmentStatus.Completed, paidAmount: 500m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 12, 10), customerId: later,
            status: AppointmentStatus.Completed, paidAmount: 999m);

        var body = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 10, 5), new(2026, 10, 11)));

        Assert.Equal(new DateOnly(2026, 10, 5), DateOnly.Parse(body.GetProperty("from").GetString()!));
        Assert.Equal(new DateOnly(2026, 10, 11), DateOnly.Parse(body.GetProperty("to").GetString()!));
        Assert.Equal(105m, body.GetProperty("revenue").GetDecimal());          // 40 + 25 + 40
        Assert.Equal(3, body.GetProperty("completedVisits").GetInt32());
        Assert.Equal(0.25m, Dec(body.GetProperty("noShowRate")));             // 1 / (3 + 1)
        Assert.Equal(2, body.GetProperty("newCustomers").GetInt32());        // discounted + newcomer
        Assert.Equal(35m, Dec(body.GetProperty("averageVisitValue")));       // 105 / 3
    }

    [Fact]
    public async Task Overview_of_empty_period_has_zero_totals_and_null_rates()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();

        var body = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 1, 1), new(2026, 1, 31)));

        Assert.Equal(0m, body.GetProperty("revenue").GetDecimal());
        Assert.Equal(0, body.GetProperty("completedVisits").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("noShowRate").ValueKind);
        Assert.Equal(0, body.GetProperty("newCustomers").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("averageVisitValue").ValueKind);
    }

    [Fact]
    public async Task Overview_rounds_average_visit_value_to_two_decimals_and_no_show_rate_to_four()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 10m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-Round", serviceId);

        // 3 completed (10 + 10 + 11 = 31) and 1 no-show: average 31 / 3 = 10.33, no-show rate 1 / 4 = 0.25.
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 19, 9), status: AppointmentStatus.Completed, paidAmount: 10m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 19, 10), status: AppointmentStatus.Completed, paidAmount: 10m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 19, 11), status: AppointmentStatus.Completed, paidAmount: 11m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 19, 12), status: AppointmentStatus.NoShow);

        var body = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 10, 19), new(2026, 10, 19)));

        Assert.Equal(10.33m, Dec(body.GetProperty("averageVisitValue")));
        Assert.Equal(0.25m, Dec(body.GetProperty("noShowRate")));
    }

    [Fact]
    public async Task Local_day_boundaries_put_appointments_into_the_local_day_in_both_dst_seasons()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 10m);
        var (early, _) = await admin.CreateEmployeeAsync("Stats-Bound-A", serviceId);
        var (midnight, _) = await admin.CreateEmployeeAsync("Stats-Bound-B", serviceId);

        // 2026-10-31 23:30 CET = 22:30 UTC: belongs to 31 Oct local time.
        await fresh.InsertAppointmentAsync(early, serviceId, At(2026, 10, 30, 23, 59), status: AppointmentStatus.Completed, paidAmount: 4m);
        await fresh.InsertAppointmentAsync(early, serviceId, At(2026, 10, 31, 23, 30), status: AppointmentStatus.Completed, paidAmount: 10m);
        await fresh.InsertAppointmentAsync(midnight, serviceId, At(2026, 10, 31, 0, 0), status: AppointmentStatus.Completed, paidAmount: 1m);
        await fresh.InsertAppointmentAsync(midnight, serviceId, At(2026, 11, 1, 0, 0), status: AppointmentStatus.Completed, paidAmount: 2m);
        // 2026-03-31 23:30 CEST = 21:30 UTC.
        await fresh.InsertAppointmentAsync(early, serviceId, At(2026, 3, 31, 23, 30), status: AppointmentStatus.Completed, paidAmount: 20m);

        // A single day: includes 00:00 local, excludes the next 00:00 local (the 30 Oct 23:59 and 1 Nov 00:00 visits are outside).
        var oct31 = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 10, 31), new(2026, 10, 31)));
        Assert.Equal(11m, oct31.GetProperty("revenue").GetDecimal());   // 10 (23:30) + 1 (00:00)
        Assert.Equal(2, oct31.GetProperty("completedVisits").GetInt32());

        var march = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 3, 31), new(2026, 3, 31)));
        Assert.Equal(20m, march.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, march.GetProperty("completedVisits").GetInt32());

        var november = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(new(2026, 11, 1), new(2026, 11, 1)));
        Assert.Equal(2m, november.GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task Monthly_returns_continuous_months_oldest_first_with_zero_months()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 40m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-Month", serviceId);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 8, 15, 10), status: AppointmentStatus.Completed, paidAmount: 50m);
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 6, 10), status: AppointmentStatus.Completed, paidAmount: 30m);

        var months = (await GetOkAsync(admin, "/api/admin/stats/monthly?months=3")).EnumerateArray().ToList();

        Assert.Equal(3, months.Count);
        Assert.Equal("2026-08", months[0].GetProperty("month").GetString());
        Assert.Equal(50m, months[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(1, months[0].GetProperty("completedVisits").GetInt32());
        Assert.Equal(1, months[0].GetProperty("newCustomers").GetInt32());
        Assert.Equal("2026-09", months[1].GetProperty("month").GetString());
        Assert.Equal(0m, months[1].GetProperty("revenue").GetDecimal());
        Assert.Equal(0, months[1].GetProperty("completedVisits").GetInt32());
        Assert.Equal(0, months[1].GetProperty("newCustomers").GetInt32());
        Assert.Equal("2026-10", months[2].GetProperty("month").GetString());
        Assert.Equal(30m, months[2].GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task Monthly_buckets_by_local_month_across_the_october_dst_change()
    {
        // 17 Nov 2026 (local): the two months are October and November.
        using var fresh = await factory.CreateFreshHostAsync(clock: new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero)));
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(30, 10m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-Dst", serviceId);

        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 25, 12), status: AppointmentStatus.Completed, paidAmount: 5m);
        // 31 Oct 23:30 CET (22:30 UTC) -> October.
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 10, 31, 23, 30), status: AppointmentStatus.Completed, paidAmount: 10m);
        // 1 Nov 00:30 CET (31 Oct 23:30 UTC) -> November, although its UTC date is still 31 Oct.
        await fresh.InsertAppointmentAsync(employeeId, serviceId, At(2026, 11, 1, 0, 30), status: AppointmentStatus.Completed, paidAmount: 20m);

        var months = (await GetOkAsync(admin, "/api/admin/stats/monthly?months=2")).EnumerateArray().ToList();

        Assert.Equal("2026-10", months[0].GetProperty("month").GetString());
        Assert.Equal(15m, months[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(2, months[0].GetProperty("completedVisits").GetInt32());
        Assert.Equal("2026-11", months[1].GetProperty("month").GetString());
        Assert.Equal(20m, months[1].GetProperty("revenue").GetDecimal());
        Assert.Equal(1, months[1].GetProperty("completedVisits").GetInt32());
    }

    [Theory]
    [InlineData("months=0")]
    [InlineData("months=25")]
    [InlineData("months=abc")]
    public async Task Monthly_rejects_months_outside_1_to_24(string query)
    {
        using var admin = (await factory.LoginAdminAsync()).Client;

        var response = await admin.GetAsync("/api/admin/stats/monthly?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Services_are_ordered_by_revenue_and_include_inactive_services_with_visits()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (alpha, _) = await admin.CreateServiceAsync(60, 40m);
        var (inactive, _) = await admin.CreateServiceAsync(60, 60m);
        (await admin.PostAsync($"/api/admin/services/{inactive}/deactivate", null)).EnsureSuccessStatusCode();
        var (frequent, _) = await admin.CreateServiceAsync(60, 10m);
        var (unused, _) = await admin.CreateServiceAsync(60, 10m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-Svc", alpha);

        await fresh.InsertAppointmentAsync(employeeId, alpha, At(2026, 10, 5, 10), status: AppointmentStatus.Completed, paidAmount: 40m);
        await fresh.InsertAppointmentAsync(employeeId, alpha, At(2026, 10, 5, 11), status: AppointmentStatus.Completed, paidAmount: 40m);
        await fresh.InsertAppointmentAsync(employeeId, inactive, At(2026, 10, 6, 10), status: AppointmentStatus.Completed, paidAmount: 150m);
        await fresh.InsertAppointmentAsync(employeeId, frequent, At(2026, 10, 7, 10), status: AppointmentStatus.Completed, paidAmount: 10m);
        await fresh.InsertAppointmentAsync(employeeId, frequent, At(2026, 10, 7, 11), status: AppointmentStatus.Completed, paidAmount: 10m);
        await fresh.InsertAppointmentAsync(employeeId, frequent, At(2026, 10, 7, 12), status: AppointmentStatus.Completed, paidAmount: 10m);

        var list = (await GetOkAsync(admin, "/api/admin/stats/services?" + Period(new(2026, 10, 1), new(2026, 10, 31))))
            .EnumerateArray().ToList();

        // Inactive service with the highest revenue first; the unused service is missing.
        Assert.Equal(new[] { inactive, alpha, frequent }, list.Select(s => s.GetProperty("serviceId").GetGuid()));
        Assert.Equal(150m, list[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(1, list[0].GetProperty("completedVisits").GetInt32());
        Assert.Equal(80m, list[1].GetProperty("revenue").GetDecimal());
        Assert.Equal(2, list[1].GetProperty("completedVisits").GetInt32());
        Assert.Equal(30m, list[2].GetProperty("revenue").GetDecimal());
        Assert.Equal(3, list[2].GetProperty("completedVisits").GetInt32());
        Assert.Equal("Skin", list[1].GetProperty("category").GetString());
        Assert.DoesNotContain(list, s => s.GetProperty("serviceId").GetGuid() == unused);
    }

    [Fact]
    public async Task Employees_report_booked_and_opening_minutes_utilization_and_membership()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 50m);
        var (busy, _) = await admin.CreateEmployeeAsync("Stats-E1", serviceId);
        var (idle, _) = await admin.CreateEmployeeAsync("Stats-E2", serviceId);
        var (noService, _) = await admin.CreateEmployeeAsync("Stats-E3");
        var (deactivated, _) = await admin.CreateEmployeeAsync("Stats-E4", serviceId);
        (await admin.PostAsync($"/api/admin/users/{deactivated}/deactivate", null)).EnsureSuccessStatusCode();

        // busy: Scheduled 60 + Completed 90 + NoShow 60 count; Cancelled 60 does not. 60 + 90 + 60 = 210 minutes.
        await fresh.InsertAppointmentAsync(busy, serviceId, At(2026, 10, 5, 9), status: AppointmentStatus.Scheduled);
        await fresh.InsertAppointmentAsync(busy, serviceId, At(2026, 10, 6, 10), durationMinutes: 90,
            status: AppointmentStatus.Completed, paidAmount: 50m);
        await fresh.InsertAppointmentAsync(busy, serviceId, At(2026, 10, 7, 9), status: AppointmentStatus.NoShow);
        await fresh.InsertAppointmentAsync(busy, serviceId, At(2026, 10, 8, 9), status: AppointmentStatus.Cancelled);
        // noService has no services but an appointment in the period: listed.
        await fresh.InsertAppointmentAsync(noService, serviceId, At(2026, 10, 9, 14), status: AppointmentStatus.Completed, paidAmount: 30m);

        // Monday 2026-10-05 .. Sunday 2026-10-11: Mon-Fri 600 min each, Saturday 300, Sunday closed = 3300.
        var list = (await GetOkAsync(admin, "/api/admin/stats/employees?" + Period(new(2026, 10, 5), new(2026, 10, 11))))
            .EnumerateArray().ToList();
        var ids = new[] { busy, idle, noService, deactivated };
        var ours = list.Where(e => ids.Contains(e.GetProperty("employeeId").GetGuid())).ToList();

        Assert.Equal(new[] { busy, idle, noService }, ours.Select(e => e.GetProperty("employeeId").GetGuid()));
        Assert.All(list, e => Assert.Equal(3300, e.GetProperty("openingMinutes").GetInt32()));

        var busyRow = ours[0];
        Assert.Equal(1, busyRow.GetProperty("completedVisits").GetInt32());
        Assert.Equal(50m, busyRow.GetProperty("revenue").GetDecimal());
        Assert.Equal(210, busyRow.GetProperty("bookedMinutes").GetInt32());
        Assert.Equal(0.0636m, Dec(busyRow.GetProperty("utilization"))); // 210 / 3300 = 0.06363... -> 0.0636

        var idleRow = ours[1];
        Assert.Equal(0, idleRow.GetProperty("bookedMinutes").GetInt32());
        Assert.Equal(0m, Dec(idleRow.GetProperty("utilization")));

        var noServiceRow = ours[2];
        Assert.Equal(30m, noServiceRow.GetProperty("revenue").GetDecimal());
        Assert.Equal(60, noServiceRow.GetProperty("bookedMinutes").GetInt32());
        Assert.Equal(0.0182m, Dec(noServiceRow.GetProperty("utilization"))); // 60 / 3300 = 0.01818... -> 0.0182
    }

    [Fact]
    public async Task Validation_rejects_inverted_periods_and_periods_over_731_days()
    {
        using var admin = (await factory.LoginAdminAsync()).Client;
        var start = new DateOnly(2026, 1, 1);

        var inverted = await admin.GetAsync("/api/admin/stats/overview?" + Period(new(2026, 10, 12), new(2026, 10, 11)));
        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);

        var maxAllowed = await admin.GetAsync("/api/admin/stats/overview?" + Period(start, start.AddDays(731)));
        Assert.Equal(HttpStatusCode.OK, maxAllowed.StatusCode);

        var tooLong = await admin.GetAsync("/api/admin/stats/overview?" + Period(start, start.AddDays(732)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("application/problem+json", tooLong.Content.Headers.ContentType?.MediaType);

        var malformed = await admin.GetAsync("/api/admin/stats/services?from=2026-13-01&to=2026-12-01");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Without_parameters_the_period_is_first_of_current_local_month_to_today()
    {
        using var fresh = await factory.CreateFreshHostAsync(clock: Oct17);
        var (admin, _) = await fresh.LoginAdminAsync();

        var body = await GetOkAsync(admin, "/api/admin/stats/overview");

        Assert.Equal("2026-10-01", body.GetProperty("from").GetString());
        Assert.Equal("2026-10-17", body.GetProperty("to").GetString());
    }

    [Fact]
    public async Task All_stats_endpoints_require_the_admin_role()
    {
        var urls = new[]
        {
            "/api/admin/stats/overview",
            "/api/admin/stats/monthly?months=12",
            "/api/admin/stats/services",
            "/api/admin/stats/employees",
        };
        using var anonymous = factory.NewClient();
        var (user, _) = await factory.LoginAsync(AuthTestHelpers.WorkerEmail, ApiFactory.UserPassword);
        using var userClient = user;

        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync(url)).StatusCode);
        }

        var (admin, _) = await factory.LoginAdminAsync();
        using var adminClient = admin;
        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(url)).StatusCode);
        }
    }

    // PLAN "Hotovo, keď": booking -> time passes -> completion with a custom amount -> overview of that day.
    [Fact]
    public async Task Booking_then_completion_shows_up_in_the_overview_of_that_day()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero)); // 08:00 local
        using var fresh = await factory.CreateFreshHostAsync(clock: clock);
        var (admin, _) = await fresh.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync(60, 40m);
        var (employeeId, _) = await admin.CreateEmployeeAsync("Stats-E2E", serviceId);
        var day = new DateOnly(2026, 11, 2);

        using var anonymous = fresh.NewClient();
        var startsAt = TestSalon.LocalToUtc(day, 10);
        var booking = await anonymous.PostBookingAsync(
            BookingTestHelpers.BookingBody(serviceId, startsAt, employeeId: employeeId, email: "stats-e2e@salon.test"));
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
        var appointmentId = (await booking.ReadJsonAsync()).GetProperty("appointmentId").GetGuid();

        // The admin session has a 2 h idle timeout: touch it on the way, then move past the start (10:00 local = 09:00 UTC).
        clock.Now = new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/stats/overview")).StatusCode);
        clock.Now = new DateTimeOffset(2026, 11, 2, 9, 5, 0, TimeSpan.Zero);
        var complete = await admin.PostAsJsonAsync($"/api/appointments/{appointmentId}/complete",
            new { paymentMethod = "Card", amount = 35m });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var body = await GetOkAsync(admin, "/api/admin/stats/overview?" + Period(day, day));
        Assert.Equal(35m, body.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, body.GetProperty("completedVisits").GetInt32());
        Assert.Equal(1, body.GetProperty("newCustomers").GetInt32());
        Assert.Equal(0m, Dec(body.GetProperty("noShowRate")));
        Assert.Equal(35m, Dec(body.GetProperty("averageVisitValue")));
    }
}
