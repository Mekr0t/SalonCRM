using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;

namespace SalonCrm.Api.Tests;

internal static class AuthTestHelpers
{
    public const string AdminEmail = "admin@salon.local";
    public const string WorkerEmail = "jana.kovacova@salon.local";

    // Cookies are handled manually (HandleCookies=false) so tests can replay / tamper with them.
    public static HttpClient NewClient(this WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{SessionDefaults.CookieName}={token}");
        }

        return client;
    }

    public static string? ExtractToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var header = values.FirstOrDefault(v => v.StartsWith(SessionDefaults.CookieName + "=", StringComparison.Ordinal));
        return header?.Split(';')[0][(SessionDefaults.CookieName.Length + 1)..];
    }

    public static async Task<HttpResponseMessage> PostLoginAsync(
        this WebApplicationFactory<Program> factory, string email, string password)
    {
        using var client = factory.NewClient();
        return await client.PostAsJsonAsync("/api/auth/login", new { email, password });
    }

    // Logs in and returns a client carrying the session cookie, plus the raw token.
    public static async Task<(HttpClient Client, string Token)> LoginAsync(
        this WebApplicationFactory<Program> factory, string email, string password)
    {
        using var response = await factory.PostLoginAsync(email, password);
        response.EnsureSuccessStatusCode();
        var token = ExtractToken(response) ?? throw new InvalidOperationException("No session cookie.");
        return (factory.NewClient(token), token);
    }

    public static Task<(HttpClient Client, string Token)> LoginAdminAsync(this WebApplicationFactory<Program> factory) =>
        factory.LoginAsync(AdminEmail, ApiFactory.AdminPassword);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task WithDbAsync(this WebApplicationFactory<Program> factory, Func<AppDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static async Task<T> WithDbAsync<T>(this WebApplicationFactory<Program> factory, Func<AppDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static Task<Guid> SessionIdAsync(this WebApplicationFactory<Program> factory, string token)
    {
        var hash = SessionTokens.Hash(token);
        return factory.WithDbAsync(db => db.Sessions.Where(s => s.TokenHash == hash).Select(s => s.Id).SingleAsync());
    }

    // Creates a user through the admin API; returns id + e-mail.
    public static async Task<(Guid Id, string Email)> CreateUserAsync(
        this WebApplicationFactory<Program> factory, HttpClient adminClient,
        string role = "User", string password = "Passw0rd!x", Guid[]? serviceIds = null)
    {
        var email = $"t-{Guid.NewGuid():N}@salon.test";
        var response = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email,
            fullName = "Test " + email[..8],
            role,
            password,
            serviceIds = serviceIds ?? [],
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        return (body.GetProperty("id").GetGuid(), email);
    }

    // A separate host on a brand new migrated + seeded database, for tests that alter the seeded accounts.
    public static async Task<WebApplicationFactory<Program>> CreateFreshHostAsync(
        this ApiFactory factory, Dictionary<string, string?>? extraSettings = null)
    {
        await using var fresh = await TestSupport.CreateEmptyDatabaseAsync(factory.ConnectionString);
        var connectionString = fresh.Database.GetConnectionString()!;
        return factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
        {
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Main"] = connectionString });
            if (extraSettings is not null)
            {
                c.AddInMemoryCollection(extraSettings);
            }
        }));
    }
}
