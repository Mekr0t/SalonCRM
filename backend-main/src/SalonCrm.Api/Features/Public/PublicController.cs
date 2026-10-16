using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
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
public class PublicController(AppDbContext db, AvailabilityService availability, TimeProvider timeProvider)
    : ControllerBase
{
    private const string SlotTakenDetail = "Vybraný termín už nie je voľný.";

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
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request",
                detail: "Na rezerváciu je potrebný súhlas so spracovaním osobných údajov.");
        }

        var service = await availability.FindActiveServiceAsync(request.ServiceId!.Value, ct);
        if (service is null)
        {
            return NotFound();
        }

        var startsAt = request.StartsAt!.Value.Kind switch
        {
            DateTimeKind.Utc => request.StartsAt.Value,
            DateTimeKind.Local => request.StartsAt.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(request.StartsAt.Value, DateTimeKind.Utc), // no offset given: treat as UTC
        };

        if (!availability.IsValidSlot(service, startsAt, BookingMode.Public))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request",
                detail: "Neplatný čas termínu.");
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startsAt, availability.Zone));
        var day = await availability.GetDayAsync(service, date, null, BookingMode.Public, ct);
        var offer = day.Slots.FirstOrDefault(s => s.StartsAt == startsAt);
        EmployeeRef? employee = null;
        if (offer is not null)
        {
            employee = request.EmployeeId is { } wanted
                ? offer.Employees.FirstOrDefault(e => e.Id == wanted)
                : AvailabilityService.PickEmployee(offer.Employees, day.DayCounts);
        }

        if (employee is null)
        {
            return SlotTaken();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var email = request.Email.Trim();
        var emailNormalized = email.ToLowerInvariant();
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var appointmentId = Guid.CreateVersion7();

        // Second attempt only after losing the race to create the same new customer.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.EmailNormalized == emailNormalized, ct);
            var isNew = customer is null;
            if (customer is null)
            {
                customer = new Customer
                {
                    Email = email,
                    EmailNormalized = emailNormalized,
                    FullName = request.FullName.Trim(),
                    Phone = phone,
                    GdprConsentAt = now,
                    CreatedAt = now,
                };
                db.Customers.Add(customer);
            }
            else if (customer.Phone is null && phone is not null)
            {
                customer.Phone = phone; // name and consent timestamp of an existing customer stay untouched
            }

            db.Appointments.Add(new Appointment
            {
                Id = appointmentId,
                CustomerId = customer.Id,
                EmployeeId = employee.Id,
                ServiceId = service.Id,
                StartsAt = startsAt,
                EndsAt = startsAt.AddMinutes(service.DurationMinutes),
                Status = AppointmentStatus.Scheduled,
                Price = service.Price,
                Source = AppointmentSource.Online,
                CustomerNote = note,
                CreatedAt = now,
                UpdatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return StatusCode(
                    StatusCodes.Status201Created,
                    new BookingResponse(appointmentId, startsAt, service.Name, employee.FullName, service.Price));
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
            {
                return SlotTaken();
            }
            catch (DbUpdateException ex) when (isNew && attempt == 0
                && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // same new customer created concurrently: re-read and reuse it
            }
        }

        return SlotTaken(); // unreachable in practice
    }

    private ObjectResult SlotTaken() =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: SlotTakenDetail);
}
