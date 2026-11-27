namespace SentiX.Risk.Tests;

/// <summary>Builders for a fully approvable baseline scenario; each test mutates exactly the dimension under test.</summary>
internal static class RiskTestData
{
    /// <summary>Monday 2026-11-30 10:00 EST (UTC-5) == 15:00 UTC — winter time, inside the window.</summary>
    public static readonly DateTimeOffset WinterMondayTenEt = new(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Wednesday 2026-07-15 10:00 EDT (UTC-4) == 14:00 UTC — summer time, inside the window.</summary>
    public static readonly DateTimeOffset SummerWednesdayTenEt = new(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);

    public static TradeProposal Proposal(
        TradeAction action = TradeAction.Buy,
        string ticker = "AAPL",
        double confidence = 0.80,
        decimal stopLoss = 0.03m,
        decimal takeProfit = 0.06m,
        string eventId = "evt_1",
        string proposalId = "prop_1") =>
        new(
            ProposalId: proposalId,
            EventId: eventId,
            Action: action,
            Ticker: ticker,
            InstrumentType: InstrumentType.Equity,
            Confidence: confidence,
            ExpectedHorizonHours: 48,
            SuggestedStopLossPct: stopLoss,
            SuggestedTakeProfitPct: takeProfit,
            Rationale: "Deterministic test rationale.",
            DissentSummary: "None.");

    public static PortfolioSnapshot Snapshot(
        TradingState state = TradingState.Active,
        decimal equity = 100_000m,
        decimal buyingPower = 50_000m,
        double dailyPnLPct = 0.0,
        decimal ask = 200m,
        string targetSector = "TECH",
        IReadOnlyList<OpenPosition>? positions = null,
        IReadOnlyList<PendingOrder>? pending = null,
        IReadOnlyDictionary<string, DateTimeOffset>? lastTrades = null) =>
        new(
            TradingState: state,
            Equity: equity,
            BuyingPower: buyingPower,
            DailyPnLPct: dailyPnLPct,
            CurrentAskPrice: ask,
            TargetSector: targetSector,
            OpenPositions: positions ?? [],
            PendingOrders: pending ?? [],
            LastTradeTimesUtc: lastTrades ?? new Dictionary<string, DateTimeOffset>());

    public static MarketClock Clock(DateTimeOffset? utcNow = null, bool holiday = false) =>
        new(utcNow ?? WinterMondayTenEt, holiday);

    public static RiskPolicy Policy(bool allowShorting = false) => new()
    {
        AllowShorting = allowShorting,
        AllowedTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AAPL", "MSFT", "NVDA", "AMD", "AMZN", "TSLA", "XOM", "CVX", "JPM", "LMT",
            "XLK", "XLE", "XLF", "XLV", "XLI", "ITA", "XLY", "SMH",
        },
    };

    public static OpenPosition Position(string ticker = "AAPL", int quantity = 10, string sector = "TECH", decimal marketValue = 2_000m) =>
        new(ticker, sector, quantity, marketValue, marketValue / Math.Max(quantity, 1));

    public static RiskDecision Evaluate(
        TradeProposal? proposal = null,
        PortfolioSnapshot? snapshot = null,
        MarketClock? clock = null,
        RiskPolicy? policy = null) =>
        HardRiskManager.Evaluate(proposal ?? Proposal(), snapshot ?? Snapshot(), clock ?? Clock(), policy ?? Policy());

    public static RiskViolationCode[] Codes(this RiskDecision decision) =>
        decision.Violations.Select(v => v.Code).ToArray();

    public static void AssertCodes(RiskDecision decision, params RiskViolationCode[] expected) =>
        Assert.Equal(expected, decision.Codes());
}
