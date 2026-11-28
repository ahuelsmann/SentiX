using System.Globalization;
using SentiX.Risk;

namespace SentiX.Execution;

/// <summary>Supported Alpaca REST order types for Phase 4 paper execution.</summary>
public enum ExecutionOrderType
{
    /// <summary>Limit order with explicit <c>limit_price</c>.</summary>
    Limit,

    /// <summary>Market order.</summary>
    Market
}

/// <summary>Supported Alpaca order time-in-force values.</summary>
public enum ExecutionTimeInForce
{
    /// <summary>Good for regular market day.</summary>
    Day,

    /// <summary>Good until cancelled.</summary>
    Gtc
}

/// <summary>Alpaca order class.</summary>
public enum ExecutionOrderClass
{
    /// <summary>Entry order with mandatory take-profit and stop-loss legs.</summary>
    Bracket,

    /// <summary>Single-leg exit order (permitted only for verified closing long exits).</summary>
    Simple
}

/// <summary>Terminal outcome status of an execution attempt.</summary>
public enum ExecutionOutcomeStatus
{
    /// <summary>Order was accepted by the Alpaca Paper API.</summary>
    Submitted,

    /// <summary>Execution was blocked by a pre-flight safety invariant before any broker call.</summary>
    RejectedPreFlight,

    /// <summary>Broker reported a duplicate <c>client_order_id</c> (HTTP 409 or duplicate 422).</summary>
    DuplicateOrder,

    /// <summary>Broker call failed closed (HTTP 429, 5xx, 4xx, timeout, network error, or malformed JSON).</summary>
    FailedBroker
}

/// <summary>Specific deterministic reason code for pre-flight rejections or broker failures.</summary>
public enum ExecutionRejectionCode
{
    /// <summary>No failure.</summary>
    None,

    /// <summary><c>TradingEnabled</c> is false.</summary>
    TradingDisabled,

    /// <summary><c>PaperMode</c> is false.</summary>
    PaperModeRequired,

    /// <summary>Base URL is not strictly <c>https://paper-api.alpaca.markets</c>.</summary>
    InvalidBaseUrl,

    /// <summary>Alpaca Paper API credentials are missing or placeholder values.</summary>
    CredentialsMissing,

    /// <summary>Persisted kill-switch or effective <see cref="TradingState"/> is not <see cref="TradingState.Active"/>.</summary>
    KillSwitchActive,

    /// <summary>Persisted <see cref="TradingState"/> could not be read or parsed (fail-closed).</summary>
    TradingStateUnreadable,

    /// <summary><see cref="RiskDecision"/> was not approved or proposal action is non-actionable.</summary>
    RiskNotApproved,

    /// <summary>Shorting is disabled (Long-Only enforcement).</summary>
    ShortingNotAllowed,

    /// <summary>Proposal ticker is missing, oversized, or invalid.</summary>
    InvalidTicker,

    /// <summary>Order quantity must be a positive whole integer (<c>&gt;= 1</c>).</summary>
    InvalidQuantity,

    /// <summary>Entry or limit price must be positive.</summary>
    InvalidEntryPrice,

    /// <summary>Mandatory long bracket invariants violated (<c>0 &lt; StopLoss &lt; Entry &lt; TakeProfit</c>).</summary>
    InvalidBracketBounds,

    /// <summary>Deterministic <c>client_order_id</c> is invalid or exceeds 48 characters.</summary>
    InvalidClientOrderId,

    /// <summary>Broker returned HTTP 409 or duplicate <c>client_order_id</c>.</summary>
    BrokerDuplicateOrder,

    /// <summary>Broker returned HTTP 429 rate limit.</summary>
    BrokerRateLimited,

    /// <summary>Broker rejected the order (4xx non-duplicate).</summary>
    BrokerRejected,

    /// <summary>Broker returned a 5xx server error.</summary>
    BrokerServerError,

    /// <summary>Broker request timed out.</summary>
    BrokerTimeout,

    /// <summary>Broker network or stream failure.</summary>
    BrokerNetworkError,

    /// <summary>Broker response was empty, null, oversized, malformed JSON, or missing required fields.</summary>
    MalformedBrokerResponse
}

/// <summary>Result of reading the persisted kill-switch / trading state.</summary>
public sealed record TradingStateReadResult(bool Success, TradingState State, string? ErrorReason = null);

/// <summary>Reads the persisted <see cref="TradingState"/> (e.g. PostgreSQL <c>system_state</c>).</summary>
public interface ITradingStateProvider
{
    /// <summary>Fetches the current persisted trading state. Implementations must not cache stale active states across halts.</summary>
    Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken);
}

/// <summary>In-memory <see cref="ITradingStateProvider"/> for tests or explicit wiring.</summary>
public sealed class StaticTradingStateProvider(TradingStateReadResult result) : ITradingStateProvider
{
    /// <summary>Initializes a readable state provider returning <paramref name="state"/>.</summary>
    public StaticTradingStateProvider(TradingState state)
        : this(new TradingStateReadResult(true, state))
    {
    }

    /// <inheritdoc />
    public Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}

