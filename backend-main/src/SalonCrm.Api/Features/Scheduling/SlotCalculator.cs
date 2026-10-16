namespace SalonCrm.Api.Features.Scheduling;

public sealed record BusyInterval(DateTime StartUtc, DateTime EndUtc);

public sealed record CalculatedSlot(DateTime StartUtc, List<Guid> FreeEmployeeIds);

// Pure slot arithmetic (no DB, no clock): opening hours are local to the salon time zone,
// results are UTC instants.
public static class SlotCalculator
{
    // All grid starts of a local date for a service duration: local open + n * step, start + duration <= close.
    // Local times that do not exist (DST gap) are skipped; ambiguous ones resolve to the standard offset.
    public static List<DateTime> GridStartsUtc(DateOnly date, SalonOptions options, int durationMinutes)
    {
        var result = new List<DateTime>();
        if (!options.OpeningHours.TryGetValue(date.DayOfWeek, out var hours) || options.SlotStepMinutes <= 0)
        {
            return result;
        }

        var zone = options.Zone;
        var openMinutes = hours.Open.Hour * 60 + hours.Open.Minute;
        var closeMinutes = hours.Close.Hour * 60 + hours.Close.Minute;
        for (var m = openMinutes; m + durationMinutes <= closeMinutes; m += options.SlotStepMinutes)
        {
            var local = date.ToDateTime(new TimeOnly(m / 60, m % 60)); // Kind = Unspecified
            if (zone.IsInvalidTime(local))
            {
                continue;
            }

            result.Add(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }

        return result;
    }

    // Slots of one local date with the employees that are free for the whole service duration.
    // Slots without any free employee are still returned (empty list); callers filter as needed.
    public static List<CalculatedSlot> Compute(
        DateOnly date,
        SalonOptions options,
        int durationMinutes,
        DateTime earliestStartUtc,
        DateOnly latestDate,
        IReadOnlyDictionary<Guid, IReadOnlyList<BusyInterval>> busyByEmployee)
    {
        var result = new List<CalculatedSlot>();
        if (date > latestDate)
        {
            return result;
        }

        foreach (var start in GridStartsUtc(date, options, durationMinutes))
        {
            if (start < earliestStartUtc)
            {
                continue;
            }

            var end = start.AddMinutes(durationMinutes);
            var free = busyByEmployee
                .Where(kv => !kv.Value.Any(b => start < b.EndUtc && b.StartUtc < end))
                .Select(kv => kv.Key)
                .ToList();
            result.Add(new CalculatedSlot(start, free));
        }

        return result;
    }

    // True if startUtc can ever be a slot (on the grid, inside opening hours, not before the earliest start,
    // not after the latest date). Says nothing about employee availability.
    public static bool IsValidGridSlot(
        DateTime startUtc,
        SalonOptions options,
        int durationMinutes,
        DateTime earliestStartUtc,
        DateOnly latestDate)
    {
        startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        if (startUtc < earliestStartUtc)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, options.Zone);
        var date = DateOnly.FromDateTime(local);
        return date <= latestDate && GridStartsUtc(date, options, durationMinutes).Contains(startUtc);
    }

    // [start, end) of a local date as UTC instants.
    public static (DateTime StartUtc, DateTime EndUtc) DayBoundsUtc(DateOnly date, TimeZoneInfo zone)
    {
        return (MidnightUtc(date), MidnightUtc(date.AddDays(1)));

        DateTime MidnightUtc(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            if (zone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
    }
}
