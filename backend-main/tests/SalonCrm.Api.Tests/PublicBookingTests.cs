using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class PublicBookingTests(ApiFactory factory)
{
    private const string SlotTaken = "Vybraný termín už nie je voľný.";
    private const string InvalidTime = "Neplatný čas termínu.";

    private static DateTime Monday10 => TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Monday), 10);

    // ---------- catalogue ----------

    [Fact]
    public async Task Public_services_lists_only_active_services_with_expected_fields()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (activeId, activeName) = await admin.CreateServiceAsync();
        var (inactiveId, _) = await admin.CreateServiceAsync();
        (await admin.PostAsync($"/api/admin/services/{inactiveId}/deactivate", null)).EnsureSuccessStatusCode();
        using var anon = factory.NewClient();

        var response = await anon.GetAsync("/api/public/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        var item = list.Single(s => s.GetProperty("id").GetGuid() == activeId);
        Assert.Equal(activeName, item.GetProperty("name").GetString());
        Assert.Equal("Skin", item.GetProperty("category").GetString());
        Assert.Equal(60, item.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(40m, item.GetProperty("price").GetDecimal());
        Assert.False(item.TryGetProperty("isActive", out _));
        Assert.DoesNotContain(list, s => s.GetProperty("id").GetGuid() == inactiveId);

        var keys = list.Select(s => (s.GetProperty("category").GetString()!, s.GetProperty("name").GetString()!)).ToList();
        Assert.Equal(keys.Select(k => k.Item1).OrderBy(c => c, StringComparer.Ordinal).ToList(), keys.Select(k => k.Item1).ToList());
    }

    [Fact]
    public async Task Service_employees_lists_only_active_eligible_employees_ordered_by_name()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var (otherServiceId, _) = await admin.CreateServiceAsync();
        var b = await admin.CreateEmployeeAsync("Bbb", serviceId);
        var a = await admin.CreateEmployeeAsync("Aaa", serviceId);
        await admin.CreateEmployeeAsync("Ccc", otherServiceId);
        var inactive = await admin.CreateEmployeeAsync("Ddd", serviceId);
        (await admin.PostAsync($"/api/admin/users/{inactive.Id}/deactivate", null)).EnsureSuccessStatusCode();
        using var anon = factory.NewClient();

        var response = await anon.GetAsync($"/api/public/services/{serviceId}/employees");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var employees = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal([a.Id, b.Id], employees.Select(e => e.GetProperty("id").GetGuid()).ToList());
        Assert.Equal(a.Name, employees[0].GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Service_employees_is_404_for_unknown_or_inactive_service()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        (await admin.PostAsync($"/api/admin/services/{serviceId}/deactivate", null)).EnsureSuccessStatusCode();
        using var anon = factory.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/services/{serviceId}/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/services/{Guid.NewGuid()}/employees")).StatusCode);
    }

    // ---------- availability ----------

    [Fact]
    public async Task Scheduled_appointment_blocks_the_slot_for_that_employee_only()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var e1 = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var e2 = await admin.CreateEmployeeAsync("Bbb", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Tuesday);
        await factory.SeedAppointmentAsync(e1.Id, serviceId, TestSalon.LocalToUtc(date, 10));
        using var anon = factory.NewClient();

        var slots = await anon.AvailabilityAsync(serviceId, date);

        Assert.Equal([e2.Id], slots.EmployeeIdsAt(TestSalon.LocalToUtc(date, 10)));
        Assert.Equal([e2.Id], slots.EmployeeIdsAt(TestSalon.LocalToUtc(date, 9, 15))); // overlaps the booked hour
        Assert.Equal([e1.Id, e2.Id], slots.EmployeeIdsAt(TestSalon.LocalToUtc(date, 9))); // back-to-back is fine
        Assert.Equal([e1.Id, e2.Id], slots.EmployeeIdsAt(TestSalon.LocalToUtc(date, 11)));
    }

    [Fact]
    public async Task Slot_with_no_free_employee_is_not_listed()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var e1 = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Tuesday);
        await factory.SeedAppointmentAsync(e1.Id, serviceId, TestSalon.LocalToUtc(date, 10));
        using var anon = factory.NewClient();

        var slots = await anon.AvailabilityAsync(serviceId, date);

        Assert.DoesNotContain(slots.EnumerateArray(), s => s.GetProperty("startsAt").GetDateTime() == TestSalon.LocalToUtc(date, 10));
        Assert.NotEmpty(slots.EnumerateArray());
    }

    [Fact]
    public async Task Cancelled_appointment_does_not_block()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var e1 = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Wednesday);
        await factory.SeedAppointmentAsync(e1.Id, serviceId, TestSalon.LocalToUtc(date, 10), status: AppointmentStatus.Cancelled);
        using var anon = factory.NewClient();

        var slots = await anon.AvailabilityAsync(serviceId, date);

        Assert.Equal([e1.Id], slots.EmployeeIdsAt(TestSalon.LocalToUtc(date, 10)));
    }

    [Fact]
    public async Task Employees_without_the_service_or_inactive_are_never_offered_and_employee_filter_works()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var (otherServiceId, _) = await admin.CreateServiceAsync();
        var e1 = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var e2 = await admin.CreateEmployeeAsync("Bbb", serviceId);
        var other = await admin.CreateEmployeeAsync("Ccc", otherServiceId);
        var inactive = await admin.CreateEmployeeAsync("Ddd", serviceId);
        (await admin.PostAsync($"/api/admin/users/{inactive.Id}/deactivate", null)).EnsureSuccessStatusCode();
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Thursday);
        var start = TestSalon.LocalToUtc(date, 14);
        using var anon = factory.NewClient();

        var all = await anon.AvailabilityAsync(serviceId, date);
        var onlyE2 = await anon.AvailabilityAsync(serviceId, date, e2.Id);
        var notEligible = await anon.AvailabilityAsync(serviceId, date, other.Id);

        Assert.Equal([e1.Id, e2.Id], all.EmployeeIdsAt(start));
        Assert.Equal([e2.Id], onlyE2.EmployeeIdsAt(start));
        Assert.Empty(notEligible.EnumerateArray());
    }

    [Fact]
    public async Task Closed_day_past_and_too_far_dates_return_empty_list()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var today = DateOnly.FromDateTime(TestSalon.ToLocal(DateTime.UtcNow));
        using var anon = factory.NewClient();

        Assert.Empty((await anon.AvailabilityAsync(serviceId, BookingTestHelpers.NextWeekday(DayOfWeek.Sunday))).EnumerateArray());
        Assert.Empty((await anon.AvailabilityAsync(serviceId, today.AddDays(-1))).EnumerateArray());
        Assert.Empty((await anon.AvailabilityAsync(serviceId, today.AddDays(-30))).EnumerateArray());
        Assert.Empty((await anon.AvailabilityAsync(serviceId, BookingTestHelpers.NextWeekday(DayOfWeek.Monday, 70))).EnumerateArray());
        Assert.NotEmpty((await anon.AvailabilityAsync(serviceId, BookingTestHelpers.NextWeekday(DayOfWeek.Monday, 7))).EnumerateArray());
    }

    [Fact]
    public async Task Availability_is_404_for_unknown_or_inactive_service_and_400_for_bad_params()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        (await admin.PostAsync($"/api/admin/services/{serviceId}/deactivate", null)).EnsureSuccessStatusCode();
        using var anon = factory.NewClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/public/availability?serviceId={serviceId}&date=2030-01-07")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/public/availability?serviceId={Guid.NewGuid()}&date=2030-01-07")).StatusCode);

        foreach (var url in new[]
        {
            "/api/public/availability",
            $"/api/public/availability?serviceId={serviceId}",
            "/api/public/availability?date=2030-01-07",
            $"/api/public/availability?serviceId={serviceId}&date=not-a-date",
            $"/api/public/availability?serviceId={serviceId}&date=2030-13-45",
            $"/api/public/availability?serviceId=nope&date=2030-01-07",
            $"/api/public/availability?serviceId={serviceId}&date=2030-01-07&employeeId=nope",
        })
        {
            var response = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    // ---------- booking ----------

    [Fact]
    public async Task Booking_creates_customer_and_appointment_and_returns_201()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, serviceName) = await admin.CreateServiceAsync(90, 55.5m);
        var emp = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = Monday10;
        var email = $"  Anna.Test-{Guid.NewGuid():N}@Salon.Test ";
        using var anon = factory.NewClient();
        var before = DateTime.UtcNow.AddSeconds(-5);

        var response = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(
            serviceId, start, emp.Id, email, "Anna Testová", "+421900111222"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(start, body.GetProperty("startsAt").GetDateTime());
        Assert.Equal(serviceName, body.GetProperty("serviceName").GetString());
        Assert.Equal(emp.Name, body.GetProperty("employeeName").GetString());
        Assert.Equal(55.5m, body.GetProperty("price").GetDecimal());
        var appointmentId = body.GetProperty("appointmentId").GetGuid();

        await factory.WithDbAsync(async db =>
        {
            var a = await db.Appointments.Include(x => x.Customer).SingleAsync(x => x.Id == appointmentId);
            Assert.Equal(AppointmentStatus.Scheduled, a.Status);
            Assert.Equal(AppointmentSource.Online, a.Source);
            Assert.Equal(55.5m, a.Price);
            Assert.Equal(start, a.StartsAt);
            Assert.Equal(start.AddMinutes(90), a.EndsAt);
            Assert.Equal(emp.Id, a.EmployeeId);
            Assert.Equal("poznámka", a.CustomerNote);
            Assert.Equal(email.Trim(), a.Customer.Email);
            Assert.Equal(email.Trim().ToLowerInvariant(), a.Customer.EmailNormalized);
            Assert.Equal("Anna Testová", a.Customer.FullName);
            Assert.Equal("+421900111222", a.Customer.Phone);
            Assert.True(a.Customer.GdprConsentAt >= before);
        });

        // The booked slot is gone afterwards.
        var slots = await anon.AvailabilityAsync(serviceId, BookingTestHelpers.NextWeekday(DayOfWeek.Monday));
        Assert.Empty(slots.EmployeeIdsAt(start));
    }

    [Fact]
    public async Task Booking_for_existing_customer_reuses_record_keeps_name_and_fills_missing_phone_only()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var emp = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Friday);
        var email = $"returning-{Guid.NewGuid():N}@salon.test";
        using var anon = factory.NewClient();

        var first = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(
            serviceId, TestSalon.LocalToUtc(date, 9), emp.Id, email, "Pôvodné Meno"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstAppointment = (await first.ReadJsonAsync()).GetProperty("appointmentId").GetGuid();
        var (customerId, consentAt) = await factory.WithDbAsync(async db =>
        {
            var c = await db.Customers.AsNoTracking().SingleAsync(c => c.EmailNormalized == email);
            Assert.Null(c.Phone);
            return (c.Id, c.GdprConsentAt);
        });

        var second = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(
            serviceId, TestSalon.LocalToUtc(date, 11), emp.Id, "  " + email.ToUpperInvariant() + " ", "Iné Meno", "+421911000111"));
        var third = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(
            serviceId, TestSalon.LocalToUtc(date, 13), emp.Id, email, "Ešte Iné", "+421999999999"));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Customers.CountAsync(c => c.EmailNormalized == email));
            var c = await db.Customers.AsNoTracking().SingleAsync(c => c.EmailNormalized == email);
            Assert.Equal(customerId, c.Id);
            Assert.Equal("Pôvodné Meno", c.FullName);
            Assert.Equal("+421911000111", c.Phone); // filled by the second booking, not overwritten by the third
            Assert.Equal(consentAt, c.GdprConsentAt);
            Assert.Equal(3, await db.Appointments.CountAsync(a => a.CustomerId == customerId));
            Assert.Equal(customerId, (await db.Appointments.SingleAsync(a => a.Id == firstAppointment)).CustomerId);
        });
    }

    [Fact]
    public async Task Booking_without_gdpr_consent_is_400_and_creates_nothing()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = Monday10;
        var email = $"nogdpr-{Guid.NewGuid():N}@salon.test";
        using var anon = factory.NewClient();

        var denied = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start, email: email, gdprConsent: false));
        var missing = await anon.PostAsJsonAsync("/api/public/bookings", new
        {
            serviceId, startsAt = start, fullName = "Anna", email,
        });

        foreach (var response in new[] { denied, missing })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.ReadJsonAsync();
            Assert.Equal("Na rezerváciu je potrebný súhlas so spracovaním osobných údajov.", body.GetProperty("detail").GetString());
        }

        await factory.WithDbAsync(async db => Assert.False(await db.Customers.AnyAsync(c => c.EmailNormalized == email)));
    }

    [Fact]
    public async Task Booking_with_missing_or_malformed_fields_is_400()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = Monday10;
        using var anon = factory.NewClient();

        var bodies = new object[]
        {
            new { startsAt = start, fullName = "A", email = "a@b.sk", gdprConsent = true },
            new { serviceId, fullName = "A", email = "a@b.sk", gdprConsent = true },
            new { serviceId, startsAt = start, email = "a@b.sk", gdprConsent = true },
            new { serviceId, startsAt = start, fullName = "A", gdprConsent = true },
            new { serviceId, startsAt = start, fullName = "A", email = "not-an-email", gdprConsent = true },
            new { serviceId, startsAt = start, fullName = new string('x', 201), email = "a@b.sk", gdprConsent = true },
        };

        foreach (var body in bodies)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/public/bookings", body)).StatusCode);
        }
    }

    [Fact]
    public async Task Taken_slot_is_409_with_slovak_detail_for_chosen_and_for_any_employee()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var emp = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = Monday10;
        using var anon = factory.NewClient();
        Assert.Equal(HttpStatusCode.Created, (await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start, emp.Id))).StatusCode);

        var chosen = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start, emp.Id));
        var anyone = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start));
        var overlapping = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start.AddMinutes(30)));

        foreach (var response in new[] { chosen, anyone, overlapping })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(SlotTaken, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Chosen_employee_who_cannot_do_the_service_gets_409()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var (otherServiceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var other = await admin.CreateEmployeeAsync("Bbb", otherServiceId);
        using var anon = factory.NewClient();

        var response = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, Monday10, other.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Parallel_bookings_for_the_same_employee_and_slot_yield_exactly_one_201()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var emp = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Monday, 14), 15);
        var email = $"race-{Guid.NewGuid():N}@salon.test"; // same NEW customer in every request
        using var anon = factory.NewClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, start, emp.Id, email))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal(SlotTaken, (await loser.ReadJsonAsync()).GetProperty("detail").GetString());
        }

        await factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Appointments.CountAsync(a => a.EmployeeId == emp.Id));
            Assert.Equal(1, await db.Customers.CountAsync(c => c.EmailNormalized == email));
        });
    }

    [Fact]
    public async Task Parallel_bookings_of_the_same_new_customer_in_different_slots_all_succeed_with_one_customer()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var emp = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Tuesday, 14);
        var email = $"race2-{Guid.NewGuid():N}@salon.test";
        using var anon = factory.NewClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(date, 9 + i), emp.Id, email))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Customers.CountAsync(c => c.EmailNormalized == email));
            Assert.Equal(5, await db.Appointments.CountAsync(a => a.EmployeeId == emp.Id));
        });
    }

    [Fact]
    public async Task Anyone_picks_the_free_employee_with_fewer_appointments_that_day()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var a = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var b = await admin.CreateEmployeeAsync("Bbb", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Wednesday, 14);
        await factory.SeedAppointmentAsync(a.Id, serviceId, TestSalon.LocalToUtc(date, 9));
        await factory.SeedAppointmentAsync(a.Id, serviceId, TestSalon.LocalToUtc(date, 11));
        await factory.SeedAppointmentAsync(b.Id, serviceId, TestSalon.LocalToUtc(date, 9));
        await factory.SeedAppointmentAsync(a.Id, serviceId, TestSalon.LocalToUtc(date, 16), status: AppointmentStatus.Cancelled);
        using var anon = factory.NewClient();

        var response = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(date, 14)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(b.Name, (await response.ReadJsonAsync()).GetProperty("employeeName").GetString());
    }

    [Fact]
    public async Task Anyone_ties_are_broken_alphabetically_and_only_free_employees_are_considered()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        var a = await admin.CreateEmployeeAsync("Aaa", serviceId);
        var b = await admin.CreateEmployeeAsync("Bbb", serviceId);
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Thursday, 14);
        using var anon = factory.NewClient();

        var tie = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(date, 14)));
        Assert.Equal(HttpStatusCode.Created, tie.StatusCode);
        Assert.Equal(a.Name, (await tie.ReadJsonAsync()).GetProperty("employeeName").GetString());

        // Same slot again: A is busy now, B is the only free one even though A has fewer... (A has 1, B has 0).
        var next = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(date, 14)));
        Assert.Equal(b.Name, (await next.ReadJsonAsync()).GetProperty("employeeName").GetString());

        // Both busy at that slot now.
        var none = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(date, 14)));
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
    }

    [Fact]
    public async Task Invalid_times_are_400_neplatny_cas()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var monday = BookingTestHelpers.NextWeekday(DayOfWeek.Monday);
        var today = DateOnly.FromDateTime(TestSalon.ToLocal(DateTime.UtcNow));
        using var anon = factory.NewClient();

        var times = new[]
        {
            TestSalon.LocalToUtc(monday, 10, 7), // off the grid
            TestSalon.LocalToUtc(monday, 10).AddSeconds(30),
            TestSalon.LocalToUtc(monday, 7, 45), // before opening
            TestSalon.LocalToUtc(monday, 17, 15), // ends after closing
            TestSalon.LocalToUtc(monday, 18), // at closing
            TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Sunday), 10), // closed
            TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Saturday), 12, 15), // Saturday ends at 13
            TestSalon.LocalToUtc(today.AddDays(-7), 10), // past
            TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Monday, 70), 10), // > 60 days
            new DateTime(2000, 1, 3, 10, 0, 0, DateTimeKind.Utc),
        };

        foreach (var t in times)
        {
            var response = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, t));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidTime, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Booking_accepts_start_time_with_an_offset()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        var start = TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Friday, 21), 10);
        var offset = TestSalon.Zone.GetUtcOffset(start);
        var startsAt = new DateTimeOffset(DateTime.SpecifyKind(start + offset, DateTimeKind.Unspecified), offset).ToString("O");
        using var anon = factory.NewClient();

        var response = await anon.PostAsJsonAsync("/api/public/bookings", new
        {
            serviceId, startsAt, fullName = "Anna", email = $"offset-{Guid.NewGuid():N}@salon.test", gdprConsent = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(start, (await response.ReadJsonAsync()).GetProperty("startsAt").GetDateTime());
    }

    [Fact]
    public async Task Booking_inactive_or_unknown_service_is_404()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        (await admin.PostAsync($"/api/admin/services/{serviceId}/deactivate", null)).EnsureSuccessStatusCode();
        using var anon = factory.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, Monday10))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostBookingAsync(BookingTestHelpers.BookingBody(Guid.NewGuid(), Monday10))).StatusCode);
    }

    [Fact]
    public async Task Price_change_and_deactivation_do_not_alter_existing_appointments()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (serviceId, name) = await admin.CreateServiceAsync(60, 40m);
        await admin.CreateEmployeeAsync("Aaa", serviceId);
        using var anon = factory.NewClient();
        var booking = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, Monday10));
        var appointmentId = (await booking.ReadJsonAsync()).GetProperty("appointmentId").GetGuid();

        var update = await admin.PutAsJsonAsync($"/api/admin/services/{serviceId}", new
        {
            name, category = "Skin", durationMinutes = 60, price = 99.9m,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        await factory.WithDbAsync(async db =>
            Assert.Equal(40m, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId)).Price));
    }

    // ---------- clock-dependent (fresh host, fixed time) ----------

    [Fact]
    public async Task Booking_within_lead_time_is_400_and_first_allowed_slot_is_201()
    {
        // Monday 2026-11-02 08:00 UTC = 09:00 local (CET). Public earliest start = 10:00 local.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
        using var fresh = await factory.CreateFreshHostAsync(clock: clock);
        using var anon = fresh.NewClient();
        var services = await (await anon.GetAsync("/api/public/services")).ReadJsonAsync();
        var serviceId = services.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "Hĺbkové čistenie pleti").GetProperty("id").GetGuid();
        var day = new DateOnly(2026, 11, 2);

        foreach (var (hour, minute) in new[] { (8, 0), (9, 0), (9, 45) })
        {
            var tooSoon = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(day, hour, minute)));
            Assert.Equal(HttpStatusCode.BadRequest, tooSoon.StatusCode);
            Assert.Equal(InvalidTime, (await tooSoon.ReadJsonAsync()).GetProperty("detail").GetString());
        }

        var slots = await anon.AvailabilityAsync(serviceId, day);
        Assert.Equal(TestSalon.LocalToUtc(day, 10), slots.EnumerateArray().First().GetProperty("startsAt").GetDateTime());

        var ok = await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(day, 10)));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Booking_window_ends_after_max_days_ahead_on_fixed_clock()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
        using var fresh = await factory.CreateFreshHostAsync(clock: clock);
        using var anon = fresh.NewClient();
        var services = await (await anon.GetAsync("/api/public/services")).ReadJsonAsync();
        var serviceId = services.EnumerateArray().First().GetProperty("id").GetGuid();
        var lastDay = new DateOnly(2026, 11, 2).AddDays(60); // Friday 2027-01-01

        Assert.Equal(DayOfWeek.Friday, lastDay.DayOfWeek);
        Assert.NotEmpty((await anon.AvailabilityAsync(serviceId, lastDay)).EnumerateArray());
        Assert.Empty((await anon.AvailabilityAsync(serviceId, lastDay.AddDays(3))).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anon.PostBookingAsync(BookingTestHelpers.BookingBody(serviceId, TestSalon.LocalToUtc(lastDay.AddDays(3), 10)))).StatusCode);
    }

    [Fact]
    public async Task Booking_rate_limit_returns_429_problem_with_slovak_detail()
    {
        using var fresh = await factory.CreateFreshHostAsync(
            new Dictionary<string, string?> { ["RateLimiting:BookingPermitLimit"] = "2" });
        using var anon = fresh.NewClient();
        var body = BookingTestHelpers.BookingBody(Guid.NewGuid(), Monday10); // 404 responses still consume permits

        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostBookingAsync(body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostBookingAsync(body)).StatusCode);
        var limited = await anon.PostBookingAsync(body);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        var problem = await limited.ReadJsonAsync();
        Assert.Equal("https://tools.ietf.org/html/rfc6585#section-4", problem.GetProperty("type").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("Príliš veľa rezervácií. Skúste to znova neskôr.", problem.GetProperty("detail").GetString());
    }
}
