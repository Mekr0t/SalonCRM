using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Common;

namespace SalonCrm.Api.Features.Scheduling;

public enum BookingFailure
{
    None,
    InvalidTime, // 400: can never be a slot
    SlotTaken,   // 409: valid slot, but no (suitable) free employee
}

public sealed record BookingInput(
    Service Service,
    Guid? EmployeeId,
    DateTime StartsAtUtc,
    string FullName,
    string Email,
    string? Phone,
    string? CustomerNote,
    string? StaffNote);

public sealed record BookingOutcome(BookingFailure Failure, Guid AppointmentId = default, EmployeeRef? Employee = null);

// Booking logic shared by the public form and the staff (phone) booking.
public sealed class BookingService(AppDbContext db, AvailabilityService availability, TimeProvider timeProvider)
{
    // Request times without an offset are treated as UTC; local ones (query-string binding) are converted.
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Validates the slot and finds the employee: the wanted one (must be eligible and free) or, for
    // wantedEmployeeId == null, the "anyone" pick. excludeAppointmentId = appointment being rescheduled.
    public async Task<(BookingFailure Failure, EmployeeRef? Employee)> FindEmployeeAsync(
        Service service, DateTime startsAtUtc, Guid? wantedEmployeeId, BookingMode mode,
        Guid? excludeAppointmentId, CancellationToken ct)
    {
        if (!availability.IsValidSlot(service, startsAtUtc, mode))
        {
            return (BookingFailure.InvalidTime, null);
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startsAtUtc, availability.Zone));
        var day = await availability.GetDayAsync(service, date, null, mode, ct, excludeAppointmentId);
        var offer = day.Slots.FirstOrDefault(s => s.StartsAt == startsAtUtc);
        EmployeeRef? employee = null;
        if (offer is not null)
        {
            employee = wantedEmployeeId is { } wanted
                ? offer.Employees.FirstOrDefault(e => e.Id == wanted)
                : AvailabilityService.PickEmployee(offer.Employees, day.DayCounts);
        }

        return employee is null ? (BookingFailure.SlotTaken, null) : (BookingFailure.None, employee);
    }

    public async Task<BookingOutcome> CreateAsync(
        BookingInput input, BookingMode mode, AppointmentSource source, CancellationToken ct)
    {
        var (failure, employee) = await FindEmployeeAsync(
            input.Service, input.StartsAtUtc, input.EmployeeId, mode, null, ct);
        if (employee is null)
        {
            return new BookingOutcome(failure);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var email = input.Email.Trim();
        var emailNormalized = email.ToLowerInvariant();
        var phone = NullIfBlank(input.Phone);
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
                    FullName = input.FullName.Trim(),
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
                ServiceId = input.Service.Id,
                StartsAt = input.StartsAtUtc,
                EndsAt = input.StartsAtUtc.AddMinutes(input.Service.DurationMinutes),
                Status = AppointmentStatus.Scheduled,
                Price = input.Service.Price,
                Source = source,
                CustomerNote = NullIfBlank(input.CustomerNote),
                StaffNote = NullIfBlank(input.StaffNote),
                CreatedAt = now,
                UpdatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return new BookingOutcome(BookingFailure.None, appointmentId, employee);
            }
            catch (DbUpdateException ex) when (IsSqlState(ex, PostgresErrorCodes.ExclusionViolation))
            {
                return new BookingOutcome(BookingFailure.SlotTaken);
            }
            catch (DbUpdateException ex) when (isNew && attempt == 0 && IsSqlState(ex, PostgresErrorCodes.UniqueViolation))
            {
                db.ChangeTracker.Clear(); // same new customer created concurrently: re-read and reuse it
            }
        }

        return new BookingOutcome(BookingFailure.SlotTaken); // unreachable in practice
    }

    // PostgresException directly (ExecuteUpdate) or wrapped (SaveChanges).
    public static bool IsSqlState(Exception ex, string sqlState) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState == sqlState;

    public static ObjectResult ToProblem(ControllerBase controller, BookingFailure failure) => failure switch
    {
        BookingFailure.InvalidTime => controller.BadRequestProblem(Messages.InvalidTime),
        _ => controller.ConflictProblem(Messages.SlotTaken),
    };
}
