using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SalonCrm.Api.Data;
using SalonCrm.Api.Data.Seed;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public sealed class DemoDataSeederTests(ApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static DemoDataSeeder Seeder(AppDbContext db, bool enabled, DateTimeOffset? now = null,
        CapturingLogger<DemoDataSeeder>? logger = null) => new(db,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:DemoData"] = enabled.ToString(),
        }).Build(), new FixedTimeProvider(now ?? Now), Options.Create(TestSalon.Options()),
        logger ?? new CapturingLogger<DemoDataSeeder>());

    [Fact]
    public async Task Seed_is_opt_in_and_skips_missing_prerequisites_and_existing_customers()
    {
        await using var db = await TestSupport.CreateEmptyDatabaseAsync(factory.ConnectionString);
        await Seeder(db, false).SeedAsync();
        Assert.Empty(await db.Customers.ToListAsync());

        var logger = new CapturingLogger<DemoDataSeeder>();
        await Seeder(db, true, logger: logger).SeedAsync();
        Assert.Empty(await db.Customers.ToListAsync());
        Assert.Contains(logger.Entries, e => e.Message.Contains("skipped"));

        db.Customers.Add(new Customer
        {
            FullName = "Existing customer",
            Email = "existing@example.test",
            EmailNormalized = "existing@example.test",
            CreatedAt = Now.UtcDateTime,
            GdprConsentAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
        await Seeder(db, true).SeedAsync();
        Assert.Equal("existing@example.test", (await db.Customers.SingleAsync()).Email);
        Assert.Empty(await db.Appointments.ToListAsync());
    }

    [Theory]
    [InlineData(2026, 10, 20)]
    [InlineData(2026, 3, 30)]
    public async Task Startup_creates_consistent_history_payments_and_three_weeks_of_future_calendar(int year, int month, int day)
    {
        var now = new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero);
        using var host = await factory.CreateFreshHostAsync(new Dictionary<string, string?>
        {
            ["Seed:DemoData"] = "true",
        }, new FixedTimeProvider(now));

        await host.WithDbAsync(async db =>
        {
            var customers = await db.Customers.AsNoTracking().ToListAsync();
            var appointments = await db.Appointments.AsNoTracking().Include(a => a.Service).ToListAsync();
            var payments = await db.Payments.AsNoTracking().ToListAsync();
            var links = (await db.UserServices.ToListAsync()).Select(l => (l.UserId, l.ServiceId)).ToHashSet();
            DemoHistoryAssertions.Verify(now, customers, appointments, payments, links);

            // A restart must preserve every generated record, including IDs and timestamps.
            var ids = appointments.Select(a => a.Id).Order().ToArray();
            await Seeder(db, true, now.AddDays(2)).SeedAsync();
            Assert.Equal(ids, await db.Appointments.Select(a => a.Id).OrderBy(id => id).ToArrayAsync());
            Assert.Equal(300, await db.Customers.CountAsync());
        });
    }

    [Fact]
    public async Task Fixed_clock_and_seed_reproduce_business_data_across_fresh_databases()
    {
        var settings = new Dictionary<string, string?> { ["Seed:DemoData"] = "true" };
        using var first = await factory.CreateFreshHostAsync(settings, new FixedTimeProvider(Now));
        using var second = await factory.CreateFreshHostAsync(settings, new FixedTimeProvider(Now));

        static async Task<string> Snapshot(AppDbContext db)
        {
            var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Email)
                .Select(c => new { c.FullName, c.Email, c.Phone, c.CreatedAt, c.GdprConsentAt }).ToListAsync();
            var appointments = await db.Appointments.AsNoTracking().OrderBy(a => a.Customer.Email).ThenBy(a => a.StartsAt)
                .Select(a => new
                {
                    a.Customer.Email,
                    Service = a.Service.Name,
                    Employee = a.Employee.Email,
                    a.StartsAt,
                    a.EndsAt,
                    a.Status,
                    a.Source,
                    a.Price,
                    a.CreatedAt,
                    a.UpdatedAt,
                    a.CancelledAt,
                    a.CompletedAt,
                    Payment = a.Payment == null ? null : new { a.Payment.Amount, a.Payment.Method, a.Payment.PaidAt },
                }).ToListAsync();
            return JsonSerializer.Serialize(new { customers, appointments });
        }

        Assert.Equal(await first.WithDbAsync(Snapshot), await second.WithDbAsync(Snapshot));
    }
}
