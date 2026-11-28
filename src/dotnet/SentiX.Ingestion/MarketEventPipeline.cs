using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentiX.DecisionSwarm;
using SentiX.Domain;
using SentiX.Execution;
using SentiX.Infrastructure;
using SentiX.NativeBridge;
using SentiX.Risk;
using RiskProposal = SentiX.Risk.TradeProposal;
using RiskTradeAction = SentiX.Risk.TradeAction;
using RiskInstrumentType = SentiX.Risk.InstrumentType;
using TradingState = SentiX.Risk.TradingState;
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
    TimeProvider? timeProvider = null,
    IPaperExecutionEngine? paperExecution = null,
    AlpacaPaperOptions? paperOptions = null,
    IPaperPortfolioSnapshotProvider? paperPortfolioProvider = null,
    IPaperOrderAuditStore? orderAuditStore = null) : IMarketEventPipeline, IDisposable
{
    private static readonly TimeSpan LocalOrderHistoryWindow = TimeSpan.FromHours(48);
    private readonly SemaphoreSlim migration = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly IPaperOrderAuditStore auditStore = orderAuditStore ?? new EfCorePaperOrderAuditStore(dbFactory, logger);
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

        var evaluatedAtUtc = clock.GetUtcNow();
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
            evaluatedAtUtc);

        var persistedTradingState = await db.SystemState.AsNoTracking()
            .Where(row => row.Id == 1)
            .Select(row => row.TradingState)
            .SingleOrDefaultAsync(token);
        var tradingState = ParsePersistedTradingState(persistedTradingState);

        var isArmedPaperMode = paperOptions is { TradingEnabled: true };
        RiskProposal riskProposal;
        decimal entryPrice;
        RiskDecision? riskDecision;
        int openLongQuantity = 0;

        if (!isArmedPaperMode)
        {
            // Shadow Mode strictly uses the synthetic $100k empty portfolio with zero broker HTTP calls.
            (riskProposal, entryPrice, riskDecision) = EvaluateHardRiskWithInputs(
                proposal, ticker, quoteCache, universe, tradingState, evaluatedAtUtc);
        }
        else
        {
            var armedEval = await EvaluateArmedPaperRiskAsync(proposal, ticker, tradingState, evaluatedAtUtc, token);
            if (!armedEval.SnapshotAvailable)
            {
                // Fail closed: NEVER fall back to synthetic $100k portfolio when TradingEnabled == true!
                var unavailableViolationsJson = JsonSerializer.Serialize(
                    new[] { new { code = "PAPER_PORTFOLIO_UNAVAILABLE", message = armedEval.FailureReason ?? "Authoritative paper portfolio snapshot is unavailable." } },
                    SentiXJson.Options);

                db.DecisionDebates.Add(debateRow);
                db.RiskEvaluations.Add(new RiskEvaluationRow(
                    0,
                    proposal.ProposalId,
                    Approved: false,
                    PersistedTradingStateFormat.ToDatabaseValue(TradingState.HaltedSystemError),
                    unavailableViolationsJson,
                    CalculatedQty: 0,
                    ClampedSlPct: null,
                    ClampedTpPct: null,
                    ClientOrderId: null,
                    evaluatedAtUtc));
                await db.SaveChangesAsync(token);
                return;
            }

            riskProposal = armedEval.Proposal!;
            entryPrice = armedEval.EntryPrice;
            riskDecision = armedEval.Decision!;
            openLongQuantity = armedEval.OpenLongQuantity;
        }

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
            evaluatedAtUtc);

        db.DecisionDebates.Add(debateRow);
        db.RiskEvaluations.Add(riskRow);
        await db.SaveChangesAsync(token);

        if (isArmedPaperMode &&
            paperExecution is not null &&
            riskDecision.Approved &&
            proposal.Action != DomainTradeAction.Hold &&
            !string.IsNullOrWhiteSpace(riskDecision.ClientOrderId) &&
            !string.IsNullOrWhiteSpace(ticker))
        {
            await ExecuteAndPersistPaperOrderWithWriteAheadAsync(
                proposal.ProposalId,
                ticker,
                proposal.Action,
                riskProposal,
                riskDecision,
                entryPrice,
                openLongQuantity,
                auditStore,
                paperExecution,
                clock,
                logger,
                token);
        }
    }

    internal async Task<(bool SnapshotAvailable, string? FailureReason, RiskProposal? Proposal, decimal EntryPrice, RiskDecision? Decision, int OpenLongQuantity)>
        EvaluateArmedPaperRiskAsync(
            SentiX.Domain.TradeProposal proposal,
            string? normalizedTicker,
            TradingState tradingState,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken token)
    {
        if (paperPortfolioProvider is null)
        {
            return (false, "Authoritative paper portfolio snapshot provider is not registered.", null, 0m, null, 0);
        }

        var accountResult = await paperPortfolioProvider.GetSnapshotAsync(universe, token);
        if (!accountResult.IsSuccess || accountResult.Snapshot is null)
        {
            logger.LogWarning(
                "Armed paper risk evaluation failed closed because Alpaca paper portfolio snapshot failed ({Code}): {Reason}",
                accountResult.FailureCode,
                accountResult.FailureReason);
            return (false, accountResult.FailureReason ?? "Failed to fetch authoritative Alpaca Paper account snapshot.", null, 0m, null, 0);
        }

        var recentOrdersResult = await auditStore.GetRecentOrdersAsync(evaluatedAtUtc - LocalOrderHistoryWindow, token);
        if (!recentOrdersResult.IsSuccess)
        {
            return (false, recentOrdersResult.ErrorReason ?? "Failed to read local orders history for cooldown/pending checks.", null, 0m, null, 0);
        }

        var (riskProposal, entryPrice, decision, openLongQty) = EvaluateArmedHardRiskFromSnapshots(
            proposal,
            normalizedTicker,
            quoteCache,
            universe,
            tradingState,
            accountResult.Snapshot,
            recentOrdersResult.Orders,
            evaluatedAtUtc);

        return (true, null, riskProposal, entryPrice, decision, openLongQty);
    }

    internal static (RiskProposal Proposal, decimal EntryPrice, RiskDecision Decision, int OpenLongQuantity)
        EvaluateArmedHardRiskFromSnapshots(
            SentiX.Domain.TradeProposal proposal,
            string? normalizedTicker,
            IMarketQuoteCache? quoteCache,
            UniverseConfig? universe,
            TradingState tradingState,
            PaperAccountSnapshot accountSnapshot,
            IReadOnlyList<OrderRow> recentLocalOrders,
            DateTimeOffset nowUtc)
    {
        var riskProposal = ToRiskProposal(proposal, normalizedTicker);
        var askPrice = ResolveAskPrice(normalizedTicker, quoteCache);
        var targetSector = AlpacaPaperPortfolioSnapshotProvider.ResolveSector(normalizedTicker ?? string.Empty, universe);
        var allowedTickers = universe?.EquitiesSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mergedPending = new Dictionary<string, PendingOrder>(StringComparer.Ordinal);
        foreach (var brokerOrder in accountSnapshot.PendingOrders)
        {
            mergedPending[brokerOrder.ClientOrderId] = brokerOrder;
        }

        var lastTradeTimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var localOrder in recentLocalOrders)
        {
            var status = localOrder.Status?.Trim().ToUpperInvariant() ?? string.Empty;
            if (status == "PENDING_SUBMIT" && !mergedPending.ContainsKey(localOrder.ClientOrderId))
            {
                var pendingSide = string.Equals(localOrder.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                    ? RiskTradeAction.Sell
                    : RiskTradeAction.Buy;
                mergedPending[localOrder.ClientOrderId] = new PendingOrder(
                    localOrder.ClientOrderId,
                    localOrder.Ticker,
                    pendingSide,
                    Math.Max(1, localOrder.Qty));
            }

            if (status is not ("REJECTED_PREFLIGHT" or "FAILED_BROKER") && !string.IsNullOrWhiteSpace(localOrder.Ticker))
            {
                var tradeTime = localOrder.SubmittedAtUtc >= localOrder.UpdatedAtUtc
                    ? localOrder.SubmittedAtUtc
                    : localOrder.UpdatedAtUtc;
                if (!lastTradeTimes.TryGetValue(localOrder.Ticker, out var existingTime) || tradeTime > existingTime)
                {
                    lastTradeTimes[localOrder.Ticker] = tradeTime;
                }
            }
        }

        var openLongQty = accountSnapshot.OpenPositions
            .Where(p => p.Quantity > 0 && string.Equals(p.Ticker, normalizedTicker, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Quantity);

        var authoritativePortfolio = new PortfolioSnapshot(
            tradingState,
            accountSnapshot.Equity,
            accountSnapshot.BuyingPower,
            accountSnapshot.DailyPnLPct,
            askPrice,
            targetSector,
            accountSnapshot.OpenPositions,
            mergedPending.Values.ToArray(),
            lastTradeTimes);

        var policy = new RiskPolicy { AllowedTickers = allowedTickers };
        var decision = HardRiskManager.Evaluate(riskProposal, authoritativePortfolio, new MarketClock(nowUtc), policy);
        return (riskProposal, askPrice, decision, openLongQty);
    }

    internal static async Task ExecuteAndPersistPaperOrderWithWriteAheadAsync(
        string proposalId,
        string normalizedTicker,
        DomainTradeAction action,
        RiskProposal riskProposal,
        RiskDecision riskDecision,
        decimal entryPrice,
        int openLongQuantity,
        IPaperOrderAuditStore orderAuditStore,
        IPaperExecutionEngine executionEngine,
        TimeProvider clock,
        ILogger logger,
        CancellationToken token)
    {
        var baseRequest = PaperOrderExecutionRequest.FromRiskDecision(riskProposal, riskDecision, entryPrice);
        var execRequest = action == DomainTradeAction.Sell && openLongQuantity > 0
            ? baseRequest with { IsClosingLongPosition = true, OpenLongQuantity = openLongQuantity }
            : baseRequest;

        var intentTimestampUtc = clock.GetUtcNow();
        var pendingIntentRow = BuildPendingSubmitRow(
            proposalId,
            normalizedTicker,
            action,
            execRequest,
            intentTimestampUtc);

        var disposition = await orderAuditStore.TryBeginOrderIntentAsync(pendingIntentRow, token);
        if (disposition != OrderIntentDisposition.ProceedToSubmit)
        {
            return;
        }

        PaperExecutionResult execResult;
        try
        {
            execResult = await executionEngine.ExecuteAsync(execRequest, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Paper order execution threw unexpectedly for ProposalId={ProposalId}, ClientOrderId={ClientOrderId} ({ErrorType}).",
                proposalId,
                pendingIntentRow.ClientOrderId,
                ex.GetType().Name);
            execResult = new PaperExecutionResult(
                ExecutionOutcomeStatus.FailedBroker,
                ExecutionRejectionCode.BrokerNetworkError,
                $"Unexpected execution exception ({ex.GetType().Name}).",
                pendingIntentRow.ClientOrderId,
                TradingState.HaltedSystemError,
                Receipt: null,
                BrokerResult: null,
                EvaluatedAtUtc: clock.GetUtcNow());
        }

        var completedRow = MapOrderRow(
            proposalId,
            normalizedTicker,
            action,
            execRequest,
            execResult,
            clock.GetUtcNow());

        await orderAuditStore.CompleteOrderIntentAsync(completedRow);
    }

    internal static OrderRow BuildPendingSubmitRow(
        string proposalId,
        string normalizedTicker,
        DomainTradeAction action,
        PaperOrderExecutionRequest request,
        DateTimeOffset nowUtc)
    {
        var side = action == DomainTradeAction.Buy ? "BUY" : "SELL";
        var orderType = request.OrderType == ExecutionOrderType.Market ? "MARKET" : "LIMIT";
        return new OrderRow(
            request.RiskDecision.ClientOrderId!,
            AlpacaOrderId: null,
            proposalId,
            normalizedTicker,
            side,
            orderType,
            request.RiskDecision.CalculatedQuantity,
            request.LimitPrice,
            request.StopLossPrice,
            request.TakeProfitPrice,
            Status: "PENDING_SUBMIT",
            FilledAvgPrice: null,
            SubmittedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc);
    }

    internal static OrderRow MapOrderRow(
        string proposalId,
        string normalizedTicker,
        DomainTradeAction action,
        PaperOrderExecutionRequest request,
        PaperExecutionResult result,
        DateTimeOffset nowUtc)
    {
        var clientOrderId = !string.IsNullOrWhiteSpace(result.ClientOrderId)
            ? result.ClientOrderId
            : request.RiskDecision.ClientOrderId!;
        var alpacaOrderId = string.IsNullOrWhiteSpace(result.Receipt?.OrderId)
            ? null
            : result.Receipt.OrderId.Trim();
        var side = action == DomainTradeAction.Buy ? "BUY" : "SELL";
        var orderType = request.OrderType == ExecutionOrderType.Market ? "MARKET" : "LIMIT";
        var status = result.Status switch
        {
            ExecutionOutcomeStatus.Submitted => !string.IsNullOrWhiteSpace(result.Receipt?.Status)
                ? result.Receipt.Status.Trim().ToUpperInvariant()
                : "SUBMITTED",
            ExecutionOutcomeStatus.DuplicateOrder => "DUPLICATE",
            ExecutionOutcomeStatus.RejectedPreFlight => "REJECTED_PREFLIGHT",
            ExecutionOutcomeStatus.FailedBroker => "FAILED_BROKER",
            _ => "FAILED_BROKER"
        };
        var submittedAtUtc = result.Status == ExecutionOutcomeStatus.Submitted
            ? (result.Receipt?.SubmittedAtUtc ?? nowUtc)
            : nowUtc;

        return new OrderRow(
            clientOrderId,
            alpacaOrderId,
            proposalId,
            normalizedTicker,
            side,
            orderType,
            request.RiskDecision.CalculatedQuantity,
            request.LimitPrice,
            request.StopLossPrice,
            request.TakeProfitPrice,
            status,
            result.Receipt?.FilledAvgPrice,
            submittedAtUtc,
            nowUtc);
    }

    internal static SentiX.Risk.TradingState ParsePersistedTradingState(string? raw)
    {
        var parsed = SystemStateParser.Parse(raw);
        return parsed.Success ? parsed.State : SentiX.Risk.TradingState.HaltedSystemError;
    }

    internal static RiskDecision EvaluateShadowHardRisk(
        SentiX.Domain.TradeProposal proposal,
        string? normalizedTicker,
        IMarketQuoteCache? quoteCache,
        UniverseConfig? universe,
        SentiX.Risk.TradingState tradingState,
        DateTimeOffset nowUtc) =>
        EvaluateHardRiskWithInputs(proposal, normalizedTicker, quoteCache, universe, tradingState, nowUtc).Decision;

    internal static (RiskProposal Proposal, decimal EntryPrice, RiskDecision Decision) EvaluateHardRiskWithInputs(
        SentiX.Domain.TradeProposal proposal,
        string? normalizedTicker,
        IMarketQuoteCache? quoteCache,
        UniverseConfig? universe,
        SentiX.Risk.TradingState tradingState,
        DateTimeOffset nowUtc)
    {
        var riskProposal = ToRiskProposal(proposal, normalizedTicker);
        var askPrice = ResolveAskPrice(normalizedTicker, quoteCache);
        var targetSector = AlpacaPaperPortfolioSnapshotProvider.ResolveSector(normalizedTicker ?? string.Empty, universe);
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
        var decision = HardRiskManager.Evaluate(riskProposal, shadowPortfolio, new MarketClock(nowUtc), policy);
        return (riskProposal, askPrice, decision);
    }

    private static RiskProposal ToRiskProposal(SentiX.Domain.TradeProposal proposal, string? normalizedTicker)
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

        return new RiskProposal(
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
    }

    private static decimal ResolveAskPrice(string? normalizedTicker, IMarketQuoteCache? quoteCache)
    {
        MarketQuoteSnapshot? tickerQuote = null;
        if (!string.IsNullOrWhiteSpace(normalizedTicker) && quoteCache is not null)
        {
            quoteCache.TryGet(normalizedTicker, out tickerQuote);
        }

        return tickerQuote is { AskPrice: > 0m } ? tickerQuote.AskPrice :
            tickerQuote is { MidPrice: > 0m } ? tickerQuote.MidPrice : 0m;
    }

    public void Dispose() => migration.Dispose();
}
