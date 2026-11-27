namespace SentiX.Infrastructure;

public sealed record MarketEventRow(
    string EventId,
    string Source,
    string Author,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset IngestedAtUtc,
    string Headline,
    string Summary,
    string Body,
    string[] SourceTickers,
    string RawPayload);

public sealed record FastPathResultRow(
    long Id,
    string EventId,
    string DedupHash,
    bool IsDuplicate,
    string[] MatchedTickers,
    string[] MatchedSectorEtfs,
    double FinbertPositive,
    double FinbertNegative,
    double FinbertNeutral,
    double CompositeScore,
    bool PassToDecisionSwarm,
    string? DropReason,
    int ProcessingTimeUs,
    DateTimeOffset CreatedAtUtc);

public sealed record DecisionDebateRow(
    string ProposalId,
    string EventId,
    string PromptVersion,
    string FactJson,
    string? BullJson,
    string? BearJson,
    string? SoftRiskJson,
    string JudgeJson,
    string FinalAction,
    string? Ticker,
    double Confidence,
    int TotalTokens,
    int TotalLatencyMs,
    DateTimeOffset CreatedAtUtc);

public sealed record RiskEvaluationRow(
    long Id,
    string ProposalId,
    bool Approved,
    string TradingState,
    string Violations,
    int CalculatedQty,
    double? ClampedSlPct,
    double? ClampedTpPct,
    string? ClientOrderId,
    DateTimeOffset EvaluatedAtUtc);

public sealed record OrderRow(
    string ClientOrderId,
    string? AlpacaOrderId,
    string ProposalId,
    string Ticker,
    string Side,
    string OrderType,
    int Qty,
    decimal? LimitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    string Status,
    decimal? FilledAvgPrice,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DailyBenchmarkRow(
    DateOnly TradingDate,
    bool PaperMode,
    decimal PortfolioEquity,
    double DailyPnlPct,
    double CumReturnPct,
    decimal SpyClosePrice,
    double SpyCumReturnPct,
    int OpenPositions,
    int TradesExecuted);

public sealed record ScheduledEventRow(
    long Id,
    string EventType,
    string? Ticker,
    string Title,
    DateTimeOffset ScheduledAtUtc,
    string Source,
    DateTimeOffset SyncedAtUtc);

public sealed record SystemStateRow(
    int Id,
    string TradingState,
    string? Reason,
    string UpdatedBy,
    DateTimeOffset UpdatedAtUtc);
