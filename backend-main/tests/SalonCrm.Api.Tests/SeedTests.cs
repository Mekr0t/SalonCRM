using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalonCrm.Api.Data;
using SalonCrm.Api.Data.Seed;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

// These tests use their own fresh databases on the shared container, so they never
// touch the data of the app-wide database used by the other tests.
[Collection("api")]
public class SeedTests(ApiFactory factory)
{
    private static BaseSeeder NewSeeder(AppDbContext db, string? adminPassword, string? userPassword,
        CapturingLogger<BaseSeeder> logger)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminPassword"] = adminPassword,
                ["Seed:UserPassword"] = userPassword,
            })
            .Build();
        return new BaseSeeder(db, config, TimeProvider.System, logger);
    }

    [Theory]
    [InlineData("", "User123!")]
    [InlineData("   ", "User123!")]
    [InlineData(null, "User123!")]
    [InlineData("Admin123!", "")]
    [InlineData("Admin123!", null)]
    public async Task Seed_is_skipped_without_both_passwords(string? adminPassword, string? userPassword)
    {
        await using var db = await TestSupport.CreateEmptyDatabaseAsync(factory.ConnectionString);
        var logger = new CapturingLogger<BaseSeeder>();

        await NewSeeder(db, adminPassword, userPassword, logger).SeedAsync();

        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Services.CountAsync());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("skipped"));
    }

    [Fact]
    public async Task Seed_on_fresh_database_creates_one_admin_four_workers_services_and_links_once()
    {
        await using var db = await TestSupport.CreateEmptyDatabaseAsync(factory.ConnectionString);

        await NewSeeder(db, ApiFactory.AdminPassword, ApiFactory.UserPassword, new CapturingLogger<BaseSeeder>())
            .SeedAsync();

        Assert.Equal(1, await db.Users.CountAsync(u => u.Role == UserRole.Admin));
        Assert.Equal(4, await db.Users.CountAsync(u => u.Role == UserRole.User));
        Assert.Equal(14, await db.Services.CountAsync());
        Assert.Equal(17, await db.UserServices.CountAsync());

        var users = await db.Users.CountAsync();
        var services = await db.Services.CountAsync();
        var links = await db.UserServices.CountAsync();

        // Second run on a populated database must change nothing and log nothing.
        var secondLogger = new CapturingLogger<BaseSeeder>();
        await NewSeeder(db, ApiFactory.AdminPassword, ApiFactory.UserPassword, secondLogger).SeedAsync();

        Assert.Equal(users, await db.Users.CountAsync());
        Assert.Equal(services, await db.Services.CountAsync());
        Assert.Equal(links, await db.UserServices.CountAsync());
        Assert.Empty(secondLogger.Entries);
    }
}
