namespace SentiX.Risk.Tests;

/// <summary>Pins the canonical public contract of specs/004 §2 (names, order, values).</summary>
public sealed class ContractTests
{
    [Fact]
    public void Enums_MatchSpecOrder()
    {
        Assert.Equal(["Active", "HaltedDailyLoss", "HaltedManual", "HaltedSystemError"], Enum.GetNames<TradingState>());
        Assert.Equal(["Hold", "Buy", "Sell"], Enum.GetNames<TradeAction>());
        Assert.Equal(["Equity", "SectorEtf"], Enum.GetNames<InstrumentType>());
        Assert.Equal(
            [
                "KillSwitchActive", "NonActionableProposal", "ShortingNotAllowed", "ConfidenceTooLow", "TickerNotAllowed",
                "OutsideTradingHours", "TickerCooldownActive", "DuplicatePosition", "MaxOpenPositions",
                "DailyLossLimitReached", "SectorExposureExceeded", "InsufficientBuyingPower", "InvalidBracketBounds",
            ],
            Enum.GetNames<RiskViolationCode>());
    }

    [Fact]
    public void TradeProposal_ExposesAllSpecFields()
    {
        var p = new TradeProposal("prop_12345", "evt_98a7f6", TradeAction.Buy, "XLE", InstrumentType.SectorEtf, 0.79, 48, 0.03m, 0.06m, "Concise synthesis.", "Acknowledged.");

        Assert.Equal("prop_12345", p.ProposalId);
        Assert.Equal("evt_98a7f6", p.EventId);
        Assert.Equal(TradeAction.Buy, p.Action);
        Assert.Equal("XLE", p.Ticker);
        Assert.Equal(InstrumentType.SectorEtf, p.InstrumentType);
        Assert.Equal(0.79, p.Confidence);
        Assert.Equal(48, p.ExpectedHorizonHours);
        Assert.Equal(0.03m, p.SuggestedStopLossPct);
        Assert.Equal(0.06m, p.SuggestedTakeProfitPct);
        Assert.Equal("Concise synthesis.", p.Rationale);
        Assert.Equal("Acknowledged.", p.DissentSummary);
        Assert.Equal(p, p with { });
    }

    [Fact]
    public void PortfolioRecords_ExposeAllSpecFields()
    {
        var position = new OpenPosition("AAPL", "TECH", 10, 2_000m, 200m);
        var order = new PendingOrder("SX-1", "MSFT", TradeAction.Buy, 3);
        var lastTrades = new Dictionary<string, DateTimeOffset> { ["AAPL"] = DateTimeOffset.UnixEpoch };
        var snapshot = new PortfolioSnapshot(TradingState.Active, 1m, 2m, -0.01, 3m, "TECH", [position], [order], lastTrades);

        Assert.Equal(("AAPL", "TECH", 10, 2_000m, 200m), (position.Ticker, position.Sector, position.Quantity, position.MarketValue, position.CurrentPrice));
        Assert.Equal(("SX-1", "MSFT", TradeAction.Buy, 3), (order.ClientOrderId, order.Ticker, order.Side, order.Quantity));
        Assert.Equal(TradingState.Active, snapshot.TradingState);
        Assert.Equal(1m, snapshot.Equity);
        Assert.Equal(2m, snapshot.BuyingPower);
        Assert.Equal(-0.01, snapshot.DailyPnLPct);
        Assert.Equal(3m, snapshot.CurrentAskPrice);
        Assert.Equal("TECH", snapshot.TargetSector);
        Assert.Same(position, Assert.Single(snapshot.OpenPositions));
        Assert.Same(order, Assert.Single(snapshot.PendingOrders));
        Assert.Same(lastTrades, snapshot.LastTradeTimesUtc);
    }

    [Fact]
    public void MarketClock_DefaultsToNonHoliday()
    {
        var clock = new MarketClock(DateTimeOffset.UnixEpoch);

        Assert.Equal(DateTimeOffset.UnixEpoch, clock.UtcNow);
        Assert.False(clock.IsUsMarketHoliday);
    }

    [Fact]
    public void RiskDecisionAndViolation_ExposeAllSpecFields()
    {
        var violation = new RiskViolation(RiskViolationCode.ConfidenceTooLow, "low");
        var decision = new RiskDecision(false, TradingState.HaltedManual, [violation], 0, 0.03m, 0.06m, null);

        Assert.Equal(RiskViolationCode.ConfidenceTooLow, violation.Code);
        Assert.Equal("low", violation.Message);
        Assert.False(decision.Approved);
        Assert.Equal(TradingState.HaltedManual, decision.EffectiveTradingState);
        Assert.Same(violation, Assert.Single(decision.Violations));
        Assert.Equal(0, decision.CalculatedQuantity);
        Assert.Equal(0.03m, decision.ClampedStopLossPct);
        Assert.Equal(0.06m, decision.ClampedTakeProfitPct);
        Assert.Null(decision.ClientOrderId);
    }

    [Fact]
    public void TradingStateTransitionContext_Defaults()
    {
        var ctx = new TradingStateTransitionContext(DateTimeOffset.UnixEpoch, new MarketClock(DateTimeOffset.UnixEpoch));

        Assert.Equal(DateTimeOffset.UnixEpoch, ctx.HaltedAtUtc);
        Assert.False(ctx.IsManualReset);
        Assert.Equal(TimeSpan.Zero, ctx.HealthyDuration);
    }
}
