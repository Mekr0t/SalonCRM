using System.Security.Claims;

namespace SalonCrm.Api.Auth;

public static class ClaimsExtensions
{
    public const string SessionIdClaim = "salon_sid";

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static Guid GetSessionId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(SessionIdClaim)!);
}
