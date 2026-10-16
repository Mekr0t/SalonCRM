using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal static class TestSalon
{
    public static SalonOptions Options() => new()
    {
        TimeZone = "Europe/Bratislava",
        SlotStepMinutes = 15,
        MinLeadMinutes = 60,
        MaxDaysAhead = 60,
        OpeningHours = new()
        {
            [DayOfWeek.Monday] = Hours(8, 18),
            [DayOfWeek.Tuesday] = Hours(8, 18),
            [DayOfWeek.Wednesday] = Hours(8, 18),
            [DayOfWeek.Thursday] = Hours(8, 18),
            [DayOfWeek.Friday] = Hours(8, 18),
            [DayOfWeek.Saturday] = Hours(8, 13),
        },
    };

    private static OpeningHoursOptions Hours(int open, int close) =>
        new() { Open = new TimeOnly(open, 0), Close = new TimeOnly(close, 0) };

    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public static DateTime LocalToUtc(DateOnly date, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), Zone);

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
}

public class SlotCalculatorTests
{
    private static readonly SalonOptions Salon = TestSalon.Options();
    private static readonly DateTime Past = TestSalon.Utc(2000, 1, 1);
    private static readonly DateOnly Far = new(2100, 1, 1);
    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<BusyInterval>> NoBusy =
        new Dictionary<Guid, IReadOnlyList<BusyInterval>>();

    // A Monday in summer time and the following Saturday / Sunday.
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateOnly Saturday = new(2026, 6, 6);
    private static readonly DateOnly Sunday = new(2026, 6, 7);

    private static List<DateTime> Local(IEnumerable<DateTime> utc) => utc.Select(TestSalon.ToLocal).ToList();

