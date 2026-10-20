using System.Text.Json;
using SalonCrm.Api.Data.Seed;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

// These tests exercise the same generator as startup, without requiring a Docker fixture.
public sealed class DemoHistoryTests
{
    [Theory]
    [InlineData(2026, 10, 20)]
    [InlineData(2026, 3, 30)]
    [InlineData(2026, 10, 25)]
    public void Generated_history_obeys_calendar_chronology_payments_and_customer_archetypes(int year, int month, int day)
    {
        var now = new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero);
        var (customers, appointments, payments, services, eligible) = Generate(now);
        foreach (var appointment in appointments)
        {
            appointment.Service = services.Single(s => s.Id == appointment.ServiceId);
        }
        var links = eligible.SelectMany(e => e.Value.Select(w => (w.Id, e.Key))).ToHashSet();
        DemoHistoryAssertions.Verify(now, customers, appointments, payments, links);

        var oneOff = customers.Where(c => string.CompareOrdinal(c.Email, "demo-241@salon.example") >= 0);
        foreach (var customer in oneOff)
        {
            Assert.InRange(appointments.Count(a => a.CustomerId == customer.Id && a.Status != AppointmentStatus.Scheduled), 1, 2);
        }
    }

    [Fact]
    public void Same_clock_and_inputs_produce_same_business_data_despite_different_UUIDs()
    {
        var now = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        string Snapshot()
        {
            var (customers, appointments, payments, services, eligible) = Generate(now);
            var byCustomer = customers.ToDictionary(c => c.Id);
            var byService = services.ToDictionary(s => s.Id);
            var workers = eligible.Values.SelectMany(w => w).DistinctBy(w => w.Id).ToDictionary(w => w.Id);
            var byAppointment = payments.ToDictionary(p => p.AppointmentId);
            return JsonSerializer.Serialize(new
            {
                customers = customers.Select(c => new { c.FullName, c.Email, c.Phone, c.CreatedAt, c.GdprConsentAt }),
                appointments = appointments.Select(a => new
                {
                    Customer = byCustomer[a.CustomerId].Email,
                    Service = byService[a.ServiceId].Name,
                    Employee = workers[a.EmployeeId].Email,
                    a.StartsAt,
                    a.EndsAt,
                    a.CreatedAt,
                    a.UpdatedAt,
                    a.CancelledAt,
                    a.CompletedAt,
                    a.Status,
                    a.Source,
                    a.Price,
                    Payment = byAppointment.TryGetValue(a.Id, out var p) ? new { p.Amount, p.Method, p.PaidAt } : null,
                }),
            });
        }
        Assert.Equal(Snapshot(), Snapshot());
    }

    private static (List<Customer>, List<Appointment>, List<Payment>, List<Service>, Dictionary<Guid, List<User>>) Generate(
        DateTimeOffset now)
    {
        var workers = Enumerable.Range(0, 4).Select(i => new User
        {
            Email = $"worker-{i}@salon.example",
            FullName = $"Worker {i}",
            PasswordHash = "test",
            Role = UserRole.User,
            IsActive = true,
        }).ToArray();
        var services = Enum.GetValues<ServiceCategory>().SelectMany(category => new[] { 30, 60 }.Select(minutes => new Service
        {
            Name = $"{category} {minutes}",
            Category = category,
            DurationMinutes = minutes,
            Price = minutes,
            IsActive = true,
        })).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var eligible = services.ToDictionary(s => s.Id, s => s.Category switch
        {
            ServiceCategory.Skin => new List<User> { workers[0], workers[3] },
            ServiceCategory.Makeup => [workers[0]],
            ServiceCategory.Nails => [workers[1]],
            ServiceCategory.Massage => [workers[3]],
            _ => [workers[2]],
        });
        var generator = new DemoDataSeeder.HistoryGenerator(now.UtcDateTime, TestSalon.Options(), services, eligible);
        var (customers, appointments, payments) = generator.Generate();
        return (customers, appointments, payments, services, eligible);
    }
}
