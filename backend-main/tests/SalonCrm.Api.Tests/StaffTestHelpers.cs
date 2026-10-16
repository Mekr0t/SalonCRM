using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

internal sealed record Worker(Guid Id, string Name, string Email, HttpClient Client);

internal static class StaffTestHelpers
{
    public const string WorkerPassword = "Passw0rd!x";

    // A User account performing the given services, already logged in. Names sort ordinally by prefix.
    public static async Task<Worker> CreateWorkerAsync(
        this WebApplicationFactory<Program> factory, HttpClient admin, string prefix, params Guid[] serviceIds)
    {
        var email = $"w-{Guid.NewGuid():N}@salon.test";
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        var response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            email, fullName = name, role = "User", password = WorkerPassword, serviceIds,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        var (client, _) = await factory.LoginAsync(email, WorkerPassword);
        return new Worker(id, name, email, client);
    }

    // Truncated to whole minutes so values survive JSON / DB round trips unchanged.
    public static DateTime MinutesAgo(int minutes)
    {
        var t = DateTime.UtcNow.AddMinutes(-minutes);
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
    }

    public static DateTime FutureMonday(int hour, int minute = 0, int minDaysAhead = 7) =>
        TestSalon.LocalToUtc(BookingTestHelpers.NextWeekday(DayOfWeek.Monday, minDaysAhead), hour, minute);

    public static async Task<Guid> InsertCustomerAsync(
        this WebApplicationFactory<Program> factory, string? name = null, string? email = null, string? phone = null)
    {
        email ??= $"cust-{Guid.NewGuid():N}@salon.test";
        return await factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var customer = new Customer
            {
                Email = email,
                EmailNormalized = email.ToLowerInvariant(),
                FullName = name ?? "Zákazník " + Guid.NewGuid().ToString("N")[..10],
                Phone = phone,
                GdprConsentAt = now,
                CreatedAt = now,
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            return customer.Id;
        });
    }

    // Inserts straight into the DB (the API refuses past times). Completed rows may get a payment.
    public static async Task<(Guid AppointmentId, Guid CustomerId)> InsertAppointmentAsync(
        this WebApplicationFactory<Program> factory, Guid employeeId, Guid serviceId, DateTime startsAtUtc,
        int durationMinutes = 60, AppointmentStatus status = AppointmentStatus.Scheduled, Guid? customerId = null,
        decimal price = 40m, decimal? paidAmount = null)
    {
        var cid = customerId ?? await factory.InsertCustomerAsync();
        var id = Guid.CreateVersion7();
        await factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Appointments.Add(new Appointment
            {
                Id = id,
                CustomerId = cid,
                EmployeeId = employeeId,
                ServiceId = serviceId,
                StartsAt = startsAtUtc,
                EndsAt = startsAtUtc.AddMinutes(durationMinutes),
                Status = status,
                Price = price,
                Source = AppointmentSource.Staff,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = status == AppointmentStatus.Completed ? startsAtUtc.AddMinutes(durationMinutes) : null,
                CancelledAt = status == AppointmentStatus.Cancelled ? now : null,
            });
            if (paidAmount is { } amount)
            {
                db.Payments.Add(new Payment
                {
                    AppointmentId = id,
                    Amount = amount,
                    Method = PaymentMethod.Cash,
                    PaidAt = startsAtUtc.AddMinutes(durationMinutes),
                    RecordedByUserId = employeeId,
                });
            }

            await db.SaveChangesAsync();
        });
        return (id, cid);
    }

    public static object StaffBody(
        Guid serviceId, DateTime startsAtUtc, Guid? employeeId = null, string? email = null,
        bool? gdprConsent = true, string? staffNote = null) => new
    {
        serviceId,
        employeeId,
        startsAt = startsAtUtc,
        fullName = "Telefonická Zákazníčka",
        email = email ?? $"phone-{Guid.NewGuid():N}@salon.test",
        phone = "+421900111222",
        customerNote = "poznámka zákazníka",
        staffNote,
        gdprConsent,
    };

    public static async Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PatchRawAsync(this HttpClient client, string url, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return client.SendAsync(request);
    }

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    public static Task<HttpResponseMessage> ListAppointmentsAsync(
        this HttpClient client, DateTime from, DateTime to, string? extra = null) =>
        client.GetAsync($"/api/appointments?from={Iso(from)}&to={Iso(to)}{extra}");

    public static List<Guid> Ids(this JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    public static Task<Appointment> LoadAppointmentAsync(this WebApplicationFactory<Program> factory, Guid id) =>
        factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
}
