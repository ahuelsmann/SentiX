namespace SentiX.Risk;

/// <summary>
/// Deterministic conversion of UTC instants to <c>America/New_York</c> wall-clock time.
/// Uses the IANA zone (<c>TimeZoneInfo.FindSystemTimeZoneById("America/New_York")</c>) and falls back to the
/// US Energy Policy Act 2005 rules (DST from the 2nd Sunday in March 02:00 to the 1st Sunday in November 02:00
/// local time, EST -05:00 / EDT -04:00) when the zone database is unavailable (e.g. stripped containers).
/// </summary>
public static class NewYorkTime
{
    /// <summary>IANA time zone identifier of the US equity market.</summary>
    public const string TimeZoneId = "America/New_York";

    private static readonly TimeSpan StandardOffset = TimeSpan.FromHours(-5);
    private static readonly TimeSpan DaylightOffset = TimeSpan.FromHours(-4);

    private static readonly TimeZoneInfo? Zone = TryResolve(TimeZoneInfo.FindSystemTimeZoneById);

    /// <summary>Converts a UTC (or any offset) instant to New York local time with the correct EST/EDT offset.</summary>
    public static DateTimeOffset ToEastern(DateTimeOffset instant) => Convert(instant, Zone);

    /// <summary>Returns the New York calendar date of the instant.</summary>
    public static DateOnly EasternDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToEastern(instant).DateTime);

    internal static DateTimeOffset Convert(DateTimeOffset instant, TimeZoneInfo? zone) =>
        zone is null ? ConvertWithUsRules(instant) : TimeZoneInfo.ConvertTime(instant, zone);

    internal static TimeZoneInfo? TryResolve(Func<string, TimeZoneInfo> finder)
    {
        try
        {
            return finder(TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    internal static DateTimeOffset ConvertWithUsRules(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        var year = utc.Year;

        // 2nd Sunday of March, 02:00 EST == 07:00 UTC.
        var dstStartUtc = NthSunday(year, 3, 2).AddHours(7);

        // 1st Sunday of November, 02:00 EDT == 06:00 UTC.
        var dstEndUtc = NthSunday(year, 11, 1).AddHours(6);

        var offset = utc >= dstStartUtc && utc < dstEndUtc ? DaylightOffset : StandardOffset;
        return instant.ToOffset(offset);
    }

    private static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(daysUntilSunday + (7 * (n - 1)));
    }
}
