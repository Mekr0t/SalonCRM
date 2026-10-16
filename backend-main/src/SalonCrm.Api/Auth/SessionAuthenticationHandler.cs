using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SalonCrm.Api.Auth;

public class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionDefaults.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var valid = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (valid is null)
        {
            return AuthenticateResult.Fail("Invalid or expired session.");
        }

        var user = valid.User;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(ClaimsExtensions.SessionIdClaim, valid.Session.Id.ToString()),
        ], Scheme.Name);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteAsync(StatusCodes.Status401Unauthorized, "Unauthorized", "Authentication is required.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteAsync(StatusCodes.Status403Forbidden, "Forbidden", "You do not have permission to access this resource.");

    private async Task WriteAsync(int status, string title, string detail)
    {
        Response.StatusCode = status;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
