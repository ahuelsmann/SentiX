using System.Globalization;

namespace SentiX.Risk;

/// <summary>
/// Deterministic Tier-2 Hard-Risk-Manager (specs/004). Pure function: no I/O, no ambient clock, no AI.
/// All 13 rules are evaluated on every call (no short-circuit) so the audit trail is complete.
/// </summary>
public static class HardRiskManager
{
    /// <summary>Earliest permitted entry time in New York local time.</summary>
    public static readonly TimeSpan TradingWindowStartEt = new(9, 35, 0);

    /// <summary>Latest permitted entry time in New York local time (inclusive).</summary>
    public static readonly TimeSpan TradingWindowEndEt = new(15, 50, 0);

    /// <summary>Evaluates a <see cref="TradeProposal"/> against all 13 mandatory hard-risk rules.</summary>
    public static RiskDecision Evaluate(TradeProposal proposal, PortfolioSnapshot snapshot, MarketClock clock, RiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(policy);

        var violations = new List<RiskViolation>(capacity: 4);
        var ticker = proposal.Ticker ?? string.Empty;
        var isBuy = proposal.Action == TradeAction.Buy;
        var isSell = proposal.Action == TradeAction.Sell;

        var existingLong = FindLongPosition(snapshot.OpenPositions, ticker);
        var isClosingSell = isSell && existingLong is not null;

        // A new entry is a BUY, or a short-opening SELL (only conceivable when shorting is enabled).
        var isNewEntry = isBuy || (isSell && !isClosingSell && policy.AllowShorting);

        var dailyLossBreached = !(snapshot.DailyPnLPct > policy.MaxDailyLossPct);
        var effectiveState = TradingStateMachine.ApplyDailyLossGuard(snapshot.TradingState, snapshot.DailyPnLPct, policy);

        // Rule 1 — KILL_SWITCH_ACTIVE (closing exits stay permitted).
        if (isNewEntry && snapshot.TradingState != TradingState.Active)
        {
            Add(violations, RiskViolationCode.KillSwitchActive, $"Trading state is {snapshot.TradingState}; new entries are blocked.");
        }

        // Rule 2 — NON_ACTIONABLE_PROPOSAL.
        if (!isBuy && !isSell)
        {
            Add(violations, RiskViolationCode.NonActionableProposal, $"Action {proposal.Action} is not actionable (BUY or SELL required).");
        }

        // Rule 3 — SHORTING_NOT_ALLOWED (Long-Only: SELL may only close an existing long).
        if (isSell && !policy.AllowShorting && existingLong is null)
        {
            Add(violations, RiskViolationCode.ShortingNotAllowed, $"SELL {ticker} without an open long position would open a short.");
        }

        // Rule 4 — CONFIDENCE_TOO_LOW (NaN fails closed).
        if (!(proposal.Confidence >= policy.MinConfidence))
        {
            Add(violations, RiskViolationCode.ConfidenceTooLow, string.Create(CultureInfo.InvariantCulture, $"Confidence {proposal.Confidence} is below {policy.MinConfidence}."));
        }

        // Rule 5 — TICKER_NOT_ALLOWED.
        if (!IsTickerAllowed(ticker, policy.AllowedTickers))
        {
            Add(violations, RiskViolationCode.TickerNotAllowed, $"Ticker '{ticker}' is not in the universe allowlist.");
        }

        // Rule 6 — OUTSIDE_TRADING_HOURS (America/New_York, DST-aware).
        if (!IsWithinTradingWindow(clock))
        {
            Add(violations, RiskViolationCode.OutsideTradingHours, "Outside US regular trading window 09:35-15:50 ET (weekday, non-holiday).");
        }

        // Rule 7 — TICKER_COOLDOWN_ACTIVE.
        if (IsCooldownActive(ticker, snapshot.LastTradeTimesUtc, clock.UtcNow, policy.CooldownMinutes))
        {
            Add(violations, RiskViolationCode.TickerCooldownActive, $"Ticker '{ticker}' is within its {policy.CooldownMinutes} min cooldown.");
        }

        // Sizing (whole shares only).
        var targetNotional = Math.Min(snapshot.Equity * policy.MaxPositionEquityPct, snapshot.BuyingPower * policy.MaxBuyingPowerUsagePct);
        int quantity;
        if (isClosingSell)
        {
            quantity = existingLong!.Quantity;
        }
        else if (isNewEntry)
        {
            quantity = CalculateQuantity(targetNotional, snapshot.CurrentAskPrice);
        }
        else
        {
            quantity = 0;
        }

        var actualNotional = quantity * snapshot.CurrentAskPrice;

        if (isBuy)
        {
            // Rule 8 — DUPLICATE_POSITION.
            if (ContainsTicker(snapshot.OpenPositions, ticker) || ContainsTicker(snapshot.PendingOrders, ticker))
            {
                Add(violations, RiskViolationCode.DuplicatePosition, $"An open position or pending order for '{ticker}' already exists.");
            }

            // Rule 9 — MAX_OPEN_POSITIONS.
            if (snapshot.OpenPositions.Count >= policy.MaxOpenPositions)
            {
                Add(violations, RiskViolationCode.MaxOpenPositions, $"{snapshot.OpenPositions.Count} open positions reached the limit of {policy.MaxOpenPositions}.");
            }

            // Rule 10 — DAILY_LOSS_LIMIT_REACHED.
            if (dailyLossBreached)
            {
                Add(violations, RiskViolationCode.DailyLossLimitReached, string.Create(CultureInfo.InvariantCulture, $"Daily PnL {snapshot.DailyPnLPct} is at or below the limit {policy.MaxDailyLossPct}."));
            }

            // Rule 11 — SECTOR_EXPOSURE_EXCEEDED.
            var newNotional = quantity > 0 ? actualNotional : targetNotional;
            if (!IsSectorExposureWithinLimit(snapshot, newNotional, policy.MaxSectorExposurePct))
            {
                Add(violations, RiskViolationCode.SectorExposureExceeded, string.Create(CultureInfo.InvariantCulture, $"Sector '{snapshot.TargetSector}' exposure after trade would exceed {policy.MaxSectorExposurePct} of equity."));
            }
        }

        // Rule 12 — INSUFFICIENT_BUYING_POWER (applies to every new entry, never to closing exits).
        if (isNewEntry && !HasSufficientBuyingPower(snapshot, quantity, actualNotional, policy))
        {
            Add(violations, RiskViolationCode.InsufficientBuyingPower, "Order notional is below the minimum or exceeds usable buying power.");
        }

        // Rule 13 — INVALID_BRACKET_BOUNDS (clamp, then reward-to-risk check).
        decimal? clampedStopLoss = null;
        decimal? clampedTakeProfit = null;
        if (proposal.SuggestedStopLossPct <= 0m || proposal.SuggestedTakeProfitPct <= 0m)
        {
            Add(violations, RiskViolationCode.InvalidBracketBounds, "Suggested stop-loss and take-profit must be positive.");
        }
        else
        {
            var stopLoss = Math.Clamp(proposal.SuggestedStopLossPct, policy.MinStopLossPct, policy.MaxStopLossPct);
            var takeProfit = Math.Clamp(proposal.SuggestedTakeProfitPct, policy.MinTakeProfitPct, policy.MaxTakeProfitPct);
            clampedStopLoss = stopLoss;
            clampedTakeProfit = takeProfit;
            if (takeProfit < stopLoss * policy.MinRewardToRiskRatio)
            {
                Add(violations, RiskViolationCode.InvalidBracketBounds, string.Create(CultureInfo.InvariantCulture, $"Take-profit {takeProfit} is below stop-loss {stopLoss} x {policy.MinRewardToRiskRatio}."));
            }
        }

        var approved = violations.Count == 0;
        var clientOrderId = approved
            ? ClientOrderIdGenerator.Create(proposal.EventId, proposal.ProposalId, ticker, proposal.Action, clock.UtcNow)
            : null;

        return new RiskDecision(approved, effectiveState, violations, quantity, clampedStopLoss, clampedTakeProfit, clientOrderId);
    }

