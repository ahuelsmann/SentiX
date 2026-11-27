using System.Globalization;

namespace SentiX.Risk.Tests;

public sealed class NewYorkTimeTests
{
    public static TheoryData<string, int> Instants => new()
    {
        { "2026-01-15T15:00:00Z", -5 },
        { "2026-03-08T06:59:59Z", -5 }, // one second before DST start (02:00 EST)
        { "2026-03-08T07:00:00Z", -4 }, // DST start -> 03:00 EDT
        { "2026-07-15T14:00:00Z", -4 },
        { "2026-11-01T05:59:59Z", -4 }, // one second before DST end (02:00 EDT)
        { "2026-11-01T06:00:00Z", -5 }, // DST end -> 01:00 EST
        { "2026-12-31T23:00:00Z", -5 },
        { "2027-03-14T07:00:00Z", -4 }, // 2027: 2nd Sunday in March is the 14th
        { "2027-11-07T05:59:59Z", -4 }, // 2027: 1st Sunday in November is the 7th
        { "2027-11-07T06:00:00Z", -5 },
    };

    [Theory]
    [MemberData(nameof(Instants))]
    public void ToEastern_AppliesEstAndEdtOffsets(string utc, int expectedOffsetHours)
    {
        var instant = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

        var eastern = NewYorkTime.ToEastern(instant);

        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), eastern.Offset);
        Assert.Equal(instant, eastern); // same instant, different wall clock
    }

    [Theory]
    [MemberData(nameof(Instants))]
    public void UsRulesFallback_MatchesIanaZone(string utc, int expectedOffsetHours)
    {
        var instant = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

        var fallback = NewYorkTime.Convert(instant, zone: null);

        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), fallback.Offset);
        Assert.Equal(NewYorkTime.ToEastern(instant).DateTime, fallback.DateTime);
    }

    [Fact]
    public void EasternDate_RollsOverAtNewYorkMidnight()
    {
        Assert.Equal(new DateOnly(2026, 11, 30), NewYorkTime.EasternDate(new DateTimeOffset(2026, 12, 1, 4, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 12, 1), NewYorkTime.EasternDate(new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TryResolve_ReturnsZone_WhenFinderSucceeds()
    {
        var zone = NewYorkTime.TryResolve(TimeZoneInfo.FindSystemTimeZoneById);

        Assert.NotNull(zone);
    }

    [Fact]
    public void TryResolve_ReturnsNull_WhenZoneIsMissing() =>
        Assert.Null(NewYorkTime.TryResolve(_ => throw new TimeZoneNotFoundException()));

    [Fact]
    public void TryResolve_ReturnsNull_WhenZoneIsCorrupt() =>
        Assert.Null(NewYorkTime.TryResolve(_ => throw new InvalidTimeZoneException()));

    [Fact]
    public void TimeZoneId_IsIana() => Assert.Equal("America/New_York", NewYorkTime.TimeZoneId);
}
