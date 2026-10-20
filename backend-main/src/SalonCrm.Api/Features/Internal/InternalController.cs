using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Internal;

public sealed record IntrospectRequest([Required] string SessionToken);
public sealed record ExportCustomerDto(Guid CustomerId, DateTime CreatedAt);
public sealed record ExportAppointmentDto(
    Guid AppointmentId, Guid CustomerId, Guid EmployeeId, Guid ServiceId,
    ServiceCategory ServiceCategory, DateTime StartsAt, DateTime CreatedAt,
    DateTime? CancelledAt, AppointmentStatus Status, decimal Price);
public sealed record MlExportDto(
    DateTime GeneratedAt, List<ExportCustomerDto> Customers, List<ExportAppointmentDto> Appointments);

[ApiController]
[Route("api/internal")]
[AllowAnonymous]
[InternalApiKey]
public sealed class InternalController(AppDbContext db, SessionService sessions, TimeProvider clock) : ControllerBase
{
    [HttpPost("sessions/introspect")]
    public async Task<IActionResult> Introspect(IntrospectRequest request, CancellationToken ct)
    {
        var valid = await sessions.ValidateAsync(request.SessionToken, ct);
        return valid is null
            ? Ok(new { active = false })
            : Ok(new
            {
                active = true,
                userId = valid.User.Id,
                role = valid.User.Role,
                fullName = valid.User.FullName,
            });
    }

    [HttpGet("ml/export")]
    public async Task<MlExportDto> Export(CancellationToken ct)
    {
        // Both projections see one consistent snapshot, even during concurrent CRM writes.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var generatedAt = clock.GetUtcNow().UtcDateTime;
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new ExportCustomerDto(c.Id, c.CreatedAt)).ToListAsync(ct);
        var appointments = await db.Appointments.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new ExportAppointmentDto(
                a.Id, a.CustomerId, a.EmployeeId, a.ServiceId, a.Service.Category,
                a.StartsAt, a.CreatedAt, a.CancelledAt, a.Status, a.Price)).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new MlExportDto(generatedAt, customers, appointments);
    }
}
