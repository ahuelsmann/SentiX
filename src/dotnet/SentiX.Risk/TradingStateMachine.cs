namespace SentiX.Risk;

/// <summary>Inputs for a deterministic <see cref="TradingStateMachine"/> reset evaluation.</summary>
/// <param name="HaltedAtUtc">Instant the current halt state was entered.</param>
/// <param name="Clock">Injected market clock (current instant + US market holiday flag).</param>
/// <param name="IsManualReset">Explicit operator reset (dashboard kill-switch release).</param>
/// <param name="HealthyDuration">Continuous duration the native fast-path has reported <c>READY</c>.</param>
public sealed record TradingStateTransitionContext(
    DateTimeOffset HaltedAtUtc,
    MarketClock Clock,
    bool IsManualReset = false,
    TimeSpan HealthyDuration = default);

/// <summary>Deterministic kill-switch state machine (specs/004 §4).</summary>
public static class TradingStateMachine
{
    /// <summary>New York local time from which a <see cref="TradingState.HaltedDailyLoss"/> may auto-reset.</summary>
    public static readonly TimeSpan DailyLossResetTimeEt = new(9, 30, 0);

    /// <summary>Continuous healthy duration required to leave <see cref="TradingState.HaltedSystemError"/>.</summary>
    public static readonly TimeSpan SystemErrorHealthyThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Evaluates whether <paramref name="current"/> resets to <see cref="TradingState.Active"/>.</summary>
    public static TradingState Next(TradingState current, TradingStateTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (current == TradingState.Active)
        {
            return TradingState.Active;
        }

        if (context.IsManualReset)
        {
            return TradingState.Active;
        }

        return current switch
        {
            TradingState.HaltedDailyLoss => IsLaterTradingDayAfterOpen(context) ? TradingState.Active : current,
            TradingState.HaltedSystemError => context.HealthyDuration >= SystemErrorHealthyThreshold ? TradingState.Active : current,
            _ => current, // HaltedManual (and any unknown value) only resets manually — fail-closed.
        };
    }

    /// <summary>Transitions <see cref="TradingState.Active"/> to <see cref="TradingState.HaltedDailyLoss"/> when <c>dailyPnLPct &lt;= policy.MaxDailyLossPct</c>.</summary>
    public static TradingState ApplyDailyLossGuard(TradingState current, double dailyPnLPct, RiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return current == TradingState.Active && !(dailyPnLPct > policy.MaxDailyLossPct)
            ? TradingState.HaltedDailyLoss
            : current;
    }

    /// <summary>Applies an operator/system halt. Halts never downgrade: a manual halt always wins, an existing halt is kept.</summary>
    public static TradingState Halt(TradingState current, TradingState haltState)
    {
        if (haltState == TradingState.Active)
        {
            throw new ArgumentOutOfRangeException(nameof(haltState), haltState, "Halt requires a halted target state.");
        }

        return current == TradingState.Active || haltState == TradingState.HaltedManual ? haltState : current;
    }

    private static bool IsLaterTradingDayAfterOpen(TradingStateTransitionContext context)
    {
        var nowEt = NewYorkTime.ToEastern(context.Clock.UtcNow);
        var haltedDateEt = NewYorkTime.EasternDate(context.HaltedAtUtc);
        return DateOnly.FromDateTime(nowEt.DateTime) > haltedDateEt
            && !context.Clock.IsUsMarketHoliday
            && HardRiskManager.IsWeekday(nowEt.DayOfWeek)
            && nowEt.TimeOfDay >= DailyLossResetTimeEt;
    }
}
