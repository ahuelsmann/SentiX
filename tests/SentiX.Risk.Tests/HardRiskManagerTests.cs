using static SentiX.Risk.Tests.RiskTestData;

namespace SentiX.Risk.Tests;

public sealed class HardRiskManagerTests
{
    // ---------------------------------------------------------------- baseline & audit semantics

    [Fact]
    public void Baseline_Buy_IsApproved_WithSizingClampsAndClientOrderId()
    {
        var decision = Evaluate();

        Assert.True(decision.Approved);
        Assert.Empty(decision.Violations);
        Assert.Equal(TradingState.Active, decision.EffectiveTradingState);
        Assert.Equal(25, decision.CalculatedQuantity); // min(100000*0.05, 50000*0.95)=5000 / 200
        Assert.Equal(0.03m, decision.ClampedStopLossPct);
        Assert.Equal(0.06m, decision.ClampedTakeProfitPct);
        Assert.Equal("SX-20261130-AAPL-BUY-c915e268", decision.ClientOrderId);
    }

    [Fact]
    public void Evaluate_DoesNotShortCircuit_CollectsEveryApplicableBuyViolationInRuleOrder()
    {
        var decision = Evaluate(
            Proposal(ticker: "ZZZZ", confidence: 0.1, stopLoss: 0m),
            Snapshot(
                state: TradingState.HaltedManual,
                equity: 0m,
                dailyPnLPct: -0.05,
                ask: 0m,
                positions: [Position("ZZZZ"), Position("A1"), Position("A2"), Position("A3"), Position("A4")],
                lastTrades: new Dictionary<string, DateTimeOffset> { ["ZZZZ"] = WinterMondayTenEt.AddDays(-2).AddMinutes(-1) }),
            Clock(new DateTimeOffset(2026, 11, 28, 15, 0, 0, TimeSpan.Zero))); // Saturday

        Assert.False(decision.Approved);
        Assert.Null(decision.ClientOrderId);
        Assert.Null(decision.ClampedStopLossPct);
        Assert.Null(decision.ClampedTakeProfitPct);
        Assert.Equal(0, decision.CalculatedQuantity);
        Assert.Equal(TradingState.HaltedManual, decision.EffectiveTradingState);
        AssertCodes(decision, [
                RiskViolationCode.KillSwitchActive,
                RiskViolationCode.ConfidenceTooLow,
                RiskViolationCode.TickerNotAllowed,
                RiskViolationCode.OutsideTradingHours,
                RiskViolationCode.TickerCooldownActive,
                RiskViolationCode.DuplicatePosition,
                RiskViolationCode.MaxOpenPositions,
                RiskViolationCode.DailyLossLimitReached,
                RiskViolationCode.SectorExposureExceeded,
                RiskViolationCode.InsufficientBuyingPower,
                RiskViolationCode.InvalidBracketBounds,
            ]);
        Assert.All(decision.Violations, v => Assert.False(string.IsNullOrWhiteSpace(v.Message)));
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => HardRiskManager.Evaluate(null!, Snapshot(), Clock(), Policy()));
        Assert.Throws<ArgumentNullException>(() => HardRiskManager.Evaluate(Proposal(), null!, Clock(), Policy()));
        Assert.Throws<ArgumentNullException>(() => HardRiskManager.Evaluate(Proposal(), Snapshot(), null!, Policy()));
        Assert.Throws<ArgumentNullException>(() => HardRiskManager.Evaluate(Proposal(), Snapshot(), Clock(), null!));
    }

    // ---------------------------------------------------------------- rule 1: KILL_SWITCH_ACTIVE

    [Theory]
    [InlineData(TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedSystemError)]
    public void Rule01_HaltedState_BlocksNewBuyEntries(TradingState state)
    {
        var decision = Evaluate(snapshot: Snapshot(state: state));

        Assert.False(decision.Approved);
        AssertCodes(decision, [RiskViolationCode.KillSwitchActive]);
        Assert.Equal(state, decision.EffectiveTradingState);
    }

    [Theory]
    [InlineData(TradingState.Active)]
    [InlineData(TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedSystemError)]
    public void Rule01_ClosingLongExit_IsPermittedInEveryState(TradingState state)
    {
        var decision = Evaluate(
            Proposal(TradeAction.Sell),
            Snapshot(state: state, positions: [Position("AAPL", quantity: 7)]));

        Assert.True(decision.Approved);
        Assert.Equal(7, decision.CalculatedQuantity);
        Assert.Equal("SX-20261130-AAPL-SELL-c915e268", decision.ClientOrderId);
    }

    [Fact]
    public void Rule01_ShortOpeningSell_IsBlockedWhenHaltedEvenIfShortingAllowed()
    {
        var decision = Evaluate(
            Proposal(TradeAction.Sell),
            Snapshot(state: TradingState.HaltedManual),
            policy: Policy(allowShorting: true));

        AssertCodes(decision, [RiskViolationCode.KillSwitchActive]);
        Assert.Equal(25, decision.CalculatedQuantity);
    }

    [Fact]
    public void Rule01_HoldWhileHalted_IsNotAnEntry()
    {
        var decision = Evaluate(Proposal(TradeAction.Hold), Snapshot(state: TradingState.HaltedManual));

        AssertCodes(decision, [RiskViolationCode.NonActionableProposal]);
    }

    // ---------------------------------------------------------------- rule 2: NON_ACTIONABLE_PROPOSAL

    [Fact]
    public void Rule02_Hold_IsNonActionable_AndNeverSized()
    {
        var decision = Evaluate(Proposal(TradeAction.Hold));

        Assert.False(decision.Approved);
        AssertCodes(decision, [RiskViolationCode.NonActionableProposal]);
        Assert.Equal(0, decision.CalculatedQuantity);
        Assert.Null(decision.ClientOrderId);
    }

    // ---------------------------------------------------------------- rule 3: SHORTING_NOT_ALLOWED (Long-Only)

    [Fact]
    public void Rule03_SellWithoutPosition_IsBlockedAsShort()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell));

        AssertCodes(decision, [RiskViolationCode.ShortingNotAllowed]);
        Assert.Equal(0, decision.CalculatedQuantity);
    }

    [Fact]
    public void Rule03_SellWithZeroQuantityPosition_IsBlockedAsShort()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), Snapshot(positions: [Position("AAPL", quantity: 0)]));

        AssertCodes(decision, [RiskViolationCode.ShortingNotAllowed]);
    }

    [Fact]
    public void Rule03_SellWithLongInDifferentTicker_IsBlockedAsShort()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), Snapshot(positions: [Position("MSFT")]));

        AssertCodes(decision, [RiskViolationCode.ShortingNotAllowed]);
    }

    [Fact]
    public void Rule03_SellClosingLong_MatchesTickerCaseInsensitively()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell, ticker: "aapl"), Snapshot(positions: [Position(" AAPL ", quantity: 3)]));

        Assert.True(decision.Approved);
        Assert.Equal(3, decision.CalculatedQuantity);
        Assert.Equal("SX-20261130-AAPL-SELL-c915e268", decision.ClientOrderId);
    }

    [Fact]
    public void Rule03_ShortSell_WhenShortingAllowed_IsSizedAsEntry()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), policy: Policy(allowShorting: true));

        Assert.True(decision.Approved);
        Assert.Equal(25, decision.CalculatedQuantity);
    }

    [Fact]
    public void Rule03_ShortSell_WhenShortingAllowed_StillNeedsBuyingPower()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), Snapshot(ask: 0m), policy: Policy(allowShorting: true));

        AssertCodes(decision, [RiskViolationCode.InsufficientBuyingPower]);
    }

    // ---------------------------------------------------------------- rule 4: CONFIDENCE_TOO_LOW

    [Theory]
    [InlineData(0.70, true)]
    [InlineData(0.6999, false)]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    [InlineData(double.NaN, false)]
    public void Rule04_ConfidenceBoundary(double confidence, bool approved)
    {
        var decision = Evaluate(Proposal(confidence: confidence));

        Assert.Equal(approved, decision.Approved);
        AssertCodes(decision, approved ? [] : [RiskViolationCode.ConfidenceTooLow]);
    }

    // ---------------------------------------------------------------- rule 5: TICKER_NOT_ALLOWED

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GME")]
    public void Rule05_TickerNotInAllowlist_IsRejected(string ticker)
    {
        var decision = Evaluate(Proposal(ticker: ticker));

        Assert.Contains(RiskViolationCode.TickerNotAllowed, decision.Codes());
        Assert.False(decision.Approved);
    }

    [Fact]
    public void Rule05_NullTicker_IsRejected()
    {
        var decision = Evaluate(Proposal(ticker: null!));

        AssertCodes(decision, [RiskViolationCode.TickerNotAllowed]);
    }

    [Fact]
    public void Rule05_Allowlist_IsCaseInsensitive_EvenWithOrdinalSet()
    {
        var policy = Policy() with { AllowedTickers = new HashSet<string>(StringComparer.Ordinal) { "AAPL" } };

        var decision = Evaluate(Proposal(ticker: "aapl"), policy: policy);

        Assert.True(decision.Approved);
        Assert.Equal("SX-20261130-AAPL-BUY-c915e268", decision.ClientOrderId);
    }

    [Fact]
    public void Rule05_TickerLongerThanClientOrderIdBudget_IsRejected()
    {
        var longTicker = new string('A', ClientOrderIdGenerator.MaxTickerLength + 1);
        var policy = Policy() with { AllowedTickers = new HashSet<string> { longTicker } };

        var decision = Evaluate(Proposal(ticker: longTicker), policy: policy);

        AssertCodes(decision, [RiskViolationCode.TickerNotAllowed]);
    }

    [Fact]
    public void Rule05_TickerAtClientOrderIdBudget_IsApproved_AndIdFitsIn48Chars()
    {
        var maxTicker = new string('B', ClientOrderIdGenerator.MaxTickerLength);
        var policy = Policy() with { AllowedTickers = new HashSet<string> { maxTicker } };

        var decision = Evaluate(Proposal(ticker: maxTicker), policy: policy);

        Assert.True(decision.Approved);
        Assert.Equal(47, decision.ClientOrderId!.Length); // BUY is one char shorter than SELL (48)
    }

    // ---------------------------------------------------------------- rule 6: OUTSIDE_TRADING_HOURS (DST)

    [Theory]
    // Winter (EST, UTC-5): 09:35 ET == 14:35 UTC, 15:50 ET == 20:50 UTC.
    [InlineData("2026-11-30T14:34:59Z", false)]
    [InlineData("2026-11-30T14:35:00Z", true)]
    [InlineData("2026-11-30T20:50:00Z", true)]
    [InlineData("2026-11-30T20:50:01Z", false)]
    // Summer (EDT, UTC-4): 09:35 ET == 13:35 UTC, 15:50 ET == 19:50 UTC.
    [InlineData("2026-07-15T13:34:59Z", false)]
    [InlineData("2026-07-15T13:35:00Z", true)]
    [InlineData("2026-07-15T19:50:00Z", true)]
    [InlineData("2026-07-15T19:50:01Z", false)]
    // Same UTC instant 13:40Z: summer 09:40 EDT (open) vs winter 08:40 EST (closed).
    [InlineData("2026-07-15T13:40:00Z", true)]
    [InlineData("2026-11-30T13:40:00Z", false)]
    // Same UTC instant 20:30Z: winter 15:30 EST (open) vs summer 16:30 EDT (closed).
    [InlineData("2026-11-30T20:30:00Z", true)]
    [InlineData("2026-07-15T20:30:00Z", false)]
    // DST switch days 2026 (Sun 03-08 / Sun 11-01) -> following Mondays.
    [InlineData("2026-03-09T13:40:00Z", true)]
    [InlineData("2026-11-02T13:40:00Z", false)]
    // Weekend.
    [InlineData("2026-11-28T15:00:00Z", false)]
    [InlineData("2026-11-29T15:00:00Z", false)]
    public void Rule06_TradingWindow_IsDstAware(string utc, bool open)
    {
        var clock = Clock(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

        var decision = Evaluate(clock: clock);

        Assert.Equal(open, HardRiskManager.IsWithinTradingWindow(clock));
        AssertCodes(decision, open ? [] : [RiskViolationCode.OutsideTradingHours]);
    }

    [Fact]
    public void Rule06_UsMarketHoliday_IsClosed()
    {
        var decision = Evaluate(clock: Clock(holiday: true));

        AssertCodes(decision, [RiskViolationCode.OutsideTradingHours]);
    }

    [Fact]
    public void Rule06_AlsoAppliesToClosingSell()
    {
        var decision = Evaluate(
            Proposal(TradeAction.Sell),
            Snapshot(positions: [Position()]),
            Clock(holiday: true));

        AssertCodes(decision, [RiskViolationCode.OutsideTradingHours]);
    }

    [Fact]
    public void Rule06_IsWithinTradingWindow_NullClock_Throws() =>
        Assert.Throws<ArgumentNullException>(() => HardRiskManager.IsWithinTradingWindow(null!));

    // ---------------------------------------------------------------- rule 7: TICKER_COOLDOWN_ACTIVE

    [Theory]
    [InlineData(-59.999, true)]
    [InlineData(-60.0, false)]
    [InlineData(-61.0, false)]
    [InlineData(+5.0, true)] // last trade in the future (clock skew) fails closed
    public void Rule07_CooldownBoundary(double minutesOffset, bool blocked)
    {
        var lastTrades = new Dictionary<string, DateTimeOffset> { ["aapl"] = WinterMondayTenEt.AddMinutes(minutesOffset) };

        var decision = Evaluate(snapshot: Snapshot(lastTrades: lastTrades));

        AssertCodes(decision, blocked ? [RiskViolationCode.TickerCooldownActive] : []);
    }

    [Fact]
    public void Rule07_CooldownOfOtherTicker_DoesNotBlock()
    {
        var lastTrades = new Dictionary<string, DateTimeOffset> { ["MSFT"] = WinterMondayTenEt.AddMinutes(-1) };

        Assert.True(Evaluate(snapshot: Snapshot(lastTrades: lastTrades)).Approved);
    }

    [Fact]
    public void Rule07_CustomCooldownMinutes_AreRespected()
    {
        var lastTrades = new Dictionary<string, DateTimeOffset> { ["AAPL"] = WinterMondayTenEt.AddMinutes(-20) };

        var decision = Evaluate(snapshot: Snapshot(lastTrades: lastTrades), policy: Policy() with { CooldownMinutes = 15 });

        Assert.True(decision.Approved);
    }

    // ---------------------------------------------------------------- rule 8: DUPLICATE_POSITION

    [Fact]
    public void Rule08_OpenPositionInSameTicker_BlocksBuy()
    {
        var decision = Evaluate(snapshot: Snapshot(positions: [Position("Aapl", quantity: 0)]));

        AssertCodes(decision, [RiskViolationCode.DuplicatePosition]);
    }

    [Fact]
    public void Rule08_PendingOrderInSameTicker_BlocksBuy()
    {
        var decision = Evaluate(snapshot: Snapshot(pending: [new PendingOrder("SX-x", "AAPL", TradeAction.Buy, 5)]));

        AssertCodes(decision, [RiskViolationCode.DuplicatePosition]);
    }

    [Fact]
    public void Rule08_OtherTickers_DoNotBlockBuy()
    {
        var decision = Evaluate(snapshot: Snapshot(
            positions: [Position("MSFT", marketValue: 1_000m)],
            pending: [new PendingOrder("SX-y", "NVDA", TradeAction.Buy, 1)]));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void Rule08_NullTickerOnPosition_IsHandled()
    {
        var decision = Evaluate(snapshot: Snapshot(positions: [new OpenPosition(null!, null!, 1, 10m, 10m)]));

        Assert.True(decision.Approved);
    }

    // ---------------------------------------------------------------- rule 9: MAX_OPEN_POSITIONS

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void Rule09_MaxOpenPositionsBoundary(int openPositions, bool approved)
    {
        var positions = Enumerable.Range(0, openPositions)
            .Select(i => Position($"OTHER{i}", sector: "ENERGY", marketValue: 1_000m))
            .ToArray();

        var decision = Evaluate(snapshot: Snapshot(positions: positions));

        AssertCodes(decision, approved ? [] : [RiskViolationCode.MaxOpenPositions]);
    }

    // ---------------------------------------------------------------- rule 10: DAILY_LOSS_LIMIT_REACHED

    [Theory]
    [InlineData(-0.0199, true)]
    [InlineData(0.05, true)]
    [InlineData(-0.02, false)]
    [InlineData(-0.021, false)]
    [InlineData(double.NaN, false)]
    public void Rule10_DailyLossBoundary(double dailyPnLPct, bool approved)
    {
        var decision = Evaluate(snapshot: Snapshot(dailyPnLPct: dailyPnLPct));

        AssertCodes(decision, approved ? [] : [RiskViolationCode.DailyLossLimitReached]);
        Assert.Equal(approved ? TradingState.Active : TradingState.HaltedDailyLoss, decision.EffectiveTradingState);
    }

    [Fact]
    public void Rule10_ClosingSell_IsNotBlockedByDailyLoss_ButStateTransitions()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), Snapshot(dailyPnLPct: -0.02, positions: [Position()]));

        Assert.True(decision.Approved);
        Assert.Equal(TradingState.HaltedDailyLoss, decision.EffectiveTradingState);
    }

    [Fact]
    public void Rule10_AlreadyHaltedManual_KeepsManualAsEffectiveState()
    {
        var decision = Evaluate(snapshot: Snapshot(state: TradingState.HaltedManual, dailyPnLPct: -0.03));

        Assert.Equal(TradingState.HaltedManual, decision.EffectiveTradingState);
        AssertCodes(decision, [RiskViolationCode.KillSwitchActive, RiskViolationCode.DailyLossLimitReached]);
    }

    // ---------------------------------------------------------------- rule 11: SECTOR_EXPOSURE_EXCEEDED

    [Fact]
    public void Rule11_ExposureExactlyAtLimit_IsApproved()
    {
        // 20000 existing TECH + 25 * 200 = 25000 / 100000 = 0.25
        var decision = Evaluate(snapshot: Snapshot(positions: [Position("MSFT", sector: "tech", marketValue: 20_000m)]));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void Rule11_ExposureAboveLimit_IsRejected()
    {
        var decision = Evaluate(snapshot: Snapshot(positions: [Position("MSFT", sector: "TECH", marketValue: 20_000.01m)]));

        AssertCodes(decision, [RiskViolationCode.SectorExposureExceeded]);
    }

    [Fact]
    public void Rule11_OtherSectorExposure_IsIgnored()
    {
        var decision = Evaluate(snapshot: Snapshot(positions: [Position("XOM", sector: "ENERGY", marketValue: 24_000m)]));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void Rule11_NonPositiveEquity_IsRejected()
    {
        var decision = Evaluate(snapshot: Snapshot(equity: 0m));

        Assert.Contains(RiskViolationCode.SectorExposureExceeded, decision.Codes());
        Assert.Contains(RiskViolationCode.InsufficientBuyingPower, decision.Codes());
    }

    [Fact]
    public void Rule11_ZeroQuantity_UsesUnroundedTargetNotional()
    {
        // ask 6000 > target 5000 -> qty 0; exposure uses 5000: (21000 + 5000) / 100000 = 0.26 > 0.25
        var decision = Evaluate(snapshot: Snapshot(ask: 6_000m, positions: [Position("MSFT", sector: "TECH", marketValue: 21_000m)]));

        Assert.Equal(0, decision.CalculatedQuantity);
        AssertCodes(decision, [RiskViolationCode.SectorExposureExceeded, RiskViolationCode.InsufficientBuyingPower]);
    }

    [Fact]
    public void Rule11_NullTargetSector_MatchesOnlyNullOrEmptySectors()
    {
        var decision = Evaluate(snapshot: Snapshot(targetSector: null!, positions: [Position("MSFT", sector: "TECH", marketValue: 24_000m)]));

        Assert.True(decision.Approved);
    }

    // ---------------------------------------------------------------- rule 12: INSUFFICIENT_BUYING_POWER & sizing

    [Fact]
    public void Rule12_Sizing_UsesBuyingPowerWhenSmallerThanEquityShare()
    {
        // min(100000*0.05=5000, 2000*0.95=1900) = 1900 / 200 = 9.5 -> 9 shares, notional 1800
        var decision = Evaluate(snapshot: Snapshot(buyingPower: 2_000m));

        Assert.True(decision.Approved);
        Assert.Equal(9, decision.CalculatedQuantity);
    }

    [Fact]
    public void Rule12_NonPositiveAsk_IsRejected()
    {
        var decision = Evaluate(snapshot: Snapshot(ask: -1m));

        Assert.Equal(0, decision.CalculatedQuantity);
        AssertCodes(decision, [RiskViolationCode.InsufficientBuyingPower]);
    }

    [Fact]
    public void Rule12_QuantityBelowOneShare_IsRejected()
    {
        var decision = Evaluate(snapshot: Snapshot(ask: 5_001m));

        Assert.Equal(0, decision.CalculatedQuantity);
        AssertCodes(decision, [RiskViolationCode.InsufficientBuyingPower]);
    }

    [Theory]
    [InlineData(2_000, 99, false)]   // target 100 -> 1 share @ 99 = 99 < 100
    [InlineData(2_000, 100, true)]   // target 100 -> 1 share @ 100 = 100 >= 100
    public void Rule12_MinimumNotionalBoundary(decimal equity, decimal ask, bool approved)
    {
        var decision = Evaluate(snapshot: Snapshot(equity: equity, ask: ask));

        Assert.Equal(1, decision.CalculatedQuantity);
        AssertCodes(decision, approved ? [] : [RiskViolationCode.InsufficientBuyingPower]);
    }

    [Fact]
    public void Rule12_ClosingSell_IsNeverBlockedByBuyingPower()
    {
        var decision = Evaluate(Proposal(TradeAction.Sell), Snapshot(buyingPower: 0m, ask: 0m, positions: [Position(quantity: 4)]));

        Assert.True(decision.Approved);
        Assert.Equal(4, decision.CalculatedQuantity);
    }

    [Theory]
    [InlineData(200, 25, 5_000, 50_000, true)]
    [InlineData(0, 1, 100, 50_000, false)]          // ask not positive
    [InlineData(200, 0, 0, 50_000, false)]          // no whole share
    [InlineData(50, 1, 50, 50_000, false)]          // below MinOrderNotional
    [InlineData(200, 25, 5_000, 5_000, false)]      // exceeds 95 % of buying power (defense in depth)
    [InlineData(200, 25, 4_750, 5_000, true)]       // exactly 95 % of buying power
    public void Rule12_HasSufficientBuyingPower_AllConditions(decimal ask, int qty, decimal notional, decimal buyingPower, bool expected)
    {
        var snapshot = Snapshot(ask: ask, buyingPower: buyingPower);

        Assert.Equal(expected, HardRiskManager.HasSufficientBuyingPower(snapshot, qty, notional, Policy()));
    }

    [Theory]
    [InlineData(5_000, 200, 25)]
    [InlineData(5_000, 0, 0)]
    [InlineData(5_000, -10, 0)]
    [InlineData(-5_000, 200, 0)]
    [InlineData(199.99, 200, 0)]
    [InlineData(1_000_000_000_000, 0.0001, int.MaxValue)]
    public void Sizing_CalculateQuantity_FloorsAndSaturates(decimal target, decimal ask, int expected) =>
        Assert.Equal(expected, HardRiskManager.CalculateQuantity(target, ask));

    // ---------------------------------------------------------------- rule 13: INVALID_BRACKET_BOUNDS

    [Theory]
    [InlineData(0, 0.06)]
    [InlineData(-0.01, 0.06)]
    [InlineData(0.03, 0)]
    [InlineData(0.03, -0.06)]
    public void Rule13_NonPositiveBracketInputs_AreRejectedWithoutClamps(decimal stopLoss, decimal takeProfit)
    {
        var decision = Evaluate(Proposal(stopLoss: stopLoss, takeProfit: takeProfit));

        AssertCodes(decision, [RiskViolationCode.InvalidBracketBounds]);
        Assert.Null(decision.ClampedStopLossPct);
        Assert.Null(decision.ClampedTakeProfitPct);
    }

    [Theory]
    [InlineData(0.001, 0.010, 0.015, 0.030, true)]   // both clamped up
    [InlineData(0.200, 0.900, 0.050, 0.150, true)]   // both clamped down
    [InlineData(0.030, 0.060, 0.030, 0.060, true)]   // defaults pass-through
    [InlineData(0.040, 0.060, 0.040, 0.060, true)]   // CRV exactly 1.5
    [InlineData(0.050, 0.060, 0.050, 0.060, false)]  // CRV 1.2 < 1.5
    [InlineData(0.041, 0.061, 0.041, 0.061, false)]  // 0.061 < 0.0615
    public void Rule13_ClampingAndRewardToRisk(decimal sl, decimal tp, decimal expectedSl, decimal expectedTp, bool approved)
    {
        var decision = Evaluate(Proposal(stopLoss: sl, takeProfit: tp));

        Assert.Equal(expectedSl, decision.ClampedStopLossPct);
        Assert.Equal(expectedTp, decision.ClampedTakeProfitPct);
        AssertCodes(decision, approved ? [] : [RiskViolationCode.InvalidBracketBounds]);
    }

    // ---------------------------------------------------------------- policy defaults (specs/004 §2)

    [Fact]
    public void RiskPolicy_Defaults_MatchSpec()
    {
        var policy = new RiskPolicy();

        Assert.False(policy.AllowShorting);
        Assert.Equal(0.70, policy.MinConfidence);
        Assert.Empty(policy.AllowedTickers);
        Assert.Equal(60, policy.CooldownMinutes);
        Assert.Equal(5, policy.MaxOpenPositions);
        Assert.Equal(-0.02, policy.MaxDailyLossPct);
        Assert.Equal(0.25m, policy.MaxSectorExposurePct);
        Assert.Equal(0.05m, policy.MaxPositionEquityPct);
        Assert.Equal(0.95m, policy.MaxBuyingPowerUsagePct);
        Assert.Equal(100m, policy.MinOrderNotional);
        Assert.Equal(0.015m, policy.MinStopLossPct);
        Assert.Equal(0.050m, policy.MaxStopLossPct);
        Assert.Equal(0.030m, policy.DefaultStopLossPct);
        Assert.Equal(0.030m, policy.MinTakeProfitPct);
        Assert.Equal(0.150m, policy.MaxTakeProfitPct);
        Assert.Equal(0.060m, policy.DefaultTakeProfitPct);
        Assert.Equal(1.5m, policy.MinRewardToRiskRatio);
    }

    [Fact]
    public void Evaluate_IsDeterministic_ForIdenticalInputs()
    {
        var first = Evaluate();
        var second = Evaluate();

        Assert.Equal(first.ClientOrderId, second.ClientOrderId);
        Assert.Equal(first.CalculatedQuantity, second.CalculatedQuantity);
        Assert.Equal(first.Approved, second.Approved);
    }
}
