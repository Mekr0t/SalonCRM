using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class AdminSessionsTests(ApiFactory factory)
{
    [Fact]
    public async Task List_contains_fresh_login_and_marks_current_session()
    {
        var (admin, adminToken) = await factory.LoginAdminAsync();
        var (admin2, token2) = await factory.LoginAdminAsync();
        var id1 = await factory.SessionIdAsync(adminToken);
        var id2 = await factory.SessionIdAsync(token2);

        var response = await admin.GetAsync("/api/admin/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        var mine = sessions.Single(s => s.GetProperty("id").GetGuid() == id1);
        var other = sessions.Single(s => s.GetProperty("id").GetGuid() == id2);
        Assert.True(mine.GetProperty("isCurrent").GetBoolean());
        Assert.False(other.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("admin@salon.local", mine.GetProperty("userEmail").GetString());
        Assert.False(string.IsNullOrEmpty(mine.GetProperty("userFullName").GetString()));
        var seen = sessions.Select(s => s.GetProperty("lastSeenAt").GetDateTime()).ToList();
        Assert.Equal(seen.OrderByDescending(d => d), seen);
        Assert.Equal(HttpStatusCode.OK, (await admin2.GetAsync("/api/admin/sessions")).StatusCode);
    }

    [Fact]
    public async Task List_excludes_revoked_sessions()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (_, token) = await factory.LoginAdminAsync();
        var id = await factory.SessionIdAsync(token);
        (await admin.DeleteAsync($"/api/admin/sessions/{id}")).EnsureSuccessStatusCode();

        var sessions = (await (await admin.GetAsync("/api/admin/sessions")).ReadJsonAsync()).EnumerateArray();

        Assert.DoesNotContain(sessions, s => s.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Delete_revokes_session_so_its_cookie_stops_working()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (victim, token) = await factory.LoginAdminAsync();
        var id = await factory.SessionIdAsync(token);

        var response = await admin.DeleteAsync($"/api/admin/sessions/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/me")).StatusCode);
        // Already inactive -> 404.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_unknown_session_returns_404()
    {
        var (admin, _) = await factory.LoginAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/sessions/{Guid.NewGuid()}")).StatusCode);
    }
}

public class AuthorizationConventionTests
{
    [Fact]
    public void Every_controller_action_declares_Authorize_or_AllowAnonymous()
    {
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(controllers);

        var missing = new List<string>();
        foreach (var controller in controllers)
        {
            var controllerLevel = controller.IsDefined(typeof(AuthorizeAttribute), true)
                || controller.IsDefined(typeof(AllowAnonymousAttribute), true);
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsDefined(typeof(NonActionAttribute), true) && !m.IsSpecialName);
            foreach (var action in actions)
            {
                var actionLevel = action.IsDefined(typeof(AuthorizeAttribute), true)
                    || action.IsDefined(typeof(AllowAnonymousAttribute), true);
                if (!controllerLevel && !actionLevel)
                {
                    missing.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        Assert.Empty(missing);
    }
}
