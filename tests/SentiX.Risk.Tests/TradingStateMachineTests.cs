namespace SentiX.Risk.Tests;

public sealed class TradingStateMachineTests
{
    // Halted on Monday 2026-11-30 at 11:00 EST.
    private static readonly DateTimeOffset HaltedAt = new(2026, 11, 30, 16, 0, 0, TimeSpan.Zero);

    private static TradingStateTransitionContext Ctx(DateTimeOffset nowUtc, bool holiday = false, bool manual = false, double healthySeconds = 0) =>
        new(HaltedAt, new MarketClock(nowUtc, holiday), manual, TimeSpan.FromSeconds(healthySeconds));

    [Fact]
    public void Active_StaysActive() =>
        Assert.Equal(TradingState.Active, TradingStateMachine.Next(TradingState.Active, Ctx(HaltedAt)));

    [Theory]
    [InlineData(TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedSystemError)]
    public void ManualReset_ResetsEveryHaltState(TradingState state) =>
        Assert.Equal(TradingState.Active, TradingStateMachine.Next(state, Ctx(HaltedAt.AddMinutes(1), manual: true)));

    [Fact]
    public void HaltedManual_NeverAutoResets()
    {
        var later = Ctx(HaltedAt.AddDays(7), healthySeconds: 3600);

        Assert.Equal(TradingState.HaltedManual, TradingStateMachine.Next(TradingState.HaltedManual, later));
    }

    [Theory]
    [InlineData("2026-11-30T23:00:00Z", false, TradingState.HaltedDailyLoss)] // same ET day
    [InlineData("2026-12-01T14:29:59Z", false, TradingState.HaltedDailyLoss)] // next day 09:29:59 EST
    [InlineData("2026-12-01T14:30:00Z", false, TradingState.Active)]          // next day 09:30:00 EST
    [InlineData("2026-12-01T14:30:00Z", true, TradingState.HaltedDailyLoss)]  // next day is a holiday
    [InlineData("2026-12-05T15:00:00Z", false, TradingState.HaltedDailyLoss)] // Saturday
    [InlineData("2026-12-06T15:00:00Z", false, TradingState.HaltedDailyLoss)] // Sunday
    [InlineData("2026-12-01T05:30:00Z", false, TradingState.HaltedDailyLoss)] // next ET day 00:30, before open
    public void HaltedDailyLoss_ResetsOnlyOnLaterTradingDayAtOrAfter0930Et(string nowUtc, bool holiday, TradingState expected)
    {
        var ctx = Ctx(DateTimeOffset.Parse(nowUtc, System.Globalization.CultureInfo.InvariantCulture), holiday);

        Assert.Equal(expected, TradingStateMachine.Next(TradingState.HaltedDailyLoss, ctx));
    }

    [Fact]
    public void HaltedDailyLoss_SummerTime_ResetsAt0930Edt()
    {
        var haltedSummer = new DateTimeOffset(2026, 7, 15, 15, 0, 0, TimeSpan.Zero);
        TradingStateTransitionContext At(DateTimeOffset now) => new(haltedSummer, new MarketClock(now));

        Assert.Equal(TradingState.HaltedDailyLoss, TradingStateMachine.Next(TradingState.HaltedDailyLoss, At(new DateTimeOffset(2026, 7, 16, 13, 29, 59, TimeSpan.Zero))));
        Assert.Equal(TradingState.Active, TradingStateMachine.Next(TradingState.HaltedDailyLoss, At(new DateTimeOffset(2026, 7, 16, 13, 30, 0, TimeSpan.Zero))));
    }

    [Theory]
    [InlineData(0, TradingState.HaltedSystemError)]
    [InlineData(29.999, TradingState.HaltedSystemError)]
    [InlineData(30, TradingState.Active)]
    [InlineData(120, TradingState.Active)]
    public void HaltedSystemError_ResetsAfter30SecondsHealthy(double healthySeconds, TradingState expected) =>
        Assert.Equal(expected, TradingStateMachine.Next(TradingState.HaltedSystemError, Ctx(HaltedAt.AddMinutes(5), healthySeconds: healthySeconds)));

    [Fact]
    public void UnknownState_FailsClosed()
    {
        var unknown = (TradingState)42;

        Assert.Equal(unknown, TradingStateMachine.Next(unknown, Ctx(HaltedAt.AddDays(3), healthySeconds: 999)));
    }

    [Fact]
    public void Next_NullContext_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TradingStateMachine.Next(TradingState.Active, null!));

    [Theory]
    [InlineData(TradingState.Active, -0.0199, TradingState.Active)]
    [InlineData(TradingState.Active, -0.02, TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.Active, double.NaN, TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedManual, -0.05, TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedSystemError, -0.05, TradingState.HaltedSystemError)]
    public void ApplyDailyLossGuard_TransitionsOnlyFromActive(TradingState current, double pnl, TradingState expected) =>
        Assert.Equal(expected, TradingStateMachine.ApplyDailyLossGuard(current, pnl, new RiskPolicy()));

    [Fact]
    public void ApplyDailyLossGuard_NullPolicy_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TradingStateMachine.ApplyDailyLossGuard(TradingState.Active, 0, null!));

    [Theory]
    [InlineData(TradingState.Active, TradingState.HaltedManual, TradingState.HaltedManual)]
    [InlineData(TradingState.Active, TradingState.HaltedSystemError, TradingState.HaltedSystemError)]
    [InlineData(TradingState.Active, TradingState.HaltedDailyLoss, TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedDailyLoss, TradingState.HaltedManual, TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedManual, TradingState.HaltedSystemError, TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedSystemError, TradingState.HaltedDailyLoss, TradingState.HaltedSystemError)]
    public void Halt_NeverDowngrades_ManualAlwaysWins(TradingState current, TradingState halt, TradingState expected) =>
        Assert.Equal(expected, TradingStateMachine.Halt(current, halt));

    [Fact]
    public void Halt_ToActive_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingStateMachine.Halt(TradingState.HaltedManual, TradingState.Active));
}
