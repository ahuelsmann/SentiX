namespace SentiX.Execution;

/// <summary>
/// Alpaca Paper REST API client abstraction (Spec 008).
/// Implementations MUST enforce strict <c>https://paper-api.alpaca.markets</c> URL validation
/// before every HTTP dispatch and return structured fail-closed results without throwing on broker errors.
/// </summary>
public interface IAlpacaPaperBrokerClient
{
    /// <summary>Submits a validated paper order to Alpaca <c>POST /v2/orders</c>.</summary>
    Task<BrokerSubmissionResult> SubmitOrderAsync(AlpacaBracketOrderRequest order, CancellationToken cancellationToken);
}