    [Fact]
    public void Monday_60_minute_service_starts_at_0800_and_ends_at_1700_local()
    {
        var local = Local(SlotCalculator.GridStartsUtc(Monday, Salon, 60));

        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), local.First());
        Assert.Equal(new DateTime(2026, 6, 1, 17, 0, 0), local.Last());
        Assert.Equal(37, local.Count);
    }

    [Fact]
    public void Saturday_last_start_is_1200_and_sunday_is_closed()
    {
        var local = Local(SlotCalculator.GridStartsUtc(Saturday, Salon, 60));

        Assert.Equal(new DateTime(2026, 6, 6, 12, 0, 0), local.Last());
        Assert.Empty(SlotCalculator.GridStartsUtc(Sunday, Salon, 60));
    }

    [Fact]
    public void Long_service_does_not_overflow_closing_time()
    {
        var local = Local(SlotCalculator.GridStartsUtc(Monday, Salon, 90));

        Assert.Equal(new DateTime(2026, 6, 1, 16, 30, 0), local.Last());
    }

    [Fact]
    public void Grid_uses_the_configured_step()
    {
        var starts = SlotCalculator.GridStartsUtc(Monday, Salon, 60);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.Equal(TimeSpan.FromMinutes(15), p.Second - p.First));

        var step30 = TestSalon.Options();
        step30.SlotStepMinutes = 30;
        var starts30 = SlotCalculator.GridStartsUtc(Monday, step30, 60);
        Assert.Equal(19, starts30.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), starts30[1] - starts30[0]);
    }

    [Fact]
    public void Busy_interval_blocks_overlapping_starts_but_back_to_back_is_allowed()
    {
        var e = Guid.NewGuid();
        var busyStart = TestSalon.LocalToUtc(Monday, 10);
        var busy = new Dictionary<Guid, IReadOnlyList<BusyInterval>>
        {
            [e] = [new BusyInterval(busyStart, busyStart.AddHours(1))],
        };

        var slots = SlotCalculator.Compute(Monday, Salon, 60, Past, Far, busy);
        var free = Local(slots.Where(s => s.FreeEmployeeIds.Contains(e)).Select(s => s.StartUtc));

        Assert.Contains(new DateTime(2026, 6, 1, 9, 0, 0), free); // ends exactly at 10:00
        Assert.DoesNotContain(new DateTime(2026, 6, 1, 9, 15, 0), free); // partial overlap at the end
        Assert.DoesNotContain(new DateTime(2026, 6, 1, 10, 0, 0), free);
        Assert.DoesNotContain(new DateTime(2026, 6, 1, 10, 45, 0), free); // partial overlap at the start
        Assert.Contains(new DateTime(2026, 6, 1, 11, 0, 0), free); // starts exactly when busy ends
        Assert.Equal(37, slots.Count);
    }

    [Fact]
    public void Only_free_employees_are_listed_per_slot()
    {
        var busyOne = Guid.NewGuid();
        var freeOne = Guid.NewGuid();
        var s = TestSalon.LocalToUtc(Monday, 10);
        var busy = new Dictionary<Guid, IReadOnlyList<BusyInterval>>
        {
            [busyOne] = [new BusyInterval(s, s.AddHours(1))],
            [freeOne] = [],
        };

        var slot = SlotCalculator.Compute(Monday, Salon, 60, Past, Far, busy).Single(x => x.StartUtc == s);

        Assert.Equal([freeOne], slot.FreeEmployeeIds);
    }

    [Fact]
    public void Earliest_start_cuts_off_the_early_slots_public_vs_staff()
    {
        var now = TestSalon.LocalToUtc(Monday, 8, 30);

        var publicSlots = SlotCalculator.Compute(Monday, Salon, 60, now.AddMinutes(Salon.MinLeadMinutes), Far, NoBusy);
        var staffSlots = SlotCalculator.Compute(Monday, Salon, 60, now, Far, NoBusy);

        Assert.Equal(new DateTime(2026, 6, 1, 9, 30, 0), TestSalon.ToLocal(publicSlots.First().StartUtc));
        Assert.Equal(new DateTime(2026, 6, 1, 8, 30, 0), TestSalon.ToLocal(staffSlots.First().StartUtc));
    }

    [Fact]
    public void Date_after_latest_allowed_date_has_no_slots()
    {
        Assert.Empty(SlotCalculator.Compute(Monday, Salon, 60, Past, Monday.AddDays(-1), NoBusy));
        Assert.NotEmpty(SlotCalculator.Compute(Monday, Salon, 60, Past, Monday, NoBusy));
    }

    [Fact]
    public void Dst_changes_the_utc_offset_of_the_same_local_opening_time()
    {
        // Spring forward: Sunday 2026-03-29.
        Assert.Equal(TestSalon.Utc(2026, 3, 28, 7), SlotCalculator.GridStartsUtc(new DateOnly(2026, 3, 28), Salon, 60).First());
        Assert.Equal(TestSalon.Utc(2026, 3, 30, 6), SlotCalculator.GridStartsUtc(new DateOnly(2026, 3, 30), Salon, 60).First());
        // Fall back: Sunday 2026-10-25.
        Assert.Equal(TestSalon.Utc(2026, 10, 24, 6), SlotCalculator.GridStartsUtc(new DateOnly(2026, 10, 24), Salon, 60).First());
        Assert.Equal(TestSalon.Utc(2026, 10, 26, 7), SlotCalculator.GridStartsUtc(new DateOnly(2026, 10, 26), Salon, 60).First());
    }

    [Fact]
    public void Nonexistent_local_times_are_skipped()
    {
        // Sunday opening hours covering the 02:00-03:00 spring-forward gap (does not occur in the real config).
        var salon = TestSalon.Options();
        salon.OpeningHours[DayOfWeek.Sunday] = new OpeningHoursOptions { Open = new TimeOnly(1, 0), Close = new TimeOnly(4, 0) };

        var local = Local(SlotCalculator.GridStartsUtc(new DateOnly(2026, 3, 29), salon, 60));

        Assert.DoesNotContain(local, l => l.Hour == 2);
        Assert.Contains(new DateTime(2026, 3, 29, 1, 45, 0), local);
        Assert.Contains(new DateTime(2026, 3, 29, 3, 0, 0), local);
    }

    [Fact]
    public void Day_bounds_follow_the_local_day_across_dst()
    {
        var (start, end) = SlotCalculator.DayBoundsUtc(new DateOnly(2026, 3, 29), TestSalon.Zone);

        Assert.Equal(TestSalon.Utc(2026, 3, 28, 23), start);
        Assert.Equal(TestSalon.Utc(2026, 3, 29, 22), end); // 23-hour day
    }

    [Fact]
    public void Valid_grid_check_accepts_slots_and_rejects_everything_else()
    {
        bool Valid(DateTime utc, int duration = 60) =>
            SlotCalculator.IsValidGridSlot(utc, Salon, duration, Past, Far);

        Assert.True(Valid(TestSalon.LocalToUtc(Monday, 8)));
        Assert.True(Valid(TestSalon.LocalToUtc(Monday, 17)));
        Assert.True(Valid(TestSalon.LocalToUtc(Monday, 9, 45)));
        Assert.False(Valid(TestSalon.LocalToUtc(Monday, 9, 10))); // off the 15-minute grid
        Assert.False(Valid(TestSalon.LocalToUtc(Monday, 7, 45))); // before opening
        Assert.False(Valid(TestSalon.LocalToUtc(Monday, 17, 15))); // would end after closing
        Assert.False(Valid(TestSalon.LocalToUtc(Monday, 17), 90));
        Assert.False(Valid(TestSalon.LocalToUtc(Saturday, 12, 15)));
        Assert.False(Valid(TestSalon.LocalToUtc(Sunday, 10)));
        Assert.False(Valid(TestSalon.LocalToUtc(Monday, 10).AddSeconds(30)));
        Assert.False(Valid(new DateTime(2026, 6, 1, 16, 30, 0, DateTimeKind.Utc))); // 18:30 local
    }

    [Fact]
    public void Valid_grid_check_enforces_earliest_start_and_latest_date()
    {
        var slot = TestSalon.LocalToUtc(Monday, 10);

        Assert.True(SlotCalculator.IsValidGridSlot(slot, Salon, 60, slot, Monday));
        Assert.False(SlotCalculator.IsValidGridSlot(slot, Salon, 60, slot.AddMinutes(1), Monday));
        Assert.False(SlotCalculator.IsValidGridSlot(slot, Salon, 60, Past, Monday.AddDays(-1)));
    }
}
