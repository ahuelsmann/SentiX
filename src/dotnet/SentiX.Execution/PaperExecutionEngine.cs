using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Risk;

namespace SentiX.Execution;

/// <summary>Phase 4 Alpaca Paper execution engine contract (Spec 008).</summary>
public interface IPaperExecutionEngine
{
    /// <summary>
    /// Evaluates all fail-closed safety invariants and submits a paper order only when every check passes.
    /// </summary>
    Task<PaperExecutionResult> ExecuteAsync(PaperOrderExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed Phase 4 Paper Execution Engine (Spec 008).
/// Enforces:
/// <list type="number">
/// <item><description><c>TradingEnabled == true</c> and strict <c>https://paper-api.alpaca.markets</c> configuration</description></item>
/// <item><description>Persisted kill-switch / <c>TradingState == TradingState.Active</c> (fail-closed if halted or unreadable)</description></item>
/// <item><description>Approved <see cref="RiskDecision"/> with <c>EffectiveTradingState == Active</c></description></item>
/// <item><description>Long-Only (<c>AllowShorting == false</c>) and whole integer shares (<c>Quantity &gt;= 1</c>)</description></item>
/// <item><description>Mandatory bracket bounds (<c>0 &lt; StopLossPrice &lt; EntryPrice &lt; TakeProfitPrice</c>)</description></item>
/// <item><description>Deterministic <c>client_order_id</c> via <see cref="ClientOrderIdGenerator"/></description></item>
/// </list>
/// </summary>
public sealed class PaperExecutionEngine : IPaperExecutionEngine
{
    private readonly AlpacaPaperOptions _options;
    private readonly ITradingStateProvider _tradingStateProvider;
    private readonly IAlpacaPaperBrokerClient _brokerClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new <see cref="PaperExecutionEngine"/>.</summary>
    public PaperExecutionEngine(
        AlpacaPaperOptions options,
        ITradingStateProvider tradingStateProvider,
        IAlpacaPaperBrokerClient brokerClient,
        TimeProvider? timeProvider = null,
        ILogger<PaperExecutionEngine>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tradingStateProvider = tradingStateProvider ?? throw new ArgumentNullException(nameof(tradingStateProvider));
        _brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<PaperExecutionResult> ExecuteAsync(
        PaperOrderExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Proposal);
        ArgumentNullException.ThrowIfNull(request.RiskDecision);
        cancellationToken.ThrowIfCancellationRequested();

        var evaluatedAtUtc = _timeProvider.GetUtcNow();

        // 1. TradingEnabled + strict paper options safety gate.
        if (!_options.TryValidateReadyToTrade(out var optionsCode, out var optionsError))
        {
            _logger.LogInformation("Execution blocked by options gate ({Code}): {Reason}", optionsCode, optionsError);
            return RejectPreFlight(optionsCode, optionsError, evaluatedAtUtc);
        }

        // 2. Persisted kill-switch / TradingState check (must be readable AND Active).
        TradingStateReadResult persistedState;
        try
        {
            persistedState = await _tradingStateProvider.GetCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Execution blocked because persisted trading state read failed ({ErrorType}).", ex.GetType().Name);
            return RejectPreFlight(
                ExecutionRejectionCode.TradingStateUnreadable,
                $"Persisted trading state read failed ({ex.GetType().Name}).",
                evaluatedAtUtc,
                TradingState.HaltedSystemError);
        }

        if (!persistedState.Success)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.TradingStateUnreadable,
                persistedState.ErrorReason ?? "Persisted trading state could not be read.",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (persistedState.State != TradingState.Active)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.KillSwitchActive,
                $"Persisted trading state is {persistedState.State}; execution halted.",
                evaluatedAtUtc,
                persistedState.State);
        }

        // 3. Hard-Risk approval & effective state check.
        var risk = request.RiskDecision;
        if (!risk.Approved)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.RiskNotApproved,
                "Hard-risk decision is not approved.",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (risk.EffectiveTradingState != TradingState.Active)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.KillSwitchActive,
                $"Risk decision effective trading state is {risk.EffectiveTradingState}.",
                evaluatedAtUtc,
                risk.EffectiveTradingState);
        }

        // 4. Proposal action, whole shares, and Long-Only enforcement.
        var proposal = request.Proposal;
        if (proposal.Action == TradeAction.Hold)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.RiskNotApproved,
                "Proposal action HOLD is not executable.",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (proposal.Action is not (TradeAction.Buy or TradeAction.Sell))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.RiskNotApproved,
                $"Unsupported trade action {proposal.Action}.",
                evaluatedAtUtc,
                persistedState.State);
        }

        var quantity = risk.CalculatedQuantity;
        if (quantity < 1)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidQuantity,
                $"Order quantity {quantity} must be at least 1 whole integer share.",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (proposal.Action == TradeAction.Sell &&
            (!request.IsClosingLongPosition || request.OpenLongQuantity < quantity))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.ShortingNotAllowed,
                "Short selling is disabled (AllowShorting=false); SELL requires a verified open long position.",
                evaluatedAtUtc,
                persistedState.State);
        }

        // 5. Ticker sanity check.
        var ticker = (proposal.Ticker ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsValidTicker(ticker))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidTicker,
                $"Ticker '{proposal.Ticker}' is invalid or exceeds {ClientOrderIdGenerator.MaxTickerLength} characters.",
                evaluatedAtUtc,
                persistedState.State);
        }

        // 6. Entry price & mandatory bracket bounds validation.
        if (request.EntryPrice <= 0m)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidEntryPrice,
                "EntryPrice must be strictly positive.",
                evaluatedAtUtc,
                persistedState.State);
        }

        decimal? effectiveLimitPrice = request.OrderType == ExecutionOrderType.Limit
            ? request.LimitPrice ?? request.EntryPrice
            : request.LimitPrice;

        if (request.OrderType == ExecutionOrderType.Limit && (effectiveLimitPrice is null || effectiveLimitPrice.Value <= 0m))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidEntryPrice,
                "LimitPrice must be strictly positive for limit orders.",
                evaluatedAtUtc,
                persistedState.State);
        }

        var referenceEntry = request.OrderType == ExecutionOrderType.Limit
            ? effectiveLimitPrice!.Value
            : request.EntryPrice;

        var orderClass = proposal.Action == TradeAction.Buy
            ? ExecutionOrderClass.Bracket
            : ExecutionOrderClass.Simple;

        if (orderClass == ExecutionOrderClass.Bracket)
        {
            if (risk.ClampedStopLossPct is <= 0m || risk.ClampedTakeProfitPct is <= 0m)
            {
                return RejectPreFlight(
                    ExecutionRejectionCode.InvalidBracketBounds,
                    "Clamped stop-loss and take-profit percentages must be positive.",
                    evaluatedAtUtc,
                    persistedState.State);
            }

            if (request.StopLossPrice is not { } stopLoss || request.TakeProfitPrice is not { } takeProfit)
            {
                return RejectPreFlight(
                    ExecutionRejectionCode.InvalidBracketBounds,
                    "Bracket orders require explicit StopLossPrice and TakeProfitPrice.",
                    evaluatedAtUtc,
                    persistedState.State);
            }

            if (stopLoss <= 0m || stopLoss >= referenceEntry || takeProfit <= referenceEntry)
            {
                return RejectPreFlight(
                    ExecutionRejectionCode.InvalidBracketBounds,
                    $"Long bracket bounds violated: StopLoss={stopLoss}, Entry={referenceEntry}, TakeProfit={takeProfit}.",
                    evaluatedAtUtc,
                    persistedState.State);
            }
        }

        // 7. Deterministic client_order_id canonical derivation & verification.
        string canonicalClientOrderId;
        try
        {
            canonicalClientOrderId = ClientOrderIdGenerator.Create(
                proposal.EventId ?? string.Empty,
                proposal.ProposalId ?? string.Empty,
                ticker,
                proposal.Action,
                evaluatedAtUtc);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidClientOrderId,
                $"Failed to generate canonical deterministic client_order_id: {ex.Message}",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (!IsValidClientOrderId(canonicalClientOrderId))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidClientOrderId,
                $"Generated canonical client_order_id '{canonicalClientOrderId}' is invalid.",
                evaluatedAtUtc,
                persistedState.State);
        }

        if (string.IsNullOrWhiteSpace(risk.ClientOrderId))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidClientOrderId,
                "Approved RiskDecision must provide a non-empty canonical ClientOrderId.",
                evaluatedAtUtc,
                persistedState.State);
        }

        var suppliedId = risk.ClientOrderId.Trim();
        if (!string.Equals(suppliedId, canonicalClientOrderId, StringComparison.Ordinal))
        {
            return RejectPreFlight(
                ExecutionRejectionCode.InvalidClientOrderId,
                "Supplied RiskDecision.ClientOrderId does not match canonical deterministic client_order_id for this proposal and trading date.",
                evaluatedAtUtc,
                persistedState.State);
        }

        var clientOrderId = canonicalClientOrderId;

        // 8. Dispatch to strict paper broker client.
        var wireOrder = new AlpacaBracketOrderRequest(
            clientOrderId,
            ticker,
            quantity,
            proposal.Action,
            request.OrderType,
            request.TimeInForce,
            orderClass,
            effectiveLimitPrice,
            orderClass == ExecutionOrderClass.Bracket ? request.StopLossPrice : null,
            orderClass == ExecutionOrderClass.Bracket ? request.TakeProfitPrice : null);

        var brokerResult = await _brokerClient.SubmitOrderAsync(wireOrder, cancellationToken).ConfigureAwait(false);

        if (brokerResult.IsSuccess)
        {
            return new PaperExecutionResult(
                ExecutionOutcomeStatus.Submitted,
                ExecutionRejectionCode.None,
                Reason: null,
                ClientOrderId: clientOrderId,
                ObservedTradingState: persistedState.State,
                Receipt: brokerResult.Receipt,
                BrokerResult: brokerResult,
                EvaluatedAtUtc: evaluatedAtUtc);
        }

        var outcomeStatus = brokerResult.IsDuplicate
            ? ExecutionOutcomeStatus.DuplicateOrder
            : ExecutionOutcomeStatus.FailedBroker;

        return new PaperExecutionResult(
            outcomeStatus,
            brokerResult.FailureCode,
            brokerResult.FailureReason,
            clientOrderId,
            persistedState.State,
            Receipt: null,
            BrokerResult: brokerResult,
            EvaluatedAtUtc: evaluatedAtUtc);
    }

    private static bool IsValidTicker(string ticker)
    {
        if (string.IsNullOrEmpty(ticker) || ticker.Length > ClientOrderIdGenerator.MaxTickerLength)
        {
            return false;
        }

        foreach (var ch in ticker)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidClientOrderId(string clientOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId) || clientOrderId.Length > ClientOrderIdGenerator.MaxLength)
        {
            return false;
        }

        foreach (var ch in clientOrderId)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static PaperExecutionResult RejectPreFlight(
        ExecutionRejectionCode code,
        string reason,
        DateTimeOffset evaluatedAtUtc,
        TradingState? observedState = null) =>
        new(
            ExecutionOutcomeStatus.RejectedPreFlight,
            code,
            reason,
            ClientOrderId: null,
            ObservedTradingState: observedState,
            Receipt: null,
            BrokerResult: null,
            EvaluatedAtUtc: evaluatedAtUtc);
}
