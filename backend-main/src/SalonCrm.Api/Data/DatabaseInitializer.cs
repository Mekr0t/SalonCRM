using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalonCrm.Api.Data.Seed;

namespace SalonCrm.Api.Data;

public static class DatabaseInitializer
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(ct);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsConnectionFailure(ex))
            {
                logger.LogWarning(ex, "Database not reachable (attempt {Attempt}/{Max}), retrying in {Delay}s.",
                    attempt, MaxAttempts, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, ct);
            }
        }

        await scope.ServiceProvider.GetRequiredService<BaseSeeder>().SeedAsync(ct);
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(ct);
    }

    // Retry only while the server is unreachable or still starting up; real SQL errors
    // (PostgresException with other SQL states) must surface immediately.
    private static bool IsConnectionFailure(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState.StartsWith("08") || pg.SqlState == PostgresErrorCodes.CannotConnectNow,
        NpgsqlException or System.Net.Sockets.SocketException or TimeoutException => true,
        _ => ex.InnerException is not null && IsConnectionFailure(ex.InnerException),
    };
}
