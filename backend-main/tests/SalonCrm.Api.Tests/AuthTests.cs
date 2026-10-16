using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Login_sets_hardened_cookie_and_stores_only_token_hash()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        using var response = await factory.PostLoginAsync(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("salon_sid="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);

        var token = AuthTestHelpers.ExtractToken(response)!;
        Assert.Equal(43, token.Length); // 32 bytes base64url without padding
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        await factory.WithDbAsync(async db =>
        {
            var session = await db.Sessions.SingleAsync(s => s.TokenHash == expectedHash);
            Assert.NotEqual(token, session.TokenHash);
            Assert.False(await db.Sessions.AnyAsync(s => s.TokenHash == token));
            Assert.InRange(session.ExpiresAt - session.CreatedAt, TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(12));
            var user = await db.Users.SingleAsync(u => u.Id == session.UserId);
            Assert.NotNull(user.LastLoginAt);
            Assert.True(user.LastLoginAt > before);
        });
    }

    [Fact]
    public async Task Login_email_is_case_insensitive_and_trimmed()
    {
        using var response = await factory.PostLoginAsync("  ADMIN@SALON.LOCAL ", ApiFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_failures_return_identical_401()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (inactiveId, inactiveEmail) = await factory.CreateUserAsync(admin);
        (await admin.PostAsync($"/api/admin/users/{inactiveId}/deactivate", null)).EnsureSuccessStatusCode();

        using var wrongPassword = await factory.PostLoginAsync(AuthTestHelpers.AdminEmail, "nope-nope-nope");
        using var unknownEmail = await factory.PostLoginAsync("nobody@salon.test", "Passw0rd!x");
        using var inactive = await factory.PostLoginAsync(inactiveEmail, "Passw0rd!x");

        var bodies = new List<(string?, string?)>();
        foreach (var r in new[] { wrongPassword, unknownEmail, inactive })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
            Assert.False(r.Headers.Contains("Set-Cookie"));
            var body = await r.ReadJsonAsync();
            bodies.Add((body.GetProperty("title").GetString(), body.GetProperty("detail").GetString()));
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal("Nesprávny e-mail alebo heslo.", bodies[0].Item2);
    }

    [Fact]
    public async Task Me_returns_user_with_string_role()
    {
        var (client, _) = await factory.LoginAdminAsync();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("admin@salon.local", body.GetProperty("email").GetString());
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("fullName").GetString()));
        Assert.True(body.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Me_without_cookie_returns_401_problem_json_without_redirect()
    {
        using var client = factory.NewClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Me_with_garbage_cookie_returns_401()
    {
        using var client = factory.NewClient("not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_session_and_clears_cookie()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (_, email) = await factory.CreateUserAsync(admin);
        var (client, token) = await factory.LoginAsync(email, "Passw0rd!x");

        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("salon_sid="));
        Assert.StartsWith("salon_sid=;", cookie);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1970", cookie);

        using var old = factory.NewClient(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/me")).StatusCode);
        var hash = SessionTokens.Hash(token);
        Assert.NotNull(await factory.WithDbAsync(db => db.Sessions.Where(s => s.TokenHash == hash).Select(s => s.RevokedAt).SingleAsync()));
    }

    [Fact]
    public async Task Logout_without_session_returns_401()
    {
        using var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Idle_session_older_than_two_hours_is_rejected()
    {
        var (client, token) = await NewUserSessionAsync();
        await SetSessionAsync(token, s => s.LastSeenAt = DateTime.UtcNow - TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_idle_just_under_two_hours_is_still_valid()
    {
        var (client, token) = await NewUserSessionAsync();
        await SetSessionAsync(token, s => s.LastSeenAt = DateTime.UtcNow - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(59));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_past_absolute_expiry_is_rejected()
    {
        var (client, token) = await NewUserSessionAsync();
        await SetSessionAsync(token, s => s.ExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Last_seen_is_updated_only_when_older_than_one_minute()
    {
        var (client, token) = await NewUserSessionAsync();
        var hash = SessionTokens.Hash(token);

        var initial = await factory.WithDbAsync(db => db.Sessions.Where(s => s.TokenHash == hash).Select(s => s.LastSeenAt).SingleAsync());
        (await client.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        var afterFresh = await factory.WithDbAsync(db => db.Sessions.Where(s => s.TokenHash == hash).Select(s => s.LastSeenAt).SingleAsync());
        Assert.Equal(initial, afterFresh);

        var stale = DateTime.UtcNow.AddMinutes(-5);
        await SetSessionAsync(token, s => s.LastSeenAt = stale);
        (await client.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        var afterStale = await factory.WithDbAsync(db => db.Sessions.Where(s => s.TokenHash == hash).Select(s => s.LastSeenAt).SingleAsync());
        Assert.True(afterStale > stale.AddMinutes(4));
    }

    [Fact]
    public async Task Deactivated_user_cookie_stops_working_and_future_appointments_are_counted()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, email) = await factory.CreateUserAsync(admin);
        var (client, _) = await factory.LoginAsync(email, "Passw0rd!x");

        await factory.WithDbAsync(async db =>
        {
            var service = await db.Services.FirstAsync();
            var customerEmail = $"c-{Guid.NewGuid():N}@x.test";
            var customer = new Customer { Email = customerEmail, EmailNormalized = customerEmail, FullName = "Zakaznik", GdprConsentAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
            db.Customers.Add(customer);
            Appointment Make(DateTime start, AppointmentStatus status) => new()
            {
                CustomerId = customer.Id,
                EmployeeId = id,
                ServiceId = service.Id,
                StartsAt = start,
                EndsAt = start.AddHours(1),
                Status = status,
                Price = 10,
                Source = AppointmentSource.Staff,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var future = DateTime.UtcNow.AddDays(3);
            db.Appointments.AddRange(
                Make(future, AppointmentStatus.Scheduled),
                Make(DateTime.UtcNow.AddDays(-3), AppointmentStatus.Scheduled),
                Make(future.AddDays(1), AppointmentStatus.Cancelled));
            await db.SaveChangesAsync();
        });

        var response = await admin.PostAsync($"/api/admin/users/{id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.ReadJsonAsync()).GetProperty("futureAppointments").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var relogin = await factory.PostLoginAsync(email, "Passw0rd!x");
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);

        // Reactivation allows login again.
        (await admin.PostAsync($"/api/admin/users/{id}/activate", null)).EnsureSuccessStatusCode();
        using var again = await factory.PostLoginAsync(email, "Passw0rd!x");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_ip()
    {
        await using var limited = await factory.CreateFreshHostAsync(
            new Dictionary<string, string?> { ["RateLimiting:LoginPermitLimit"] = "3" });

        for (var i = 0; i < 3; i++)
        {
            using var r = await limited.PostLoginAsync(AuthTestHelpers.AdminEmail, "wrong-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        using var blocked = await limited.PostLoginAsync(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        await AssertTooManyRequestsProblemAsync(blocked);
    }

    [Fact]
    public async Task Login_default_rate_limit_is_five_per_minute()
    {
        // Undo the shared factory's high test limit using the value shipped in appsettings.json.
        var appSettingsPath = Path.Combine(
            factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "appsettings.json");
        using var appSettings = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
        var shipped = appSettings.RootElement.GetProperty("RateLimiting").GetProperty("LoginPermitLimit").GetInt32();
        Assert.Equal(5, shipped); // SPEC §4.5: 5 attempts / min / IP

        await using var host = await factory.CreateFreshHostAsync(
            new Dictionary<string, string?> { ["RateLimiting:LoginPermitLimit"] = shipped.ToString() });

        for (var i = 1; i <= 5; i++)
        {
            using var r = await host.PostLoginAsync(AuthTestHelpers.AdminEmail, "wrong-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        using var sixth = await host.PostLoginAsync(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword);
        await AssertTooManyRequestsProblemAsync(sixth);
    }

    [Fact]
    public async Task Login_with_overlong_password_returns_400()
    {
        using var response = await factory.PostLoginAsync(AuthTestHelpers.AdminEmail, new string('a', 10_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_rehashes_legacy_v2_password_hash_to_v3()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, email) = await factory.CreateUserAsync(admin);
        const string password = "Passw0rd!x";
        var v2Hasher = new PasswordHasher<User>(Options.Create(
            new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        var v2Hash = v2Hasher.HashPassword(null!, password);
        Assert.Equal(0x00, Convert.FromBase64String(v2Hash)[0]);
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, v2Hash)));

        using var first = await factory.PostLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stored = await factory.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.PasswordHash).SingleAsync());
        Assert.NotEqual(v2Hash, stored);
        Assert.Equal(0x01, Convert.FromBase64String(stored)[0]);
        using var second = await factory.PostLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_non_json_content()
    {
        using var client = factory.NewClient();
        var content = new StringContent("email=a&password=b", Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await client.PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Public_endpoints_stay_anonymous()
    {
        using var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        // SPA fallback must not be protected (no index.html in the test host, so 404 is fine, 401 is not).
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/some/spa/route")).StatusCode);
        var unknown = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task AssertTooManyRequestsProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.ReadJsonAsync();
        Assert.Equal("https://tools.ietf.org/html/rfc6585#section-4", body.GetProperty("type").GetString());
        Assert.Equal("Too Many Requests", body.GetProperty("title").GetString());
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        Assert.Equal("Príliš veľa pokusov o prihlásenie. Skúste to znova o chvíľu.", body.GetProperty("detail").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    private async Task<(HttpClient Client, string Token)> NewUserSessionAsync()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (_, email) = await factory.CreateUserAsync(admin);
        return await factory.LoginAsync(email, "Passw0rd!x");
    }

    private async Task SetSessionAsync(string token, Action<Session> change)
    {
        var hash = SessionTokens.Hash(token);
        await factory.WithDbAsync(async db =>
        {
            var session = await db.Sessions.SingleAsync(s => s.TokenHash == hash);
            change(session);
            await db.SaveChangesAsync();
        });
    }
}
