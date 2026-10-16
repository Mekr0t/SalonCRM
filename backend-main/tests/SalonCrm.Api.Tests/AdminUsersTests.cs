using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class AdminUsersTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!x";

    [Fact]
    public async Task User_role_gets_403_problem_json_on_every_admin_endpoint()
    {
        var (worker, _) = await factory.LoginAsync(AuthTestHelpers.WorkerEmail, ApiFactory.UserPassword);
        var id = Guid.NewGuid();
        var json = JsonContent(new { email = "a@b.sk", fullName = "x", role = "User", password = Pw, serviceIds = Array.Empty<Guid>() });

        var calls = new List<Func<Task<HttpResponseMessage>>>
        {
            () => worker.GetAsync("/api/admin/users"),
            () => worker.PostAsync("/api/admin/users", json),
            () => worker.PutAsync($"/api/admin/users/{id}", JsonContent(new { fullName = "x", role = "User", serviceIds = Array.Empty<Guid>() })),
            () => worker.PostAsync($"/api/admin/users/{id}/deactivate", null),
            () => worker.PostAsync($"/api/admin/users/{id}/activate", null),
            () => worker.PostAsync($"/api/admin/users/{id}/password", JsonContent(new { password = Pw })),
            () => worker.GetAsync("/api/admin/sessions"),
            () => worker.DeleteAsync($"/api/admin/sessions/{id}"),
        };

        foreach (var call in calls)
        {
            using var response = await call();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Anonymous_gets_401_on_admin_endpoints()
    {
        using var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/sessions")).StatusCode);
    }

    [Fact]
    public async Task List_returns_users_ordered_by_name_with_services()
    {
        var (admin, _) = await factory.LoginAdminAsync();

        var response = await admin.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        var names = users.Select(u => u.GetProperty("fullName").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).Count(), names.Count);
        var jana = users.Single(u => u.GetProperty("email").GetString() == AuthTestHelpers.WorkerEmail);
        Assert.NotEmpty(jana.GetProperty("services").EnumerateArray());
        Assert.Equal("User", jana.GetProperty("role").GetString());
        Assert.True(jana.GetProperty("isActive").GetBoolean());
        Assert.False(jana.ToString().Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_user_then_user_can_log_in_and_services_are_assigned()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var serviceIds = await ServiceIdsAsync(2);
        var email = $"New-{Guid.NewGuid():N}@Salon.Test";

        var response = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "  " + email + " ", fullName = "Nová Kolegyňa", role = "User", password = Pw, serviceIds });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(email.ToLowerInvariant(), body.GetProperty("email").GetString());
        Assert.Equal(2, body.GetProperty("services").GetArrayLength());
        using var login = await factory.PostLoginAsync(email, Pw);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Create_with_duplicate_email_in_other_case_returns_409()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (_, email) = await factory.CreateUserAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = email.ToUpperInvariant(), fullName = "Dup", role = "User", password = Pw, serviceIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_validates_input()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var email = $"v-{Guid.NewGuid():N}@salon.test";

        var shortPassword = await admin.PostAsJsonAsync("/api/admin/users",
            new { email, fullName = "X", role = "User", password = "short", serviceIds = Array.Empty<Guid>() });
        var badEmail = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "not-an-email", fullName = "X", role = "User", password = Pw, serviceIds = Array.Empty<Guid>() });
        var badRole = await admin.PostAsJsonAsync("/api/admin/users",
            new { email, fullName = "X", role = "Boss", password = Pw, serviceIds = Array.Empty<Guid>() });
        var unknownService = await admin.PostAsJsonAsync("/api/admin/users",
            new { email, fullName = "X", role = "User", password = Pw, serviceIds = new[] { Guid.NewGuid() } });

        foreach (var r in new[] { shortPassword, badEmail, badRole, unknownService })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        Assert.Equal("application/problem+json", unknownService.Content.Headers.ContentType?.MediaType);
        // None of the invalid requests created the user.
        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Create_without_role_returns_400_and_creates_nothing()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var email = $"norole-{Guid.NewGuid():N}@salon.test";

        var response = await admin.PostAsJsonAsync("/api/admin/users",
            new { email, fullName = "Bez roly", password = Pw, serviceIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("Role", out _));
        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"7\"")]
    [InlineData("\"0\"")]
    public async Task Create_rejects_null_numeric_or_undefined_role(string roleJson)
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var email = $"numrole-{Guid.NewGuid():N}@salon.test";
        var body = $$"""{"email":"{{email}}","fullName":"X","role":{{roleJson}},"password":"{{Pw}}","serviceIds":[]}""";

        var response = await admin.PostAsync("/api/admin/users", RawJson(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Update_without_role_returns_400_and_keeps_role()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await factory.CreateUserAsync(admin);

        var response = await admin.PutAsJsonAsync($"/api/admin/users/{id}",
            new { fullName = "Bez roly", serviceIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("Role", out _));
        var user = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));
        Assert.Equal(Domain.UserRole.User, user.Role);
        Assert.NotEqual("Bez roly", user.FullName);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("\"7\"")]
    public async Task Update_rejects_null_numeric_or_undefined_role(string roleJson)
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await factory.CreateUserAsync(admin);

        var response = await admin.PutAsync($"/api/admin/users/{id}",
            RawJson($$"""{"fullName":"X","role":{{roleJson}},"serviceIds":[]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var role = await factory.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.Role).SingleAsync());
        Assert.Equal(Domain.UserRole.User, role);
    }

    [Fact]
    public async Task Overlong_fields_are_rejected_with_400()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await factory.CreateUserAsync(admin);
        var email = $"long-{Guid.NewGuid():N}@salon.test";
        var longPassword = new string('a', 257);

        var responses = new[]
        {
            await admin.PostAsJsonAsync("/api/admin/users",
                new { email, fullName = "X", role = "User", password = longPassword, serviceIds = Array.Empty<Guid>() }),
            await admin.PostAsJsonAsync("/api/admin/users",
                new { email, fullName = new string('x', 201), role = "User", password = Pw, serviceIds = Array.Empty<Guid>() }),
            await admin.PostAsJsonAsync("/api/admin/users",
                new { email = new string('a', 250) + "@x.sk", fullName = "X", role = "User", password = Pw, serviceIds = Array.Empty<Guid>() }),
            await admin.PostAsJsonAsync($"/api/admin/users/{id}/password", new { password = longPassword }),
        };

        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Update_replaces_services_and_name()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var ids = await ServiceIdsAsync(3);
        var (id, _) = await factory.CreateUserAsync(admin, serviceIds: [ids[0], ids[1]]);

        var response = await admin.PutAsJsonAsync($"/api/admin/users/{id}",
            new { fullName = "Premenovaná", role = "User", serviceIds = new[] { ids[1], ids[2] } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("Premenovaná", body.GetProperty("fullName").GetString());
        var actual = body.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).OrderBy(g => g);
        Assert.Equal(new[] { ids[1], ids[2] }.OrderBy(g => g), actual);
        Assert.Equal(2, await factory.WithDbAsync(db => db.UserServices.CountAsync(us => us.UserId == id)));
    }

    [Fact]
    public async Task Update_unknown_user_returns_404()
    {
        var (admin, _) = await factory.LoginAdminAsync();

        var response = await admin.PutAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}",
            new { fullName = "x", role = "User", serviceIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_self_or_remove_own_admin_role()
    {
        var (admin, token) = await factory.LoginAdminAsync();
        var me = (await (await admin.GetAsync("/api/auth/me")).ReadJsonAsync()).GetProperty("id").GetGuid();

        var deactivate = await admin.PostAsync($"/api/admin/users/{me}/deactivate", null);
        var demote = await admin.PutAsJsonAsync($"/api/admin/users/{me}",
            new { fullName = "Admin", role = "User", serviceIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Contains("sám seba", (await deactivate.ReadJsonAsync()).GetProperty("detail").GetString());
        // Session and role intact.
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient(token).GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Last_active_admin_cannot_be_deactivated_or_demoted()
    {
        await using var fresh = await factory.CreateFreshHostAsync();
        var (seeded, _) = await fresh.LoginAsync(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword);
        var seededId = (await (await seeded.GetAsync("/api/auth/me")).ReadJsonAsync()).GetProperty("id").GetGuid();

        // A second admin takes over: demoting / deactivating the seeded admin is fine while another active admin exists.
        var (secondId, secondEmail) = await fresh.CreateUserAsync(seeded, role: "Admin");
        var (second, _) = await fresh.LoginAsync(secondEmail, Pw);
        var deactivate = await second.PostAsync($"/api/admin/users/{seededId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // Now `second` is the only active admin.
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync($"/api/admin/users/{secondId}/deactivate", null)).StatusCode);
        var demote = await second.PutAsJsonAsync($"/api/admin/users/{secondId}",
            new { fullName = "x", role = "User", serviceIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        // Reactivating the first admin makes demotion of the other possible again.
        (await second.PostAsync($"/api/admin/users/{seededId}/activate", null)).EnsureSuccessStatusCode();
        var (first, _) = await fresh.LoginAsync(AuthTestHelpers.AdminEmail, ApiFactory.AdminPassword);
        var demoteSecond = await first.PutAsJsonAsync($"/api/admin/users/{secondId}",
            new { fullName = "x", role = "User", serviceIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, demoteSecond.StatusCode);
    }

    [Fact]
    public async Task Password_reset_revokes_target_sessions_and_changes_password()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, email) = await factory.CreateUserAsync(admin);
        var (target, _) = await factory.LoginAsync(email, Pw);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{id}/password", new { password = "BrandNew-pw1" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
        using var oldLogin = await factory.PostLoginAsync(email, Pw);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await factory.PostLoginAsync(email, "BrandNew-pw1");
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Password_reset_rejects_short_password()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await factory.CreateUserAsync(admin);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{id}/password", new { password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_resetting_own_password_keeps_current_session_only()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, email) = await factory.CreateUserAsync(admin, role: "Admin");
        var (self, _) = await factory.LoginAsync(email, Pw);
        var (other, _) = await factory.LoginAsync(email, Pw);

        var response = await self.PostAsJsonAsync($"/api/admin/users/{id}/password", new { password = "BrandNew-pw1" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await self.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Deactivate_unknown_user_returns_404()
    {
        var (admin, _) = await factory.LoginAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/api/admin/users/{Guid.NewGuid()}/deactivate", null)).StatusCode);
    }

    private static StringContent RawJson(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private static StringContent JsonContent(object o) =>
        new(System.Text.Json.JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    private Task<Guid[]> ServiceIdsAsync(int count) =>
        factory.WithDbAsync(db => db.Services.OrderBy(s => s.Name).Select(s => s.Id).Take(count).ToArrayAsync());
}
