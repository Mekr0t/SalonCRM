using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

internal static class BookingTestHelpers
{
    // A date with the given weekday, at least minDaysAhead local days from the real "today" (and < 50 days ahead).
    public static DateOnly NextWeekday(DayOfWeek day, int minDaysAhead = 7)
    {
        var today = DateOnly.FromDateTime(TestSalon.ToLocal(DateTime.UtcNow));
        var date = today.AddDays(minDaysAhead);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return date;
    }

    public static async Task<(Guid Id, string Name)> CreateServiceAsync(
        this HttpClient admin, int durationMinutes = 60, decimal price = 40m)
    {
        var name = "Služba " + Guid.NewGuid().ToString("N")[..10];
        var response = await admin.PostAsJsonAsync("/api/admin/services", new
        {
            name,
            category = "Skin",
            description = "test",
            durationMinutes,
            price,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await response.ReadJsonAsync()).GetProperty("id").GetGuid(), name);
    }

    // Employee with an explicit name prefix (names sort ordinally by prefix) who performs the given services.
    public static async Task<(Guid Id, string Name)> CreateEmployeeAsync(
        this HttpClient admin, string prefix, params Guid[] serviceIds)
    {
        var email = $"e-{Guid.NewGuid():N}@salon.test";
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        var response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            email,
            fullName = name,
            role = "User",
            password = "Passw0rd!x",
            serviceIds,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await response.ReadJsonAsync()).GetProperty("id").GetGuid(), name);
    }

    public static async Task SeedAppointmentAsync(
        this WebApplicationFactory<Program> factory, Guid employeeId, Guid serviceId, DateTime startsAtUtc,
        int durationMinutes = 60, AppointmentStatus status = AppointmentStatus.Scheduled)
    {
        await factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var email = $"seed-{Guid.NewGuid():N}@salon.test";
            var customer = new Customer
            {
                Email = email, EmailNormalized = email, FullName = "Seed Customer", GdprConsentAt = now, CreatedAt = now,
            };
            db.Customers.Add(customer);
            db.Appointments.Add(new Appointment
            {
                CustomerId = customer.Id,
                EmployeeId = employeeId,
                ServiceId = serviceId,
                StartsAt = startsAtUtc,
                EndsAt = startsAtUtc.AddMinutes(durationMinutes),
                Status = status,
                Price = 10,
                Source = AppointmentSource.Staff,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
    }

    public static async Task<JsonElement> AvailabilityAsync(
        this HttpClient client, Guid serviceId, DateOnly date, Guid? employeeId = null)
    {
        var url = $"/api/public/availability?serviceId={serviceId}&date={date:yyyy-MM-dd}"
            + (employeeId is null ? "" : $"&employeeId={employeeId}");
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public static List<Guid> EmployeeIdsAt(this JsonElement slots, DateTime startUtc)
    {
        var slot = slots.EnumerateArray().FirstOrDefault(s => s.GetProperty("startsAt").GetDateTime() == startUtc);
        return slot.ValueKind == JsonValueKind.Undefined
            ? []
            : slot.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    public static object BookingBody(
        Guid serviceId, DateTime startsAtUtc, Guid? employeeId = null, string? email = null,
        string fullName = "Anna Testová", string? phone = null, bool? gdprConsent = true) => new
    {
        serviceId,
        employeeId,
        startsAt = startsAtUtc,
        fullName,
        email = email ?? $"booking-{Guid.NewGuid():N}@salon.test",
        phone,
        note = "poznámka",
        gdprConsent,
    };

    public static Task<HttpResponseMessage> PostBookingAsync(this HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/public/bookings", body);
}
