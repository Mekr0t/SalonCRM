using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public sealed class InternalApiTests(ApiFactory factory)
{
    private const string Key = "internal-api-test-key";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private Task<WebApplicationFactory<Program>> HostAsync(string? key = Key, bool demo = false, TimeProvider? clock = null) =>
        factory.CreateFreshHostAsync(new Dictionary<string, string?>
        {
            ["Internal:ApiKey"] = key,
            ["Seed:DemoData"] = demo.ToString(),
        }, clock ?? new FixedTimeProvider(Now));

    private static HttpClient Client(WebApplicationFactory<Program> host, string? key = Key, string? cookie = null)
    {
        var client = host.NewClient(cookie);
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", key);
        }
        return client;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("internal-api-test-keY")]
    public async Task Both_endpoints_reject_missing_or_wrong_key_even_with_valid_cookie(string? key)
    {
        using var host = await HostAsync();
        var (admin, token) = await host.LoginAdminAsync();
        using (admin)
        using (var client = Client(host, key, token))
        {
            using var export = await client.GetAsync("/api/internal/ml/export");
            using var introspect = await client.PostAsJsonAsync("/api/internal/sessions/introspect", new { sessionToken = token });
            foreach (var response in new[] { export, introspect })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                var body = await response.ReadJsonAsync();
                Assert.Equal(401, body.GetProperty("status").GetInt32());
                Assert.True(body.TryGetProperty("type", out _));
                Assert.True(body.TryGetProperty("title", out _));
                Assert.True(body.TryGetProperty("detail", out _));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_configured_key_never_authorizes(string key)
    {
        using var host = await HostAsync(key);
        using var client = Client(host, key);
        using var response = await client.GetAsync("/api/internal/ml/export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_key_values_are_rejected()
    {
        using var host = await HostAsync();
        using var client = Client(host);
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong");
        using var response = await client.GetAsync("/api/internal/ml/export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword, "Admin")]
    [InlineData(AuthTestHelpers.WorkerEmail, ApiFactory.UserPassword, "User")]
    public async Task Introspect_accepts_raw_token_without_cookie_and_returns_only_contract_fields(
        string email, string password, string role)
    {
        using var host = await HostAsync();
        var (loggedIn, token) = await host.LoginAsync(email, password);
        using (loggedIn)
        using (var client = Client(host))
        using (var response = await client.PostAsJsonAsync("/api/internal/sessions/introspect", new { sessionToken = token }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.ReadJsonAsync();
            AssertFields(body, "active", "userId", "role", "fullName");
            Assert.True(body.GetProperty("active").GetBoolean());
            Assert.Equal(role, body.GetProperty("role").GetString());
            var user = await host.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
            Assert.Equal(user.Id, body.GetProperty("userId").GetGuid());
            Assert.Equal(user.FullName, body.GetProperty("fullName").GetString());
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("idle")]
    [InlineData("inactive")]
    public async Task Introspect_returns_inactive_for_every_invalid_session_condition(string condition)
    {
        using var host = await HostAsync();
        var (loggedIn, token) = await host.LoginAdminAsync();
        using (loggedIn)
        {
            await host.WithDbAsync(async db =>
            {
                var hash = SessionTokens.Hash(token);
                var session = await db.Sessions.Include(s => s.User).SingleAsync(s => s.TokenHash == hash);
                switch (condition)
                {
                    case "revoked": session.RevokedAt = Now.UtcDateTime; break;
                    case "expired": session.ExpiresAt = Now.UtcDateTime; break;
                    case "idle": session.LastSeenAt = Now.UtcDateTime.AddHours(-2); break;
                    case "inactive": session.User.IsActive = false; break;
                }
                await db.SaveChangesAsync();
            });
            using var client = Client(host);
            using var response = await client.PostAsJsonAsync("/api/internal/sessions/introspect",
                new { sessionToken = condition == "unknown" ? "unknown-token" : token });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.ReadJsonAsync();
            AssertFields(body, "active");
            Assert.False(body.GetProperty("active").GetBoolean());
        }
    }

    [Fact]
    public async Task Introspect_touches_last_seen_at_most_once_per_minute_without_extending_absolute_expiry()
    {
        var clock = new MutableClock(Now);
        using var host = await HostAsync(clock: clock);
        var (loggedIn, token) = await host.LoginAdminAsync();
        using (loggedIn)
        using (var client = Client(host))
        {
            var hash = SessionTokens.Hash(token);
            var original = await host.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.TokenHash == hash));
            foreach (var seconds in new[] { 59, 60, 80 })
            {
                clock.Now = Now.AddSeconds(seconds);
                using var response = await client.PostAsJsonAsync("/api/internal/sessions/introspect", new { sessionToken = token });
                response.EnsureSuccessStatusCode();
                var current = await host.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.TokenHash == hash));
                Assert.Equal(seconds < 60 ? Now.UtcDateTime : Now.AddMinutes(1).UtcDateTime, current.LastSeenAt);
                Assert.Equal(original.ExpiresAt, current.ExpiresAt);
            }
        }
    }

    [Fact]
    public async Task Export_without_cookie_supports_empty_database()
    {
        using var host = await HostAsync();
        using var client = Client(host);
        using var response = await client.GetAsync("/api/internal/ml/export");
        response.EnsureSuccessStatusCode();
        var body = await response.ReadJsonAsync();
        AssertFields(body, "generatedAt", "customers", "appointments");
        Assert.Equal(Now.UtcDateTime, body.GetProperty("generatedAt").GetDateTime());
        Assert.Empty(body.GetProperty("customers").EnumerateArray());
        Assert.Empty(body.GetProperty("appointments").EnumerateArray());
    }

    [Fact]
    public async Task Export_has_exact_ML_contract_all_statuses_and_no_personal_fields()
    {
        using var host = await HostAsync(demo: true);
        using var client = Client(host);
        using var response = await client.GetAsync("/api/internal/ml/export");
        response.EnsureSuccessStatusCode();
        var body = await response.ReadJsonAsync();
        AssertFields(body, "generatedAt", "customers", "appointments");
        var ids = new HashSet<Guid>();
        foreach (var customer in body.GetProperty("customers").EnumerateArray())
        {
            AssertFields(customer, "customerId", "createdAt");
            ids.Add(customer.GetProperty("customerId").GetGuid());
            AssertUtc(customer.GetProperty("createdAt"));
        }
        Assert.Equal(300, ids.Count);
        var statuses = new HashSet<string>();
        foreach (var appointment in body.GetProperty("appointments").EnumerateArray())
        {
            AssertFields(appointment, "appointmentId", "customerId", "employeeId", "serviceId", "serviceCategory",
                "startsAt", "createdAt", "cancelledAt", "status", "price");
            Assert.Contains(appointment.GetProperty("customerId").GetGuid(), ids);
            Assert.True(Enum.TryParse<ServiceCategory>(appointment.GetProperty("serviceCategory").GetString(), out _));
            AssertUtc(appointment.GetProperty("startsAt"));
            AssertUtc(appointment.GetProperty("createdAt"));
            var cancelled = appointment.GetProperty("cancelledAt");
            if (cancelled.ValueKind != JsonValueKind.Null)
            {
                AssertUtc(cancelled);
            }
            Assert.Equal(JsonValueKind.Number, appointment.GetProperty("price").ValueKind);
            statuses.Add(appointment.GetProperty("status").GetString()!);
        }
        Assert.Equal(new[] { "Cancelled", "Completed", "NoShow", "Scheduled" }, statuses.Order());
    }

    private static void AssertFields(JsonElement item, params string[] fields) =>
        Assert.Equal(fields.Order(), item.EnumerateObject().Select(p => p.Name).Order());

    private static void AssertUtc(JsonElement value)
    {
        Assert.EndsWith("Z", value.GetString());
        Assert.Equal(DateTimeKind.Utc, value.GetDateTime().Kind);
    }
}
