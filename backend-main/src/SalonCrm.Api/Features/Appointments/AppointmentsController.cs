using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Common;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Features.Appointments;

public sealed record PersonRefDto(Guid Id, string FullName);

public sealed record AppointmentServiceDto(Guid Id, string Name, ServiceCategory Category, int DurationMinutes);

public sealed record AppointmentListItemDto(
    Guid Id,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentStatus Status,
    AppointmentSource Source,
    decimal Price,
    AppointmentServiceDto Service,
    PersonRefDto Employee,
    PersonRefDto Customer);

public sealed record AppointmentCustomerDto(Guid Id, string FullName, string Email, string? Phone);

public sealed record AppointmentPaymentDto(decimal Amount, PaymentMethod Method, DateTime PaidAt, PersonRefDto RecordedBy);

public sealed record AppointmentDetailDto(
    Guid Id,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentStatus Status,
    AppointmentSource Source,
    decimal Price,
    AppointmentServiceDto Service,
    PersonRefDto Employee,
    AppointmentCustomerDto Customer,
    string? CustomerNote,
    string? StaffNote,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt,
    AppointmentPaymentDto? Payment);

public sealed record ListAppointmentsQuery
{
    [Required] public DateTime? From { get; init; }
    [Required] public DateTime? To { get; init; }
    [EnumDataType(typeof(AppointmentStatus))] public AppointmentStatus? Status { get; init; }
    public Guid? EmployeeId { get; init; }
}

public sealed record CreateStaffAppointmentRequest(
    [Required] Guid? ServiceId,
    Guid? EmployeeId,
    [Required] DateTime? StartsAt,
    [Required, MaxLength(200)] string FullName,
    [Required, EmailAddress, MaxLength(254)] string Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(1000)] string? CustomerNote,
    [MaxLength(2000)] string? StaffNote,
    bool? GdprConsent); // checked in the action so "missing" and "false" give the same Slovak message

public sealed record UpdateAppointmentRequest(
    DateTime? StartsAt,
    Guid? EmployeeId,
    [MaxLength(2000)] string? StaffNote);

public sealed record CompleteAppointmentRequest(
    [Required, EnumDataType(typeof(PaymentMethod))] PaymentMethod? PaymentMethod,
    decimal? Amount);