    /// <summary><c>Quantity = floor(TargetNotional / askPrice)</c>, saturated to <c>[0, int.MaxValue]</c>; <c>0</c> when the ask is not positive.</summary>
    public static int CalculateQuantity(decimal targetNotional, decimal askPrice)
    {
        if (askPrice <= 0m)
        {
            return 0;
        }

        var raw = Math.Floor(targetNotional / askPrice);
        if (raw <= 0m)
        {
            return 0;
        }

        return raw >= int.MaxValue ? int.MaxValue : (int)raw;
    }

    /// <summary>US regular trading window check for new entries (<c>09:35:00 &lt;= t &lt;= 15:50:00 ET</c>, weekday, not a holiday).</summary>
    public static bool IsWithinTradingWindow(MarketClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var et = NewYorkTime.ToEastern(clock.UtcNow);
        var timeOfDay = et.TimeOfDay;
        return !clock.IsUsMarketHoliday
            && IsWeekday(et.DayOfWeek)
            && timeOfDay >= TradingWindowStartEt
            && timeOfDay <= TradingWindowEndEt;
    }

    internal static bool IsWeekday(DayOfWeek day) => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private static void Add(List<RiskViolation> violations, RiskViolationCode code, string message) =>
        violations.Add(new RiskViolation(code, message));

