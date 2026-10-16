using Microsoft.Extensions.Options;

namespace SalonCrm.Api.Features.Scheduling;

public sealed class OpeningHoursOptions
{
    public TimeOnly Open { get; set; }
    public TimeOnly Close { get; set; }
}

public sealed class SalonOptions
{
    public const string SectionName = "Salon";

    private TimeZoneInfo? _zone;

    public string TimeZone { get; set; } = "Europe/Bratislava";
    public int SlotStepMinutes { get; set; } = 15;
    public int MinLeadMinutes { get; set; } = 60;
    public int MaxDaysAhead { get; set; } = 60;

    // A day missing from the dictionary means the salon is closed.
    public Dictionary<DayOfWeek, OpeningHoursOptions> OpeningHours { get; set; } = [];

    // Salon time zone (resolved lazily; ValidateOnStart guarantees it resolves).
    public TimeZoneInfo Zone => _zone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public sealed class SalonOptionsValidator : IValidateOptions<SalonOptions>
{
    public ValidateOptionsResult Validate(string? name, SalonOptions options)
    {
        var errors = new List<string>();

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            errors.Add($"Salon:TimeZone '{options.TimeZone}' cannot be resolved.");
        }

        if (options.SlotStepMinutes <= 0)
        {
            errors.Add("Salon:SlotStepMinutes must be greater than 0.");
        }

        if (options.MinLeadMinutes < 0)
        {
            errors.Add("Salon:MinLeadMinutes must not be negative.");
        }

        if (options.MaxDaysAhead < 0)
        {
            errors.Add("Salon:MaxDaysAhead must not be negative.");
        }

        foreach (var (day, hours) in options.OpeningHours)
        {
            if (hours.Open >= hours.Close)
            {
                errors.Add($"Salon:OpeningHours:{day}: Open must be earlier than Close.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