[ApiController]
[Authorize(Roles = Roles.Staff)]
[Route("api/appointments")]
public class AppointmentsController(
    AppDbContext db,
    AvailabilityService availability,
    BookingService booking,
    TimeProvider timeProvider) : ControllerBase
{
    private const int MaxRangeDays = 92;
    private const decimal MaxAmount = 10_000m;


    private static readonly Expression<Func<Appointment, AppointmentListItemDto>> ListProjection = a =>
        new AppointmentListItemDto(
            a.Id, a.StartsAt, a.EndsAt, a.Status, a.Source, a.Price,
            new AppointmentServiceDto(a.Service.Id, a.Service.Name, a.Service.Category, a.Service.DurationMinutes),
            new PersonRefDto(a.Employee.Id, a.Employee.FullName),
            new PersonRefDto(a.Customer.Id, a.Customer.FullName));

    private static readonly Expression<Func<Appointment, AppointmentDetailDto>> DetailProjection = a =>
        new AppointmentDetailDto(
            a.Id, a.StartsAt, a.EndsAt, a.Status, a.Source, a.Price,
            new AppointmentServiceDto(a.Service.Id, a.Service.Name, a.Service.Category, a.Service.DurationMinutes),
            new PersonRefDto(a.Employee.Id, a.Employee.FullName),
            new AppointmentCustomerDto(a.Customer.Id, a.Customer.FullName, a.Customer.Email, a.Customer.Phone),
            a.CustomerNote, a.StaffNote, a.CreatedAt, a.UpdatedAt, a.CompletedAt, a.CancelledAt,
            a.Payment == null
                ? null
                : new AppointmentPaymentDto(
                    a.Payment.Amount, a.Payment.Method, a.Payment.PaidAt,
                    new PersonRefDto(a.Payment.RecordedByUser.Id, a.Payment.RecordedByUser.FullName)));

    private Guid Me => User.GetUserId();

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListAppointmentsQuery query, CancellationToken ct)
    {
        var from = BookingService.ToUtc(query.From!.Value);
        var to = BookingService.ToUtc(query.To!.Value);
        if (to <= from)
        {
            return this.BadRequestProblem("Parameter 'to' must be later than 'from'.");
        }

        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            return this.BadRequestProblem($"The requested range must not exceed {MaxRangeDays} days.");
        }

        if (query.EmployeeId is { } employeeId && !User.IsAdmin() && employeeId != Me)
        {
            return ForeignEmployee();
        }

        var appointments = db.Appointments.AsNoTracking().VisibleTo(User)
            .Where(a => a.StartsAt < to && a.EndsAt > from);
        if (query.Status is { } status)
        {
            appointments = appointments.Where(a => a.Status == status);
        }

        if (query.EmployeeId is { } wanted)
        {
            appointments = appointments.Where(a => a.EmployeeId == wanted);
        }

        return Ok(await appointments.OrderBy(a => a.StartsAt).ThenBy(a => a.Id).Select(ListProjection).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var dto = await LoadDetailAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateStaffAppointmentRequest request, CancellationToken ct)
    {
        var isAdmin = User.IsAdmin();
        if (!isAdmin && request.EmployeeId is { } other && other != Me)
        {
            return ForeignEmployee();
        }

        if (request.GdprConsent != true)
        {
            return this.BadRequestProblem(Messages.GdprRequired);
        }

        var service = await availability.FindActiveServiceAsync(request.ServiceId!.Value, ct);
        if (service is null)
        {
            return NotFound();
        }

        // A User always books for himself (and must perform the service, else the slot is "taken").
        Guid? employeeId = isAdmin ? request.EmployeeId : Me;
        var outcome = await booking.CreateAsync(
            new BookingInput(
                service, employeeId, BookingService.ToUtc(request.StartsAt!.Value), request.FullName,
                request.Email, request.Phone, request.CustomerNote, request.StaffNote),
            BookingMode.Staff, AppointmentSource.Staff, ct);
        if (outcome.Failure != BookingFailure.None)
        {
            return BookingService.ToProblem(this, outcome.Failure);
        }

        var dto = await LoadDetailAsync(outcome.AppointmentId, ct);
        return Created($"/api/appointments/{outcome.AppointmentId}", dto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAppointmentRequest request, CancellationToken ct)
    {
        var current = await db.Appointments.AsNoTracking().VisibleTo(User)
            .Where(a => a.Id == id)
            .Select(a => new { a.Status, a.StartsAt, a.EmployeeId, a.ServiceId, a.StaffNote })
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            return NotFound();
        }

        if (request.EmployeeId is { } wantedEmployee && !User.IsAdmin() && wantedEmployee != Me)
        {
            return ForeignEmployee();
        }

        var now = Now;
        var newStart = request.StartsAt is { } s ? BookingService.ToUtc(s) : current.StartsAt;
        var newEmployee = request.EmployeeId ?? current.EmployeeId;
        var newNote = request.StaffNote is null ? current.StaffNote : BookingService.NullIfBlank(request.StaffNote);

        if (newStart != current.StartsAt || newEmployee != current.EmployeeId)
        {
            if (current.Status != AppointmentStatus.Scheduled)
            {
                return this.ConflictProblem(Messages.AppointmentWrongState);
            }

            // The service may have been deactivated since; its duration is still what the appointment needs.
            var service = await db.Services.AsNoTracking().FirstAsync(x => x.Id == current.ServiceId, ct);
            var (failure, _) = await booking.FindEmployeeAsync(
                service, newStart, newEmployee, BookingMode.Staff, id, ct);
            if (failure != BookingFailure.None)
            {
                return BookingService.ToProblem(this, failure);
            }

            var newEnd = newStart.AddMinutes(service.DurationMinutes);
            int rows;
            try
            {
                rows = await db.Appointments.VisibleTo(User)
                    .Where(a => a.Id == id && a.Status == AppointmentStatus.Scheduled)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.StartsAt, newStart)
                        .SetProperty(a => a.EndsAt, newEnd)
                        .SetProperty(a => a.EmployeeId, newEmployee)
                        .SetProperty(a => a.StaffNote, newNote)
                        .SetProperty(a => a.UpdatedAt, now), ct);
            }
            catch (Exception ex) when (BookingService.IsSqlState(ex, PostgresErrorCodes.ExclusionViolation))
            {
                return BookingService.ToProblem(this, BookingFailure.SlotTaken);
            }

            if (rows == 0)
            {
                return this.ConflictProblem(Messages.AppointmentWrongState); // state changed concurrently
            }
        }
        else if (request.StaffNote is not null)
        {
            await db.Appointments.VisibleTo(User)
                .Where(a => a.Id == id)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.StaffNote, newNote)
                    .SetProperty(a => a.UpdatedAt, now), ct);
        }

        return Ok(await LoadDetailAsync(id, ct));
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CompleteAppointmentRequest request, CancellationToken ct)
    {
        var price = await db.Appointments.AsNoTracking().VisibleTo(User)
            .Where(a => a.Id == id)
            .Select(a => (decimal?)a.Price)
            .FirstOrDefaultAsync(ct);
        if (price is null)
        {
            return NotFound();
        }

        var amount = request.Amount ?? price.Value;
        if (amount < 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
        {
            return this.BadRequestProblem(Messages.InvalidAmount);
        }

        var now = Now;
        var recordedBy = Me;
        bool done;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var rows = await db.Appointments.VisibleTo(User)
                .Where(a => a.Id == id && a.Status == AppointmentStatus.Scheduled && a.StartsAt <= now)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.Status, AppointmentStatus.Completed)
                    .SetProperty(a => a.CompletedAt, now)
                    .SetProperty(a => a.UpdatedAt, now), ct);
            done = rows == 1;
            if (done)
            {
                db.Payments.Add(new Payment
                {
                    AppointmentId = id,
                    Amount = amount,
                    Method = request.PaymentMethod!.Value,
                    PaidAt = now,
                    RecordedByUserId = recordedBy,
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                catch (DbUpdateException ex) when (BookingService.IsSqlState(ex, PostgresErrorCodes.UniqueViolation))
                {
                    done = false; // payment already exists: the transaction is rolled back on dispose
                }
            }
        }

        return done ? Ok(await LoadDetailAsync(id, ct)) : await ConflictForAsync(id, requireStarted: true, ct);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!await db.Appointments.AsNoTracking().VisibleTo(User).AnyAsync(a => a.Id == id, ct))
        {
            return NotFound();
        }

        var now = Now;
        var isAdmin = User.IsAdmin(); // a User may cancel only before the start
        var rows = await db.Appointments.VisibleTo(User)
            .Where(a => a.Id == id && a.Status == AppointmentStatus.Scheduled && (isAdmin || a.StartsAt > now))
            .ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, AppointmentStatus.Cancelled)
                .SetProperty(a => a.CancelledAt, now)
                .SetProperty(a => a.UpdatedAt, now), ct);

        return rows == 1 ? Ok(await LoadDetailAsync(id, ct)) : await ConflictForAsync(id, requireStarted: false, ct);
    }

    [HttpPost("{id:guid}/no-show")]
    public async Task<IActionResult> NoShow(Guid id, CancellationToken ct)
    {
        if (!await db.Appointments.AsNoTracking().VisibleTo(User).AnyAsync(a => a.Id == id, ct))
        {
            return NotFound();
        }

        var now = Now;
        var rows = await db.Appointments.VisibleTo(User)
            .Where(a => a.Id == id && a.Status == AppointmentStatus.Scheduled && a.StartsAt <= now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, AppointmentStatus.NoShow)
                .SetProperty(a => a.UpdatedAt, now), ct);

        return rows == 1 ? Ok(await LoadDetailAsync(id, ct)) : await ConflictForAsync(id, requireStarted: true, ct);
    }

    private Task<AppointmentDetailDto?> LoadDetailAsync(Guid id, CancellationToken ct) =>
        db.Appointments.AsNoTracking().VisibleTo(User).Where(a => a.Id == id).Select(DetailProjection)
            .FirstOrDefaultAsync(ct);

    // The atomic update touched no row: pick the 409 text that matches what is actually wrong.
    private async Task<IActionResult> ConflictForAsync(Guid id, bool requireStarted, CancellationToken ct)
    {
        var state = await db.Appointments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.Status, a.StartsAt })
            .FirstOrDefaultAsync(ct);
        if (state is null)
        {
            return NotFound();
        }

        if (state.Status != AppointmentStatus.Scheduled)
        {
            return this.ConflictProblem(Messages.AppointmentWrongState);
        }

        return this.ConflictProblem(requireStarted ? Messages.AppointmentNotStarted : Messages.AppointmentAdminOnlyCancel);
    }

    private ObjectResult ForeignEmployee() => this.ForbiddenProblem(Messages.OtherEmployee);
}
