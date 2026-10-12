using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalonCrm.Api.Data;

namespace SalonCrm.Api.Features.Health;

[ApiController]
[AllowAnonymous]
[Route("api/health")]
public class HealthController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        bool canConnect;
        try
        {
            canConnect = await db.Database.CanConnectAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            canConnect = false;
        }

        return canConnect
            ? Ok(new { status = "ok", database = "ok" })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "degraded", database = "unavailable" });
    }
}
