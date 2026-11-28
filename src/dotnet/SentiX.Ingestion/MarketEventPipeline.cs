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

internal interface IPipelinePersistence
{
    Task RecordMarketEventAndFastPathAsync(MarketEvent marketEvent, string source, string rawPayload, FastPathResultRow fastPathRow, CancellationToken token);
    Task<string[]> GetUpcomingScheduleDescriptionsAsync(DateTimeOffset nowUtc, CancellationToken token);
    Task<bool> ProposalExistsAsync(string proposalId, CancellationToken token);
    Task<string?> ReadPersistedTradingStateAsync(CancellationToken token);
    Task PersistDebateAndRiskAsync(DecisionDebateRow debateRow, RiskEvaluationRow riskRow, CancellationToken token);
}

internal sealed class EfCorePipelinePersistence(IDbContextFactory<SentiXDbContext> dbFactory) : IPipelinePersistence
{
    public async Task RecordMarketEventAndFastPathAsync(
        MarketEvent marketEvent,
        string source,
        string rawPayload,
        FastPathResultRow fastPathRow,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var timestamp = marketEvent.PublishedAtUtc.ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO market_events
                (event_id, source, author, published_at_utc, headline, summary, body, source_tickers, raw_payload)
            VALUES ({marketEvent.EventId}, {source}, {marketEvent.Author}, {timestamp}, {marketEvent.Headline},
                {marketEvent.Summary}, {marketEvent.Body}, {marketEvent.SourceTickers}, CAST({rawPayload} AS jsonb))
            ON CONFLICT (event_id) DO NOTHING
            """, token);

        db.FastPathResults.Add(fastPathRow);
        await db.SaveChangesAsync(token);
    }

    public async Task<string[]> GetUpcomingScheduleDescriptionsAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var windowEnd = nowUtc.AddHours(48);
        var upcoming = await db.ScheduledEvents.AsNoTracking()
            .Where(row => row.ScheduledAtUtc >= nowUtc && row.ScheduledAtUtc <= windowEnd)
            .OrderBy(row => row.ScheduledAtUtc)
            .Take(20)
            .Select(row => new { row.EventType, row.Ticker, row.Title, row.ScheduledAtUtc })
            .ToArrayAsync(token);

        return upcoming
            .Select(item => FormattableString.Invariant(
                $"{item.EventType}:{(item.Ticker ?? "MACRO")} - {item.Title} @ {item.ScheduledAtUtc:yyyy-MM-ddTHH:mmZ}"))
            .ToArray();
    }

    public async Task<bool> ProposalExistsAsync(string proposalId, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.DecisionDebates.AsNoTracking()
            .AnyAsync(row => row.ProposalId == proposalId, token);
    }

    public async Task<string?> ReadPersistedTradingStateAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.SystemState.AsNoTracking()
            .Where(row => row.Id == 1)
            .Select(row => row.TradingState)
            .SingleOrDefaultAsync(token);
    }

    public async Task PersistDebateAndRiskAsync(
        DecisionDebateRow debateRow,
        RiskEvaluationRow riskRow,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        db.DecisionDebates.Add(debateRow);
        db.RiskEvaluations.Add(riskRow);
        await db.SaveChangesAsync(token);
    }
}

public sealed class MarketEventPipeline : IMarketEventPipeline, IDisposable
{
    private static readonly TimeSpan LocalOrderHistoryWindow = TimeSpan.FromHours(48);

    private readonly IDbContextFactory<SentiXDbContext> dbFactory;
    private readonly NativeBridgeClient native;
    private readonly ILogger<MarketEventPipeline> logger;
    private readonly IDecisionSwarmOrchestrator? decisionSwarm;
    private readonly IMarketQuoteCache? quoteCache;
    private readonly UniverseConfig? universe;
    private readonly TimeProvider clock;
    private readonly IPaperExecutionEngine? paperExecution;
    private readonly AlpacaPaperOptions? paperOptions;
    private readonly IPaperPortfolioSnapshotProvider? paperPortfolioProvider;
    private readonly IPaperOrderAuditStore auditStore;
    private readonly IAlpacaPaperBrokerClient? paperBrokerClient;
    private readonly IPipelinePersistence persistence;
    private readonly SemaphoreSlim migration = new(1, 1);
    private readonly SemaphoreSlim armedExecutionGate = new(1, 1);
    private volatile bool migrated;

    public MarketEventPipeline(
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
        IPaperOrderAuditStore? orderAuditStore = null,
        IAlpacaPaperBrokerClient? paperBrokerClient = null)
        : this(
            dbFactory,
            native,
            logger,
            isConfigured,
            decisionSwarm,
            quoteCache,
            universe,
            timeProvider,
            paperExecution,
            paperOptions,
            paperPortfolioProvider,
            orderAuditStore,
            paperBrokerClient,
            pipelinePersistence: null)
    {
    }

    internal MarketEventPipeline(
        IDbContextFactory<SentiXDbContext> dbFactory,
        NativeBridgeClient native,
        ILogger<MarketEventPipeline> logger,
        bool isConfigured,
        IDecisionSwarmOrchestrator? decisionSwarm,
        IMarketQuoteCache? quoteCache,
        UniverseConfig? universe,
        TimeProvider? timeProvider,
        IPaperExecutionEngine? paperExecution,
        AlpacaPaperOptions? paperOptions,
        IPaperPortfolioSnapshotProvider? paperPortfolioProvider,
        IPaperOrderAuditStore? orderAuditStore,
        IAlpacaPaperBrokerClient? paperBrokerClient,
        IPipelinePersistence? pipelinePersistence)
    {
        this.dbFactory = dbFactory;
        this.native = native;
        this.logger = logger;
        IsConfigured = isConfigured;
        this.decisionSwarm = decisionSwarm;
        this.quoteCache = quoteCache;
        this.universe = universe;
        clock = timeProvider ?? TimeProvider.System;
        this.paperExecution = paperExecution;
        this.paperOptions = paperOptions;
        this.paperPortfolioProvider = paperPortfolioProvider;
        auditStore = orderAuditStore ?? new EfCorePaperOrderAuditStore(dbFactory, logger);
        this.paperBrokerClient = paperBrokerClient;
        persistence = pipelinePersistence ?? new EfCorePipelinePersistence(dbFactory);
    }

    public bool IsConfigured { get; }

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
        string source = JsonSerializer.SerializeToElement(marketEvent.Source, SentiXJson.Options).GetString()!;

        var attempt = await native.ScoreEventAsync(marketEvent, token);
        var score = attempt.Value;
        string? reason;
        if (attempt.IsFailClosed)
        {
            logger.LogWarning("Native scoring failed closed for {EventId}: {Failure} ({ErrorCode}).",
                marketEvent.EventId, attempt.Failure, attempt.ErrorCode);
            reason = "FAIL_CLOSED";
        }
        else
        {
            reason = score?.DropReason is { } drop
                ? JsonSerializer.SerializeToElement(drop, SentiXJson.Options).GetString()
                : null;
        }

        var fastPathRow = new FastPathResultRow(
            0, marketEvent.EventId, score?.DedupHash ?? "", score?.IsDuplicate ?? false, score?.MatchedTickers ?? [],
            score?.MatchedSectorEtfs ?? [], score?.Finbert.Positive ?? 0, score?.Finbert.Negative ?? 0,
            score?.Finbert.Neutral ?? 1, score?.Finbert.CompositeScore ?? 0, attempt.PassToDecisionSwarm,
            reason, score?.ProcessingTimeUs ?? 0, default);

        await persistence.RecordMarketEventAndFastPathAsync(marketEvent, source, rawPayload, fastPathRow, token);

        if (attempt.PassToDecisionSwarm && score is not null && decisionSwarm is not null)
        {
            await EvaluateDebateAndRiskAsync(marketEvent, score, token);
        }
    }

    private async Task EvaluateDebateAndRiskAsync(
        MarketEvent marketEvent,
        FastPathResult score,
        CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var upcomingDescriptions = await persistence.GetUpcomingScheduleDescriptionsAsync(now, token);

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

        if (await persistence.ProposalExistsAsync(proposal.ProposalId, token))
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

        if (paperOptions is not { TradingEnabled: true })
        {
            // Shadow Mode strictly uses the synthetic $100k empty portfolio with zero broker HTTP calls.
            var persistedTradingState = await persistence.ReadPersistedTradingStateAsync(token);
            var tradingState = ParsePersistedTradingState(persistedTradingState);
            var (_, _, shadowDecision) = EvaluateHardRiskWithInputs(
                proposal, ticker, quoteCache, universe, tradingState, evaluatedAtUtc);

            await persistence.PersistDebateAndRiskAsync(
                debateRow,
                BuildRiskEvaluationRow(proposal.ProposalId, shadowDecision, evaluatedAtUtc),
                token);
            return;
        }

        await ExecuteArmedEvaluationAndOrderAsync(
            proposal,
            ticker,
            evaluatedAtUtc,
            persistence.ReadPersistedTradingStateAsync,
            (riskRow, ct) => persistence.PersistDebateAndRiskAsync(debateRow, riskRow, ct),
            token);
    }

    internal async Task ExecuteArmedEvaluationAndOrderAsync(
        SentiX.Domain.TradeProposal proposal,
        string? ticker,
        DateTimeOffset evaluatedAtUtc,
        Func<CancellationToken, Task<string?>> readPersistedTradingStateAsync,
        Func<RiskEvaluationRow, CancellationToken, Task> persistDebateAndRiskAsync,
        CancellationToken token)
    {
        PaperOrderExecutionRequest? reservedExecRequest = null;

        // Serialize: (1) reconcile expired PENDING_SUBMIT intents -> (2) read authoritative snapshot ->
        // (3) evaluate HardRiskManager -> (4) commit durable PENDING_SUBMIT reservation.
        await armedExecutionGate.WaitAsync(token);
        try
        {
            await auditStore.ReconcileExpiredPendingOrdersAsync(paperBrokerClient, clock, token);

            var rawTradingState = await readPersistedTradingStateAsync(token);
            var tradingState = ParsePersistedTradingState(rawTradingState);
            var armedEval = await EvaluateArmedPaperRiskAsync(proposal, ticker, tradingState, evaluatedAtUtc, token);

            if (!armedEval.SnapshotAvailable)
            {
                var unavailableViolationsJson = JsonSerializer.Serialize(
                    new[] { new { code = "PAPER_PORTFOLIO_UNAVAILABLE", message = armedEval.FailureReason ?? "Authoritative paper portfolio snapshot is unavailable." } },
                    SentiXJson.Options);

                var failedClosedRiskRow = new RiskEvaluationRow(
                    0,
                    proposal.ProposalId,
                    Approved: false,
                    PersistedTradingStateFormat.ToDatabaseValue(TradingState.HaltedSystemError),
                    unavailableViolationsJson,
                    CalculatedQty: 0,
                    ClampedSlPct: null,
                    ClampedTpPct: null,
                    ClientOrderId: null,
                    evaluatedAtUtc);

                await persistDebateAndRiskAsync(failedClosedRiskRow, token);
                return;
            }

            var riskDecision = armedEval.Decision!;
            await persistDebateAndRiskAsync(BuildRiskEvaluationRow(proposal.ProposalId, riskDecision, evaluatedAtUtc), token);

            if (paperExecution is not null &&
                riskDecision.Approved &&
                proposal.Action != DomainTradeAction.Hold &&
                !string.IsNullOrWhiteSpace(riskDecision.ClientOrderId) &&
                !string.IsNullOrWhiteSpace(ticker))
            {
                var baseRequest = PaperOrderExecutionRequest.FromRiskDecision(armedEval.Proposal!, riskDecision, armedEval.EntryPrice);
                var candidateRequest = proposal.Action == DomainTradeAction.Sell && armedEval.OpenLongQuantity > 0
                    ? baseRequest with { IsClosingLongPosition = true, OpenLongQuantity = armedEval.OpenLongQuantity }
                    : baseRequest;

                var pendingIntentRow = BuildPendingSubmitRow(
                    proposal.ProposalId,
                    ticker,
                    proposal.Action,
                    candidateRequest,
                    clock.GetUtcNow());

                var reservation = await auditStore.TryReserveOrderIntentAsync(
                    pendingIntentRow,
                    paperBrokerClient,
                    clock,
                    token);

                if (reservation == OrderIntentReservationStatus.ReservedForSubmit)
                {
                    reservedExecRequest = candidateRequest;
                }
            }
        }
        finally
        {
            armedExecutionGate.Release();
        }

        if (reservedExecRequest is not null && paperExecution is not null && !string.IsNullOrWhiteSpace(ticker))
        {
            await SubmitReservedOrderAsync(
                proposal.ProposalId,
                ticker,
                proposal.Action,
                reservedExecRequest,
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

        return TryEvaluateArmedHardRiskFromSnapshots(
            proposal,
            normalizedTicker,
            quoteCache,
            universe,
            tradingState,
            accountResult.Snapshot,
            recentOrdersResult.Orders,
            evaluatedAtUtc);
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
        var eval = TryEvaluateArmedHardRiskFromSnapshots(
            proposal, normalizedTicker, quoteCache, universe, tradingState, accountSnapshot, recentLocalOrders, nowUtc);
        if (!eval.SnapshotAvailable)
        {
            throw new InvalidOperationException(eval.FailureReason ?? "Armed risk snapshot validation failed closed.");
        }

        return (eval.Proposal!, eval.EntryPrice, eval.Decision!, eval.OpenLongQuantity);
    }

    internal static (bool SnapshotAvailable, string? FailureReason, RiskProposal? Proposal, decimal EntryPrice, RiskDecision? Decision, int OpenLongQuantity)
        TryEvaluateArmedHardRiskFromSnapshots(
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

        var localPricesByClientOrderId = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (var row in recentLocalOrders)
        {
            if (!string.IsNullOrWhiteSpace(row.ClientOrderId) && row.LimitPrice is > 0m)
            {
                localPricesByClientOrderId[row.ClientOrderId] = row.LimitPrice;
            }
        }

        var brokerOrderIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedPending = new Dictionary<string, (PendingOrder Order, decimal? LimitPrice, bool IsBrokerOrder)>(StringComparer.Ordinal);
        foreach (var brokerOrder in accountSnapshot.PendingOrders)
        {
            brokerOrderIds.Add(brokerOrder.ClientOrderId);
            decimal? brokerLimitPrice = null;
            if (accountSnapshot.PendingOrderLimitPrices is not null)
            {
                accountSnapshot.PendingOrderLimitPrices.TryGetValue(brokerOrder.ClientOrderId, out brokerLimitPrice);
            }

            brokerLimitPrice ??= localPricesByClientOrderId.GetValueOrDefault(brokerOrder.ClientOrderId);
            mergedPending[brokerOrder.ClientOrderId] = (brokerOrder, brokerLimitPrice, IsBrokerOrder: true);
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
                var order = new PendingOrder(
                    localOrder.ClientOrderId,
                    localOrder.Ticker,
                    pendingSide,
                    localOrder.Qty);
                mergedPending[localOrder.ClientOrderId] = (order, localOrder.LimitPrice, IsBrokerOrder: false);
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

        var localUnsubmittedBuyNotional = 0m;
        IReadOnlyList<OpenPosition> effectiveOpenPositions = accountSnapshot.OpenPositions;

        if (proposal.Action == DomainTradeAction.Buy)
        {
            var summedPendingBuysByTicker = new Dictionary<string, (int Qty, decimal Notional, string Sector)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (order, explicitLimitPrice, isBrokerOrder) in mergedPending.Values)
            {
                if (order.Side != RiskTradeAction.Buy)
                {
                    continue;
                }

                if (order.Quantity <= 0)
                {
                    return (false, $"Pending BUY order '{order.ClientOrderId}' has non-positive quantity.", null, 0m, null, 0);
                }

                if (!AlpacaPaperPortfolioSnapshotProvider.TryResolveKnownSector(order.Ticker, universe, out var pendingSector))
                {
                    return (false, $"Pending BUY order '{order.ClientOrderId}' references unknown ticker '{order.Ticker}'.", null, 0m, null, 0);
                }

                var unitPrice = explicitLimitPrice is > 0m
                    ? explicitLimitPrice.Value
                    : ResolveAskPrice(order.Ticker, quoteCache);
                if (unitPrice <= 0m)
                {
                    return (false, $"Cannot determine positive price for pending BUY order '{order.ClientOrderId}' ({order.Ticker}).", null, 0m, null, 0);
                }

                var orderNotional = order.Quantity * unitPrice;
                if (!isBrokerOrder)
                {
                    localUnsubmittedBuyNotional += orderNotional;
                }

                if (summedPendingBuysByTicker.TryGetValue(order.Ticker, out var accumulated))
                {
                    summedPendingBuysByTicker[order.Ticker] = (
                        accumulated.Qty + order.Quantity,
                        accumulated.Notional + orderNotional,
                        accumulated.Sector);
                }
                else
                {
                    summedPendingBuysByTicker[order.Ticker] = (order.Quantity, orderNotional, pendingSector);
                }
            }

            if (summedPendingBuysByTicker.Count > 0)
            {
                var projectedPositions = new List<OpenPosition>(accountSnapshot.OpenPositions.Count + summedPendingBuysByTicker.Count);
                foreach (var held in accountSnapshot.OpenPositions)
                {
                    if (summedPendingBuysByTicker.Remove(held.Ticker, out var addedPending))
                    {
                        projectedPositions.Add(held with
                        {
                            Quantity = held.Quantity + addedPending.Qty,
                            MarketValue = held.MarketValue + addedPending.Notional
                        });
                    }
                    else
                    {
                        projectedPositions.Add(held);
                    }
                }

                foreach (var (pendingTicker, pendingTotal) in summedPendingBuysByTicker)
                {
                    projectedPositions.Add(new OpenPosition(
                        pendingTicker.Trim().ToUpperInvariant(),
                        pendingTotal.Sector,
                        pendingTotal.Qty,
                        pendingTotal.Notional,
                        pendingTotal.Notional / pendingTotal.Qty));
                }

                effectiveOpenPositions = projectedPositions;
            }
        }

        var effectiveBuyingPower = Math.Max(0m, accountSnapshot.BuyingPower - localUnsubmittedBuyNotional);

        var authoritativePortfolio = new PortfolioSnapshot(
            tradingState,
            accountSnapshot.Equity,
            effectiveBuyingPower,
            accountSnapshot.DailyPnLPct,
            askPrice,
            targetSector,
            effectiveOpenPositions,
            mergedPending.Values.Select(static entry => entry.Order).ToArray(),
            lastTradeTimes);

        var policy = new RiskPolicy { AllowedTickers = allowedTickers };
        var decision = HardRiskManager.Evaluate(riskProposal, authoritativePortfolio, new MarketClock(nowUtc), policy);
        return (true, null, riskProposal, askPrice, decision, openLongQty);
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
        CancellationToken token,
        IAlpacaPaperBrokerClient? brokerClient = null)
    {
        var baseRequest = PaperOrderExecutionRequest.FromRiskDecision(riskProposal, riskDecision, entryPrice);
        var execRequest = action == DomainTradeAction.Sell && openLongQuantity > 0
            ? baseRequest with { IsClosingLongPosition = true, OpenLongQuantity = openLongQuantity }
            : baseRequest;

        var pendingIntentRow = BuildPendingSubmitRow(
            proposalId,
            normalizedTicker,
            action,
            execRequest,
            clock.GetUtcNow());

        var reservation = await orderAuditStore.TryReserveOrderIntentAsync(pendingIntentRow, brokerClient, clock, token);
        if (reservation != OrderIntentReservationStatus.ReservedForSubmit)
        {
            return;
        }

        await SubmitReservedOrderAsync(
            proposalId,
            normalizedTicker,
            action,
            execRequest,
            orderAuditStore,
            executionEngine,
            clock,
            logger,
            token);
    }

    internal static bool IsUncertainBrokerOutcome(PaperExecutionResult result) =>
        result.Status == ExecutionOutcomeStatus.FailedBroker &&
        result.RejectionCode is
            ExecutionRejectionCode.BrokerTimeout or
            ExecutionRejectionCode.BrokerNetworkError or
            ExecutionRejectionCode.BrokerServerError or
            ExecutionRejectionCode.MalformedBrokerResponse;

    private static async Task SubmitReservedOrderAsync(
        string proposalId,
        string normalizedTicker,
        DomainTradeAction action,
        PaperOrderExecutionRequest execRequest,
        IPaperOrderAuditStore orderAuditStore,
        IPaperExecutionEngine executionEngine,
        TimeProvider clock,
        ILogger logger,
        CancellationToken token)
    {
        var expectedClientOrderId = execRequest.RiskDecision.ClientOrderId!;
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
                "Paper order execution threw unexpectedly for ProposalId={ProposalId}, ClientOrderId={ClientOrderId} ({ErrorType}); keeping PENDING_SUBMIT fail-closed.",
                proposalId,
                expectedClientOrderId,
                ex.GetType().Name);
            return;
        }

        if (IsUncertainBrokerOutcome(execResult))
        {
            logger.LogWarning(
                "Leaving order {ClientOrderId} (ProposalId={ProposalId}) in PENDING_SUBMIT after uncertain broker outcome ({RejectionCode}) for fail-closed reconciliation.",
                expectedClientOrderId,
                proposalId,
                execResult.RejectionCode);
            return;
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

    private static RiskEvaluationRow BuildRiskEvaluationRow(
        string proposalId,
        RiskDecision riskDecision,
        DateTimeOffset evaluatedAtUtc)
    {
        var violationsJson = JsonSerializer.Serialize(
            riskDecision.Violations.Select(static v => new { code = v.Code.ToString(), message = v.Message }),
            SentiXJson.Options);

        return new RiskEvaluationRow(
            0,
            proposalId,
            riskDecision.Approved,
            riskDecision.EffectiveTradingState.ToString().ToUpperInvariant(),
            violationsJson,
            riskDecision.CalculatedQuantity,
            riskDecision.ClampedStopLossPct is { } sl ? (double)sl : null,
            riskDecision.ClampedTakeProfitPct is { } tp ? (double)tp : null,
            riskDecision.ClientOrderId,
            evaluatedAtUtc);
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

    public void Dispose()
    {
        migration.Dispose();
        armedExecutionGate.Dispose();
    }
}