/// <summary>Parses persisted <c>system_state.trading_state</c> strings into <see cref="TradingState"/> fail-closed.</summary>
public static class SystemStateParser
{
    /// <summary>Parses a persisted state string; unknown or empty values fail closed with <c>Success = false</c>.</summary>
    public static TradingStateReadResult Parse(string? rawState)
    {
        if (string.IsNullOrWhiteSpace(rawState))
        {
            return new TradingStateReadResult(false, TradingState.HaltedSystemError, "Persisted trading_state is empty.");
        }

        return rawState.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => new TradingStateReadResult(true, TradingState.Active),
            "HALTED_DAILY_LOSS" => new TradingStateReadResult(true, TradingState.HaltedDailyLoss, "Daily loss halt active."),
            "HALTED_MANUAL" => new TradingStateReadResult(true, TradingState.HaltedManual, "Manual kill-switch active."),
            "HALTED_SYSTEM_ERROR" => new TradingStateReadResult(true, TradingState.HaltedSystemError, "System error halt active."),
            _ => new TradingStateReadResult(false, TradingState.HaltedSystemError, $"Unknown persisted trading_state '{rawState.Trim()}'.")
        };
    }
}

/// <summary>Execution request combining a <see cref="TradeProposal"/>, its <see cref="RiskDecision"/>, and order prices.</summary>
public sealed record PaperOrderExecutionRequest(
    TradeProposal Proposal,
    RiskDecision RiskDecision,
    decimal EntryPrice,
    decimal? LimitPrice = null,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null,
    ExecutionOrderType OrderType = ExecutionOrderType.Limit,
    ExecutionTimeInForce TimeInForce = ExecutionTimeInForce.Day,
    bool IsClosingLongPosition = false,
    int OpenLongQuantity = 0)
{
    /// <summary>
    /// Builds a Long bracket <see cref="PaperOrderExecutionRequest"/> using the clamped SL/TP percentages from
    /// <paramref name="riskDecision"/> and <paramref name="entryPrice"/>.
    /// </summary>
    public static PaperOrderExecutionRequest FromRiskDecision(
        TradeProposal proposal,
        RiskDecision riskDecision,
        decimal entryPrice,
        ExecutionOrderType orderType = ExecutionOrderType.Limit,
        ExecutionTimeInForce timeInForce = ExecutionTimeInForce.Day)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(riskDecision);

        decimal? stopLossPrice = null;
        decimal? takeProfitPrice = null;

        if (entryPrice > 0m &&
            riskDecision.ClampedStopLossPct is { } slPct && slPct > 0m &&
            riskDecision.ClampedTakeProfitPct is { } tpPct && tpPct > 0m)
        {
            var decimals = entryPrice >= 1m ? 2 : 4;
            stopLossPrice = Math.Round(entryPrice * (1m - slPct), decimals, MidpointRounding.AwayFromZero);
            takeProfitPrice = Math.Round(entryPrice * (1m + tpPct), decimals, MidpointRounding.AwayFromZero);
        }

        var limitPrice = orderType == ExecutionOrderType.Limit && entryPrice > 0m ? entryPrice : (decimal?)null;

        return new PaperOrderExecutionRequest(
            proposal,
            riskDecision,
            entryPrice,
            limitPrice,
            stopLossPrice,
            takeProfitPrice,
            orderType,
            timeInForce);
    }
}

/// <summary>Validated wire payload submitted to <see cref="IAlpacaPaperBrokerClient"/>.</summary>
public sealed record AlpacaBracketOrderRequest(
    string ClientOrderId,
    string Symbol,
    int Quantity,
    TradeAction Side,
    ExecutionOrderType OrderType,
    ExecutionTimeInForce TimeInForce,
    ExecutionOrderClass OrderClass,
    decimal? LimitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice)
{
    /// <summary>Formats prices using <see cref="CultureInfo.InvariantCulture"/> for Alpaca REST API JSON.</summary>
    public static string FormatPrice(decimal price)
    {
        var rounded = price >= 1m
            ? Math.Round(price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(price, 4, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.00##", CultureInfo.InvariantCulture);
    }
}

/// <summary>Parsed receipt from an accepted Alpaca Paper order (<c>POST /v2/orders</c>).</summary>
public sealed record AlpacaOrderReceipt(
    string OrderId,
    string ClientOrderId,
    string Symbol,
    string Status,
    string Side,
    string OrderType,
    string? OrderClass,
    string? Qty,
    decimal? LimitPrice,
    decimal? FilledAvgPrice,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? CreatedAtUtc);

/// <summary>Non-throwing structured outcome returned by <see cref="IAlpacaPaperBrokerClient"/>.</summary>
public sealed record BrokerSubmissionResult(
    bool IsSuccess,
    bool IsDuplicate,
    bool IsRetryable,
    System.Net.HttpStatusCode? StatusCode,
    ExecutionRejectionCode FailureCode,
    string? FailureReason,
    AlpacaOrderReceipt? Receipt);

/// <summary>Non-throwing structured outcome returned by <see cref="PaperExecutionEngine"/>.</summary>
public sealed record PaperExecutionResult(
    ExecutionOutcomeStatus Status,
    ExecutionRejectionCode RejectionCode,
    string? Reason,
    string? ClientOrderId,
    TradingState? ObservedTradingState,
    AlpacaOrderReceipt? Receipt,
    BrokerSubmissionResult? BrokerResult,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>True only when the paper order was accepted by Alpaca Paper API.</summary>
    public bool Executed => Status == ExecutionOutcomeStatus.Submitted;
}
