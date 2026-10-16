using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Scheduling;

public enum BookingMode
{
    Public, // MinLeadMinutes applies
    Staff,  // no lead time, but never in the past
}

public sealed record EmployeeRef(Guid Id, string FullName);

public sealed record SlotOffer(DateTime StartsAt, List<EmployeeRef> Employees);

// Free slots of one day plus per-employee count of non-cancelled appointments that day
// (input for PickEmployee).
public sealed record DayAvailability(List<SlotOffer> Slots, Dictionary<Guid, int> DayCounts);

public sealed class AvailabilityService(AppDbContext db, IOptions<SalonOptions> options, TimeProvider timeProvider)
{
    private SalonOptions Salon => options.Value;

    public TimeZoneInfo Zone => Salon.Zone;

    public Task<Service?> FindActiveServiceAsync(Guid serviceId, CancellationToken ct) =>
        db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId && s.IsActive, ct);

    // Active users (any role) who perform the service, ordered by name.
    public async Task<List<EmployeeRef>> EligibleEmployeesAsync(Guid serviceId, CancellationToken ct)
    {
        var employees = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.UserServices.Any(us => us.ServiceId == serviceId))
            .Select(u => new EmployeeRef(u.Id, u.FullName))
            .ToListAsync(ct);
        return employees.OrderBy(e => e.FullName, StringComparer.Ordinal).ThenBy(e => e.Id).ToList();
    }

    public (DateTime EarliestUtc, DateOnly LatestDate) Window(BookingMode mode)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var earliest = mode == BookingMode.Public ? now.AddMinutes(Salon.MinLeadMinutes) : now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, Salon.Zone));
        return (earliest, today.AddDays(Salon.MaxDaysAhead));
    }

    public bool IsValidSlot(Service service, DateTime startsAtUtc, BookingMode mode)
    {
        var (earliest, latest) = Window(mode);
        return SlotCalculator.IsValidGridSlot(startsAtUtc, Salon, service.DurationMinutes, earliest, latest);
    }

    // Slots of a local date that have at least one free employee. employeeId restricts to that employee.
    public async Task<DayAvailability> GetDayAsync(
        Service service, DateOnly date, Guid? employeeId, BookingMode mode, CancellationToken ct)
    {
        var employees = await EligibleEmployeesAsync(service.Id, ct);
        if (employeeId is not null)
        {
            employees = employees.Where(e => e.Id == employeeId).ToList();
        }

        var (dayStart, dayEnd) = SlotCalculator.DayBoundsUtc(date, Salon.Zone);
        var ids = employees.Select(e => e.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.Status != AppointmentStatus.Cancelled
                && a.StartsAt < dayEnd && a.EndsAt > dayStart)
            .Select(a => new { a.EmployeeId, a.StartsAt, a.EndsAt })
            .ToListAsync(ct);

        var busy = employees.ToDictionary(
            e => e.Id,
            e => (IReadOnlyList<BusyInterval>)appointments.Where(a => a.EmployeeId == e.Id)
                .Select(a => new BusyInterval(a.StartsAt, a.EndsAt)).ToList());
        var counts = employees.ToDictionary(
            e => e.Id,
            e => appointments.Count(a => a.EmployeeId == e.Id && a.StartsAt >= dayStart && a.StartsAt < dayEnd));

        var (earliest, latest) = Window(mode);
        var byId = employees.ToDictionary(e => e.Id);
        var slots = SlotCalculator.Compute(date, Salon, service.DurationMinutes, earliest, latest, busy)
            .Where(s => s.FreeEmployeeIds.Count > 0)
            .Select(s => new SlotOffer(
                s.StartUtc,
                s.FreeEmployeeIds.Select(id => byId[id])
                    .OrderBy(e => e.FullName, StringComparer.Ordinal).ThenBy(e => e.Id).ToList()))
            .ToList();
        return new DayAvailability(slots, counts);
    }

    // "Anyone": the free employee with the fewest appointments that day; ties by name (ordinal), then id.
    public static EmployeeRef PickEmployee(IEnumerable<EmployeeRef> free, IReadOnlyDictionary<Guid, int> dayCounts) =>
        free.OrderBy(e => dayCounts.GetValueOrDefault(e.Id))
            .ThenBy(e => e.FullName, StringComparer.Ordinal)
            .ThenBy(e => e.Id)
            .First();
}
