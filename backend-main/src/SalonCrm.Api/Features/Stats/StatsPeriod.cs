using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Features.Stats;

// A statistics period in local salon dates (inclusive) and the matching UTC interval [StartUtc, EndUtc).
public sealed record StatsPeriod(DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtc)
{
    public const int MaxDays = 731;

    private static readonly DateOnly MinDate = new(1900, 1, 1);
    private static readonly DateOnly MaxDate = new(2999, 12, 31);

    public static DateOnly Today(TimeProvider time, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, zone));

    // Defaults: from = first day of the current local month, to = today (local).
    public static (StatsPeriod? Period, string? Error) Resolve(DateOnly? from, DateOnly? to, SalonOptions salon, TimeProvider time)
    {
        var zone = salon.Zone;
        var today = Today(time, zone);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;

        if (start > end)
        {
            return (null, "Parameter 'from' must not be later than 'to'.");
        }

        if (end.DayNumber - start.DayNumber > MaxDays)
        {
            return (null, $"The requested period must not exceed {MaxDays} days.");
        }

        if (start < MinDate || end > MaxDate)
        {
            return (null, "Dates must be between 1900-01-01 and 2999-12-31.");
        }

        var (startUtc, endUtc) = UtcRange(start, end, zone);
        return (new StatsPeriod(start, end, startUtc, endUtc), null);
    }

    // [local midnight of from, local midnight of to + 1 day) as UTC instants. Built from SlotCalculator.DayBoundsUtc,
    // so a DST day keeps its real length (23 or 25 hours) and the bounds are exact for any zone offset.
    public static (DateTime StartUtc, DateTime EndUtc) UtcRange(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone)
    {
        var (startUtc, _) = SlotCalculator.DayBoundsUtc(fromInclusive, zone);
        var (_, endUtc) = SlotCalculator.DayBoundsUtc(toInclusive, zone);
        return (startUtc, endUtc);
    }

    // Opening-hours minutes over the local dates [from, to]; days missing from OpeningHours are closed.
    public static int OpeningMinutes(DateOnly from, DateOnly to, SalonOptions salon)
    {
        var total = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (salon.OpeningHours.TryGetValue(day.DayOfWeek, out var hours))
            {
                total += (int)(hours.Close - hours.Open).TotalMinutes;
            }
        }

        return total;
    }
}
