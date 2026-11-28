using System.Net;
using System.Text;
using SentiX.Risk;

namespace SentiX.Execution.Tests;

internal static class ExecutionTestFixtures
{
    public const string TestApiKey = "PKTESTKEY00000000001";
    public const string TestSecretKey = "SECRET_TEST_VALUE_00000000001";

    public static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 27, 16, 0, 0, TimeSpan.Zero); // 11:00 ET (weekday)

    public static AlpacaPaperOptions ReadyPaperOptions() => new()
    {
        TradingEnabled = true,
        PaperMode = true,
        AllowShorting = false,
        BaseUrl = AlpacaPaperOptions.DefaultPaperBaseUrl,
        ApiKey = TestApiKey,
        SecretKey = TestSecretKey,
        RequestTimeout = TimeSpan.FromSeconds(5)
    };

    public static TradeProposal BuyProposal(string ticker = "AAPL", TradeAction action = TradeAction.Buy) => new(
        ProposalId: "prop_1234567890abcdef",
        EventId: "alpaca:1001:999999",
        Action: action,
        Ticker: ticker,
        InstrumentType: InstrumentType.Equity,
        Confidence: 0.85,
        ExpectedHorizonHours: 48,
        SuggestedStopLossPct: 0.03m,
        SuggestedTakeProfitPct: 0.06m,
        Rationale: "Strong earnings beat.",
        DissentSummary: "Valuation stretch noted.");

    public static RiskDecision ApprovedDecision(
        TradeProposal proposal,
        int quantity = 10,
        TradingState effectiveState = TradingState.Active,
        bool includeClientOrderId = true)
    {
        var clientOrderId = includeClientOrderId
            ? ClientOrderIdGenerator.Create(proposal.EventId, proposal.ProposalId, proposal.Ticker, proposal.Action, FixedUtcNow)
            : null;

        return new RiskDecision(
            Approved: true,
            EffectiveTradingState: effectiveState,
            Violations: [],
            CalculatedQuantity: quantity,
            ClampedStopLossPct: 0.03m,
            ClampedTakeProfitPct: 0.06m,
            ClientOrderId: clientOrderId);
    }

    public static PaperOrderExecutionRequest ValidBuyRequest(decimal entryPrice = 150.00m, int quantity = 10)
    {
        var proposal = BuyProposal();
        var decision = ApprovedDecision(proposal, quantity);
        return PaperOrderExecutionRequest.FromRiskDecision(proposal, decision, entryPrice);
    }

    public static string AcceptedOrderJson(string clientOrderId, string symbol = "AAPL", string side = "buy") =>
        $$$"""
        {
          "id": "ord_paper_001",
          "client_order_id": "{{{clientOrderId}}}",
          "symbol": "{{{symbol}}}",
          "qty": "10",
          "side": "{{{side}}}",
          "type": "limit",
          "order_class": "bracket",
          "limit_price": "150.00",
          "filled_avg_price": null,
          "status": "accepted",
          "submitted_at": "2026-11-27T16:00:01Z",
          "created_at": "2026-11-27T16:00:00Z"
        }
        """;
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((req, _) => Task.FromResult(responder(req)))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public int CallCount { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public Uri? LastUri { get; private set; }
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastBody { get; private set; }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod = request.Method;
        LastUri = request.RequestUri;
        LastHeaders.Clear();
        foreach (var header in request.Headers)
        {
            LastHeaders[header.Key] = string.Join(",", header.Value);
        }

        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class ThrowingTradingStateProvider(Exception exception) : ITradingStateProvider
{
    public Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken) =>
        Task.FromException<TradingStateReadResult>(exception);
}
