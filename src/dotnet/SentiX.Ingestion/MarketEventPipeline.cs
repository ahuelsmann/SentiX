using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentiX.DecisionSwarm;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.NativeBridge;
using SentiX.Risk;
using RiskProposal = SentiX.Risk.TradeProposal;
using RiskTradeAction = SentiX.Risk.TradeAction;
using RiskInstrumentType = SentiX.Risk.InstrumentType;
using DomainTradeAction = SentiX.Domain.TradeAction;
using DomainInstrumentType = SentiX.Domain.InstrumentType;

namespace SentiX.Ingestion;

public interface IMarketEventPipeline
{
    bool IsConfigured { get; }
    Task EnsureReadyAsync(CancellationToken token);
    Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token);
    Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token);
}

public sealed class MarketEventPipeline(
    IDbContextFactory<SentiXDbContext> dbFactory,
    NativeBridgeClient native,
    ILogger<MarketEventPipeline> logger,
    bool isConfigured,
    IDecisionSwarmOrchestrator? decisionSwarm = null,
    IMarketQuoteCache? quoteCache = null,
    UniverseConfig? universe = null,
    TimeProvider? timeProvider = null) : IMarketEventPipeline, IDisposable
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private volatile bool migrated;

    public bool IsConfigured { get; } = isConfigured;

    // Apply the versioned schema before either collector opens its stream.
    public async Task EnsureReadyAsync(CancellationToken token)
    {
        if (migrated)
        {
            return;
        }

        await migration.WaitAsync(token);
        try
        {
            if (!migrated)
            {
                await using var context = await dbFactory.CreateDbContextAsync(token);
                await context.Database.MigrateAsync(token);
                migrated = true;
            }
        }
        finally
        {
            migration.Release();
        }
    }

    public async Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.MarketEvents.CountAsync(
            row => row.Source == "X_ALLOWLIST" && row.IngestedAtUtc >= start && row.IngestedAtUtc < start.AddDays(1),
            token);
    }

    public async Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        string source = JsonSerializer.SerializeToElement(marketEvent.Source, SentiXJson.Options).GetString()!;
        var timestamp = marketEvent.PublishedAtUtc.ToUniversalTime();
        // Preserve the canonical raw event on feed replay. Native deduplication evaluates each receipt.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO market_events
                (event_id, source, author, published_at_utc, headline, summary, body, source_tickers, raw_payload)
            VALUES ({marketEvent.EventId}, {source}, {marketEvent.Author}, {timestamp}, {marketEvent.Headline},
                {marketEvent.Summary}, {marketEvent.Body}, {marketEvent.SourceTickers}, CAST({rawPayload} AS jsonb))
            ON CONFLICT (event_id) DO NOTHING
            """, token);

        var attempt = await native.ScoreEventAsync(marketEvent, token);
        var score = attempt.Value;
        string? reason;
        if (attempt.IsFailClosed)
        {
            logger.LogWarning("Native scoring failed closed for {EventId}: {Failure} ({ErrorCode}).",
                marketEvent.EventId, attempt.Failure, attempt.ErrorCode);
            // The audit schema requires numeric fields. An empty hash and FAIL_CLOSED identify an unscored attempt.
            reason = "FAIL_CLOSED";
        }
        else
        {
            reason = score?.DropReason is { } drop
                ? JsonSerializer.SerializeToElement(drop, SentiXJson.Options).GetString()
                : null;
        }

        db.FastPathResults.Add(new FastPathResultRow(
            0, marketEvent.EventId, score?.DedupHash ?? "", score?.IsDuplicate ?? false, score?.MatchedTickers ?? [],
            score?.MatchedSectorEtfs ?? [], score?.Finbert.Positive ?? 0, score?.Finbert.Negative ?? 0,
            score?.Finbert.Neutral ?? 1, score?.Finbert.CompositeScore ?? 0, attempt.PassToDecisionSwarm,
            reason, score?.ProcessingTimeUs ?? 0, default));
        await db.SaveChangesAsync(token);

        if (attempt.PassToDecisionSwarm && score is not null && decisionSwarm is not null)
        {
            await EvaluateShadowDebateAndRiskAsync(db, marketEvent, score, token);
        }
    }

    private async Task EvaluateShadowDebateAndRiskAsync(
        SentiXDbContext db,
        MarketEvent marketEvent,
        FastPathResult score,
        CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var windowEnd = now.AddHours(48);
        var upcoming = await db.ScheduledEvents.AsNoTracking()
            .Where(row => row.ScheduledAtUtc >= now && row.ScheduledAtUtc <= windowEnd)
            .OrderBy(row => row.ScheduledAtUtc)
            .Take(20)
            .Select(row => new { row.EventType, row.Ticker, row.Title, row.ScheduledAtUtc })
            .ToArrayAsync(token);

        var upcomingDescriptions = upcoming
            .Select(item => FormattableString.Invariant(
                $"{item.EventType}:{(item.Ticker ?? "MACRO")} - {item.Title} @ {item.ScheduledAtUtc:yyyy-MM-ddTHH:mmZ}"))
            .ToArray();

        MarketQuoteSnapshot? quote = null;
        if (quoteCache is not null)
        {
            var candidateTicker = score.MatchedTickers.FirstOrDefault() ?? score.MatchedSectorEtfs.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidateTicker))
            {
                quoteCache.TryGet(candidateTicker, out quote);
            }
        }

        var outcome = await decisionSwarm!.EvaluateAsync(
            new DecisionDebateRequest(marketEvent, score, upcomingDescriptions, quote),
            token);
        var proposal = outcome.Proposal;

        var proposalExists = await db.DecisionDebates.AsNoTracking()
            .AnyAsync(row => row.ProposalId == proposal.ProposalId, token);
        if (proposalExists)
        {
            return;
        }

        var persistedAction = proposal.Action.ToString().ToUpperInvariant();
        var ticker = string.IsNullOrWhiteSpace(proposal.Ticker) ? null : proposal.Ticker.Trim().ToUpperInvariant();
        var debateRow = new DecisionDebateRow(
            proposal.ProposalId,
            marketEvent.EventId,
            outcome.PromptVersion,
            outcome.FactJson ?? JsonSerializer.Serialize(new { error = outcome.FailureReason ?? "unavailable" }, SentiXJson.Options),
            outcome.BullJson,
            outcome.BearJson,
            outcome.SoftRiskJson,
            outcome.JudgeJson ?? JsonSerializer.Serialize(proposal, SentiXJson.Options),
            persistedAction,
            ticker,
            proposal.Confidence,
            outcome.TotalTokens,
            (int)Math.Clamp(outcome.TotalLatencyMs, 0, int.MaxValue),
            now);

        var persistedTradingState = await db.SystemState.AsNoTracking()
            .Where(row => row.Id == 1)
            .Select(row => row.TradingState)
            .SingleOrDefaultAsync(token);
        var tradingState = ParsePersistedTradingState(persistedTradingState);

        // Phase 3 runs HardRiskManager strictly in shadow mode (virtual paper portfolio, zero broker orders).
        var riskDecision = EvaluateShadowHardRisk(proposal, ticker, quoteCache, universe, tradingState, now);
        var violationsJson = JsonSerializer.Serialize(
            riskDecision.Violations.Select(static v => new { code = v.Code.ToString(), message = v.Message }),
            SentiXJson.Options);

        var riskRow = new RiskEvaluationRow(
            0,
            proposal.ProposalId,
            riskDecision.Approved,
            riskDecision.EffectiveTradingState.ToString().ToUpperInvariant(),
            violationsJson,
            riskDecision.CalculatedQuantity,
            riskDecision.ClampedStopLossPct is { } sl ? (double)sl : null,
            riskDecision.ClampedTakeProfitPct is { } tp ? (double)tp : null,
            riskDecision.ClientOrderId,
            now);

        db.DecisionDebates.Add(debateRow);
        db.RiskEvaluations.Add(riskRow);
        await db.SaveChangesAsync(token);
    }

    internal static SentiX.Risk.TradingState ParsePersistedTradingState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SentiX.Risk.TradingState.HaltedSystemError;
        }

        return raw.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => SentiX.Risk.TradingState.Active,
            "HALTED_DAILY_LOSS" => SentiX.Risk.TradingState.HaltedDailyLoss,
            "HALTED_MANUAL" => SentiX.Risk.TradingState.HaltedManual,
            "HALTED_SYSTEM_ERROR" => SentiX.Risk.TradingState.HaltedSystemError,
            _ => SentiX.Risk.TradingState.HaltedSystemError,
        };
    }

    internal static RiskDecision EvaluateShadowHardRisk(
        SentiX.Domain.TradeProposal proposal,
        string? normalizedTicker,
        IMarketQuoteCache? quoteCache,
        UniverseConfig? universe,
        SentiX.Risk.TradingState tradingState,
        DateTimeOffset nowUtc)
    {
        var riskAction = proposal.Action switch
        {
            DomainTradeAction.Buy => RiskTradeAction.Buy,
            DomainTradeAction.Sell => RiskTradeAction.Sell,
            _ => RiskTradeAction.Hold
        };
        var riskInstrument = proposal.InstrumentType == DomainInstrumentType.SectorEtf
            ? RiskInstrumentType.SectorEtf
            : RiskInstrumentType.Equity;

        var riskProposal = new RiskProposal(
            proposal.ProposalId,
            proposal.EventId,
            riskAction,
            normalizedTicker ?? string.Empty,
            riskInstrument,
            proposal.Confidence,
            proposal.ExpectedHorizonHours,
            proposal.SuggestedStopLossPct,
            proposal.SuggestedTakeProfitPct,
            proposal.Rationale,
            proposal.DissentSummary);

        MarketQuoteSnapshot? tickerQuote = null;
        if (!string.IsNullOrWhiteSpace(normalizedTicker) && quoteCache is not null)
        {
            quoteCache.TryGet(normalizedTicker, out tickerQuote);
        }

        var askPrice = tickerQuote is { AskPrice: > 0m } ? tickerQuote.AskPrice :
            tickerQuote is { MidPrice: > 0m } ? tickerQuote.MidPrice : 0m;

        var targetSector = universe?.Watchlist
            .FirstOrDefault(e => string.Equals(e.Ticker, normalizedTicker, StringComparison.OrdinalIgnoreCase))
            ?.Sector ?? "ETF";

        var allowedTickers = universe?.EquitiesSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shadowPortfolio = new PortfolioSnapshot(
            tradingState,
            Equity: 100_000m,
            BuyingPower: 100_000m,
            DailyPnLPct: 0d,
            CurrentAskPrice: askPrice,
            TargetSector: targetSector,
            OpenPositions: [],
            PendingOrders: [],
            LastTradeTimesUtc: new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        var policy = new RiskPolicy { AllowedTickers = allowedTickers };
        return HardRiskManager.Evaluate(riskProposal, shadowPortfolio, new MarketClock(nowUtc), policy);
    }

    public void Dispose() => migration.Dispose();
}
