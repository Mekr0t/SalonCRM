using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Common;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Features.Public;

public sealed record PublicServiceDto(
    Guid Id, string Name, ServiceCategory Category, string? Description, int DurationMinutes, decimal Price);

public sealed record AvailabilityQuery
{
    [Required] public Guid? ServiceId { get; init; }
    [Required] public DateOnly? Date { get; init; }
    public Guid? EmployeeId { get; init; }
}

public sealed record CreateBookingRequest(
    [Required] Guid? ServiceId,
    Guid? EmployeeId,
    [Required] DateTime? StartsAt,
    [Required, MaxLength(200)] string FullName,
    [Required, EmailAddress, MaxLength(254)] string Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(1000)] string? Note,
    bool? GdprConsent); // checked in the action so both "missing" and "false" give the same Slovak message

public sealed record BookingResponse(
    Guid AppointmentId, DateTime StartsAt, string ServiceName, string EmployeeName, decimal Price);

[ApiController]
[AllowAnonymous]
[Route("api/public")]
public class PublicController(AppDbContext db, AvailabilityService availability, BookingService booking)
    : ControllerBase
{
    [HttpGet("services")]
    public async Task<List<PublicServiceDto>> Services(CancellationToken ct) =>
        await db.Services.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .Select(s => new PublicServiceDto(s.Id, s.Name, s.Category, s.Description, s.DurationMinutes, s.Price))
            .ToListAsync(ct);

    [HttpGet("services/{id:guid}/employees")]
    public async Task<IActionResult> Employees(Guid id, CancellationToken ct)
    {
        if (await availability.FindActiveServiceAsync(id, ct) is null)
        {
            return NotFound();
        }

        return Ok(await availability.EligibleEmployeesAsync(id, ct));
    }

    [HttpGet("availability")]
    public async Task<IActionResult> Availability([FromQuery] AvailabilityQuery query, CancellationToken ct)
    {
        var service = await availability.FindActiveServiceAsync(query.ServiceId!.Value, ct);
        if (service is null)
        {
            return NotFound();
        }

        var day = await availability.GetDayAsync(
            service, query.Date!.Value, query.EmployeeId, BookingMode.Public, ct);
        return Ok(day.Slots);
    }

    [HttpPost("bookings")]
    [EnableRateLimiting("booking")]
    public async Task<IActionResult> CreateBooking(CreateBookingRequest request, CancellationToken ct)
    {
        if (request.GdprConsent != true)
        {
            return this.BadRequestProblem(Messages.GdprRequired);
        }

        var service = await availability.FindActiveServiceAsync(request.ServiceId!.Value, ct);
        if (service is null)
        {
            return NotFound();
        }

        var startsAt = BookingService.ToUtc(request.StartsAt!.Value);
        var outcome = await booking.CreateAsync(
            new BookingInput(
                service, request.EmployeeId, startsAt, request.FullName, request.Email,
                request.Phone, request.Note, null),
            BookingMode.Public, AppointmentSource.Online, ct);
        if (outcome.Failure != BookingFailure.None)
        {
            return BookingService.ToProblem(this, outcome.Failure);
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new BookingResponse(outcome.AppointmentId, startsAt, service.Name, outcome.Employee!.FullName, service.Price));
    }
}
