namespace SentiX.Risk;

/// <summary>Global trading state (kill-switch state machine, specs/004 §4).</summary>
public enum TradingState
{
    /// <summary>Normal operation; new entries are allowed.</summary>
    Active,

    /// <summary>Daily loss limit reached; resets on a later US trading day at or after 09:30 ET, or manually.</summary>
    HaltedDailyLoss,

    /// <summary>Operator kill-switch; resets only manually.</summary>
    HaltedManual,

    /// <summary>System failure (e.g. native fast-path degraded); resets after 30 s healthy or manually.</summary>
    HaltedSystemError,
}

/// <summary>Proposed trade action from the Decision-Swarm <c>JudgeAgent</c>.</summary>
public enum TradeAction
{
    /// <summary>No trade.</summary>
    Hold,

    /// <summary>Open a long position.</summary>
    Buy,

    /// <summary>Close an existing long position (Long-Only while shorting is disabled).</summary>
    Sell,
}

/// <summary>Instrument category of the proposal ticker.</summary>
public enum InstrumentType
{
    /// <summary>Single equity.</summary>
    Equity,

    /// <summary>Sector ETF.</summary>
    SectorEtf,
}

/// <summary>The 13 mandatory hard-risk rule codes (specs/004 §3), in evaluation order.</summary>
public enum RiskViolationCode
{
    /// <summary>Rule 1 — <c>KILL_SWITCH_ACTIVE</c>.</summary>
    KillSwitchActive,

    /// <summary>Rule 2 — <c>NON_ACTIONABLE_PROPOSAL</c>.</summary>
    NonActionableProposal,

    /// <summary>Rule 3 — <c>SHORTING_NOT_ALLOWED</c>.</summary>
    ShortingNotAllowed,

    /// <summary>Rule 4 — <c>CONFIDENCE_TOO_LOW</c>.</summary>
    ConfidenceTooLow,

    /// <summary>Rule 5 — <c>TICKER_NOT_ALLOWED</c>.</summary>
    TickerNotAllowed,

    /// <summary>Rule 6 — <c>OUTSIDE_TRADING_HOURS</c>.</summary>
    OutsideTradingHours,

    /// <summary>Rule 7 — <c>TICKER_COOLDOWN_ACTIVE</c>.</summary>
    TickerCooldownActive,

    /// <summary>Rule 8 — <c>DUPLICATE_POSITION</c>.</summary>
    DuplicatePosition,

    /// <summary>Rule 9 — <c>MAX_OPEN_POSITIONS</c>.</summary>
    MaxOpenPositions,

    /// <summary>Rule 10 — <c>DAILY_LOSS_LIMIT_REACHED</c>.</summary>
    DailyLossLimitReached,

    /// <summary>Rule 11 — <c>SECTOR_EXPOSURE_EXCEEDED</c>.</summary>
    SectorExposureExceeded,

    /// <summary>Rule 12 — <c>INSUFFICIENT_BUYING_POWER</c>.</summary>
    InsufficientBuyingPower,

    /// <summary>Rule 13 — <c>INVALID_BRACKET_BOUNDS</c>.</summary>
    InvalidBracketBounds,
}

/// <summary><c>TradeProposal</c> produced by the Decision-Swarm <c>JudgeAgent</c> (specs/003).</summary>
public sealed record TradeProposal(
    string ProposalId,
    string EventId,
    TradeAction Action,
    string Ticker,
    InstrumentType InstrumentType,
    double Confidence,
    int ExpectedHorizonHours,
    decimal SuggestedStopLossPct,
    decimal SuggestedTakeProfitPct,
    string Rationale,
    string DissentSummary);

/// <summary>An open broker position (positive <see cref="Quantity"/> = long).</summary>
public sealed record OpenPosition(
    string Ticker,
    string Sector,
    int Quantity,
    decimal MarketValue,
    decimal CurrentPrice);

/// <summary>An order submitted to the broker that is not yet filled or cancelled.</summary>
public sealed record PendingOrder(
    string ClientOrderId,
    string Ticker,
    TradeAction Side,
    int Quantity);

