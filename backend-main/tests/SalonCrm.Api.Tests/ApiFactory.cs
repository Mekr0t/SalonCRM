using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace SalonCrm.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "Admin123!";
    public const string UserPassword = "User123!";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();

    // Server-level connection string of the shared container (database "test").
    public string ConnectionString => _db.GetConnectionString();

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = _db.GetConnectionString(),
            ["Seed:AdminPassword"] = AdminPassword,
            ["Seed:UserPassword"] = UserPassword,
            ["Cookie:Secure"] = "false",
            // High enough that functional tests never hit the login rate limit.
            ["RateLimiting:LoginPermitLimit"] = "100000",
            ["RateLimiting:BookingPermitLimit"] = "100000",
        }));
        return base.CreateHost(builder);
    }
}
