using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;

namespace SalonCrm.Api.Features.Sessions;

public sealed record AdminSessionDto(
    Guid Id,
    Guid UserId,
    string UserFullName,
    string UserEmail,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    string? IpAddress,
    string? UserAgent,
    bool IsCurrent);

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/sessions")]
public class AdminSessionsController(SessionService sessions) : ControllerBase
{
    [HttpGet]
    public async Task<List<AdminSessionDto>> List(CancellationToken ct)
    {
        var current = User.GetSessionId();
        return await sessions.ActiveSessions().AsNoTracking()
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new AdminSessionDto(
                s.Id, s.UserId, s.User.FullName, s.User.Email, s.CreatedAt, s.LastSeenAt,
                s.IpAddress, s.UserAgent, s.Id == current))
            .ToListAsync(ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (!await sessions.ActiveSessions().AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        await sessions.RevokeAsync(id, ct);
        return NoContent();
    }
}