/// <summary>In-memory portfolio snapshot supplied by the host (no I/O inside <c>SentiX.Risk</c>).</summary>
public sealed record PortfolioSnapshot(
    TradingState TradingState,
    decimal Equity,
    decimal BuyingPower,
    double DailyPnLPct,
    decimal CurrentAskPrice,
    string TargetSector,
    IReadOnlyList<OpenPosition> OpenPositions,
    IReadOnlyList<PendingOrder> PendingOrders,
    IReadOnlyDictionary<string, DateTimeOffset> LastTradeTimesUtc);

/// <summary>Injected clock — the only source of time inside <c>SentiX.Risk</c>.</summary>
public sealed record MarketClock(
    DateTimeOffset UtcNow,
    bool IsUsMarketHoliday = false);

/// <summary>Hard-risk policy with the Phase-1 defaults of specs/004.</summary>
public sealed record RiskPolicy
{
    /// <summary>Shorting switch; <c>false</c> enforces Long-Only.</summary>
    public bool AllowShorting { get; init; } = false;

    /// <summary>Minimum JudgeAgent confidence (inclusive).</summary>
    public double MinConfidence { get; init; } = 0.70;

    /// <summary>Ticker allowlist populated from <c>config/universe.json</c>.</summary>
    public IReadOnlySet<string> AllowedTickers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-ticker cooldown in minutes.</summary>
    public int CooldownMinutes { get; init; } = 60;

    /// <summary>Maximum number of simultaneously open positions (exclusive upper bound for new buys).</summary>
    public int MaxOpenPositions { get; init; } = 5;

    /// <summary>Daily loss limit; <c>DailyPnLPct</c> must be strictly greater.</summary>
    public double MaxDailyLossPct { get; init; } = -0.02;

    /// <summary>Maximum sector exposure after the trade, as fraction of equity (inclusive).</summary>
    public decimal MaxSectorExposurePct { get; init; } = 0.25m;

    /// <summary>Target position size as fraction of equity.</summary>
    public decimal MaxPositionEquityPct { get; init; } = 0.05m;

    /// <summary>Maximum fraction of buying power that may be used.</summary>
    public decimal MaxBuyingPowerUsagePct { get; init; } = 0.95m;

    /// <summary>Minimum order notional in USD.</summary>
    public decimal MinOrderNotional { get; init; } = 100m;

    /// <summary>Lower stop-loss clamp bound.</summary>
    public decimal MinStopLossPct { get; init; } = 0.015m;

    /// <summary>Upper stop-loss clamp bound.</summary>
    public decimal MaxStopLossPct { get; init; } = 0.050m;

    /// <summary>Documented default stop-loss (informational in Phase 1, see specs/004 rule 13).</summary>
    public decimal DefaultStopLossPct { get; init; } = 0.030m;

    /// <summary>Lower take-profit clamp bound.</summary>
    public decimal MinTakeProfitPct { get; init; } = 0.030m;

    /// <summary>Upper take-profit clamp bound.</summary>
    public decimal MaxTakeProfitPct { get; init; } = 0.150m;

    /// <summary>Documented default take-profit (informational in Phase 1, see specs/004 rule 13).</summary>
    public decimal DefaultTakeProfitPct { get; init; } = 0.060m;

    /// <summary>Minimum reward-to-risk ratio (<c>TP &gt;= SL * ratio</c>).</summary>
    public decimal MinRewardToRiskRatio { get; init; } = 1.5m;
}

/// <summary>A single rule violation.</summary>
public sealed record RiskViolation(RiskViolationCode Code, string Message);

/// <summary>Full audit result of <see cref="HardRiskManager.Evaluate"/>.</summary>
public sealed record RiskDecision(
    bool Approved,
    TradingState EffectiveTradingState,
    IReadOnlyList<RiskViolation> Violations,
    int CalculatedQuantity,
    decimal? ClampedStopLossPct,
    decimal? ClampedTakeProfitPct,
    string? ClientOrderId);
