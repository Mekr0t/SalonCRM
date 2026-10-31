using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Appointments;
using SalonCrm.Api.Features.Common;

namespace SalonCrm.Api.Features.Customers;

public sealed record RetentionActionDto(
    Guid Id,
    RetentionChannel Channel,
    string Note,
    decimal? ChurnProbability,
    DateTime CreatedAt,
    PersonRefDto CreatedBy);

// Channel is nullable so an omitted channel is a 400 (an unknown value is rejected by the enum converter).
public sealed record CreateRetentionActionRequest(RetentionChannel? Channel, string? Note, decimal? ChurnProbability);

[ApiController]
[Route("api/customers/{customerId:guid}/retention-actions")]
public class RetentionActionsController(AppDbContext db, TimeProvider timeProvider) : ControllerBase
{
    private const int MaxNoteLength = 2000;

    [HttpGet]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> List(Guid customerId, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            return NotFound();
        }

        var items = await db.RetentionActions.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new RetentionActionDto(
                a.Id, a.Channel, a.Note, a.ChurnProbability, a.CreatedAt,
                new PersonRefDto(a.CreatedByUser.Id, a.CreatedByUser.FullName)))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create(Guid customerId, CreateRetentionActionRequest request, CancellationToken ct)
    {
        if (request.Channel is not { } channel)
        {
            return this.BadRequestProblem(Messages.RetentionChannelRequired);
        }

        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > MaxNoteLength)
        {
            return this.BadRequestProblem(Messages.RetentionNoteInvalid);
        }

        if (request.ChurnProbability is < 0m or > 1m)
        {
            return this.BadRequestProblem(Messages.RetentionProbabilityInvalid);
        }

        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var action = new RetentionAction
        {
            CustomerId = customerId,
            CreatedByUserId = userId,
            Channel = channel,
            Note = note,
            ChurnProbability = request.ChurnProbability is { } p ? decimal.Round(p, 3, MidpointRounding.AwayFromZero) : null,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };
        db.RetentionActions.Add(action);
        await db.SaveChangesAsync(ct);

        var createdBy = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new PersonRefDto(u.Id, u.FullName)).SingleAsync(ct);
        var dto = new RetentionActionDto(
            action.Id, action.Channel, action.Note, action.ChurnProbability, action.CreatedAt, createdBy);
        return Created($"/api/customers/{customerId}/retention-actions/{action.Id}", dto);
    }
}
