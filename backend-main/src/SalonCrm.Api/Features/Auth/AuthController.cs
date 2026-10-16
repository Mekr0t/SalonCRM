using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Auth;

// Bounded so huge payloads are rejected before hashing.
public sealed record LoginRequest(
    [Required, MaxLength(254)] string Email,
    [Required, MaxLength(256)] string Password);

public sealed record CurrentUserDto(Guid Id, string Email, string FullName, UserRole Role);

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    SessionService sessions,
    IPasswordHasher<User> hasher,
    IConfiguration configuration,
    TimeProvider timeProvider) : ControllerBase
{
    private const int MaxUserAgentLength = 512;

    // Verified against when the e-mail is unknown, so response time does not reveal whether it exists.
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<User>().HashPassword(null!, "dummy-password-for-timing"));

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        var result = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash.Value, request.Password);
        if (user is null || !user.IsActive || result == PasswordVerificationResult.Failed)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Nesprávny e-mail alebo heslo.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        user.LastLoginAt = timeProvider.GetUtcNow().UtcDateTime;

        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > MaxUserAgentLength)
        {
            userAgent = userAgent[..MaxUserAgentLength];
        }

        var (session, token) = await sessions.CreateAsync(
            user,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            ct); // also persists the user changes (same DbContext)

        Response.Cookies.Append(SessionDefaults.CookieName, token, CookieOptions(session.ExpiresAt));
        return Ok(ToDto(user));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sessions.RevokeAsync(User.GetSessionId(), ct);
        Response.Cookies.Delete(SessionDefaults.CookieName, CookieOptions(null));
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct)
    {
        var id = User.GetUserId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == id, ct);
        return ToDto(user);
    }

    private static CurrentUserDto ToDto(User u) => new(u.Id, u.Email, u.FullName, u.Role);

    private CookieOptions CookieOptions(DateTime? expiresAtUtc) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Secure = configuration.GetValue("Cookie:Secure", true),
        Expires = expiresAtUtc is { } e ? new DateTimeOffset(e, TimeSpan.Zero) : null,
    };
}