    private static OpenPosition? FindLongPosition(IReadOnlyList<OpenPosition> positions, string ticker)
    {
        foreach (var position in positions)
        {
            if (position.Quantity > 0 && SymbolEquals(position.Ticker, ticker))
            {
                return position;
            }
        }

        return null;
    }

    private static bool ContainsTicker(IReadOnlyList<OpenPosition> positions, string ticker)
    {
        foreach (var position in positions)
        {
            if (SymbolEquals(position.Ticker, ticker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTicker(IReadOnlyList<PendingOrder> orders, string ticker)
    {
        foreach (var order in orders)
        {
            if (SymbolEquals(order.Ticker, ticker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SymbolEquals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static bool IsTickerAllowed(string ticker, IReadOnlySet<string> allowedTickers)
    {
        if (string.IsNullOrWhiteSpace(ticker) || Normalize(ticker).Length > ClientOrderIdGenerator.MaxTickerLength)
        {
            return false;
        }

        foreach (var allowed in allowedTickers)
        {
            if (SymbolEquals(allowed, ticker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCooldownActive(string ticker, IReadOnlyDictionary<string, DateTimeOffset> lastTradeTimesUtc, DateTimeOffset utcNow, int cooldownMinutes)
    {
        var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
        foreach (var (lastTicker, lastTradeUtc) in lastTradeTimesUtc)
        {
            // A last-trade time in the future (clock skew) yields a negative elapsed span and fails closed.
            if (SymbolEquals(lastTicker, ticker) && utcNow - lastTradeUtc < cooldown)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSectorExposureWithinLimit(PortfolioSnapshot snapshot, decimal newNotional, decimal maxSectorExposurePct)
    {
        if (snapshot.Equity <= 0m)
        {
            return false;
        }

        var sectorValue = 0m;
        foreach (var position in snapshot.OpenPositions)
        {
            if (SymbolEquals(position.Sector, snapshot.TargetSector))
            {
                sectorValue += position.MarketValue;
            }
        }

        return (sectorValue + newNotional) / snapshot.Equity <= maxSectorExposurePct;
    }

    internal static bool HasSufficientBuyingPower(PortfolioSnapshot snapshot, int quantity, decimal actualNotional, RiskPolicy policy) =>
        snapshot.CurrentAskPrice > 0m
        && quantity >= 1
        && actualNotional >= policy.MinOrderNotional
        && actualNotional <= snapshot.BuyingPower * policy.MaxBuyingPowerUsagePct;
}
