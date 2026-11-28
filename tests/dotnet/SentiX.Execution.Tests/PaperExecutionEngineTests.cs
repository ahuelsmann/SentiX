using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentiX.Risk;

namespace SentiX.Execution.Tests;

public sealed class PaperExecutionEngineTests
{
    [Fact]
    public async Task TradingEnabledFalse_BlocksExecutionWithoutCallingBroker()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.TradingEnabled = false;

        var engine = CreateEngine(options, new StaticTradingStateProvider(TradingState.Active), handler);

        var result = await engine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.TradingDisabled, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("https://api.alpaca.markets")]
    [InlineData("http://paper-api.alpaca.markets")]
    [InlineData("https://paper-api.alpaca.markets@api.alpaca.markets")]
    public async Task NonPaperUrl_BlocksExecutionWithoutCallingBroker(string baseUrl)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.BaseUrl = baseUrl;

        var engine = CreateEngine(options, new StaticTradingStateProvider(TradingState.Active), handler);

        var result = await engine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.InvalidBaseUrl, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(TradingState.HaltedManual)]
    [InlineData(TradingState.HaltedDailyLoss)]
    [InlineData(TradingState.HaltedSystemError)]
    public async Task NonActivePersistedTradingState_FailsClosedWithoutBrokerCall(TradingState state)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(
            ExecutionTestFixtures.ReadyPaperOptions(),
            new StaticTradingStateProvider(state),
            handler);

        var result = await engine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.KillSwitchActive, result.RejectionCode);
        Assert.Equal(state, result.ObservedTradingState);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task UnreadableOrThrowingTradingState_FailsClosedWithoutBrokerCall()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));

        var unreadableEngine = CreateEngine(
            ExecutionTestFixtures.ReadyPaperOptions(),
            new StaticTradingStateProvider(new TradingStateReadResult(false, TradingState.Active, "DB row missing")),
            handler);

        var throwingEngine = CreateEngine(
            ExecutionTestFixtures.ReadyPaperOptions(),
            new ThrowingTradingStateProvider(new InvalidOperationException("Postgres unreachable")),
            handler);

        var unreadableResult = await unreadableEngine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);
        var throwingResult = await throwingEngine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);

        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, unreadableResult.Status);
        Assert.Equal(ExecutionRejectionCode.TradingStateUnreadable, unreadableResult.RejectionCode);

        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, throwingResult.Status);
        Assert.Equal(ExecutionRejectionCode.TradingStateUnreadable, throwingResult.RejectionCode);
        Assert.Equal(TradingState.HaltedSystemError, throwingResult.ObservedTradingState);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("ACTIVE", true, TradingState.Active)]
    [InlineData("HALTED_MANUAL", true, TradingState.HaltedManual)]
    [InlineData("HALTED_DAILY_LOSS", true, TradingState.HaltedDailyLoss)]
    [InlineData("HALTED_SYSTEM_ERROR", true, TradingState.HaltedSystemError)]
    [InlineData("PAUSED", false, TradingState.HaltedSystemError)]
    [InlineData("", false, TradingState.HaltedSystemError)]
    [InlineData(null, false, TradingState.HaltedSystemError)]
    public void SystemStateParser_FailsClosedOnUnknownOrEmpty(string? raw, bool expectedSuccess, TradingState expectedState)
    {
        var parsed = SystemStateParser.Parse(raw);

        Assert.Equal(expectedSuccess, parsed.Success);
        Assert.Equal(expectedState, parsed.State);
    }

    [Fact]
    public async Task UnapprovedRiskDecision_IsRejectedWithoutBrokerCall()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var proposal = ExecutionTestFixtures.BuyProposal();
        var rejectedDecision = new RiskDecision(
            Approved: false,
            EffectiveTradingState: TradingState.Active,
            Violations: [new RiskViolation(RiskViolationCode.ConfidenceTooLow, "Confidence too low")],
            CalculatedQuantity: 10,
            ClampedStopLossPct: 0.03m,
            ClampedTakeProfitPct: 0.06m,
            ClientOrderId: null);

        var request = PaperOrderExecutionRequest.FromRiskDecision(proposal, rejectedDecision, 150.00m);
        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.RiskNotApproved, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task HoldProposal_IsRejectedWithoutBrokerCall()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var proposal = ExecutionTestFixtures.BuyProposal(action: TradeAction.Hold);
        var decision = new RiskDecision(true, TradingState.Active, [], 10, 0.03m, 0.06m, "SX-20261127-AAPL-HOLD-12345678");
        var request = new PaperOrderExecutionRequest(proposal, decision, 150m, 150m, 145.5m, 159m);

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.RiskNotApproved, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task NonPositiveWholeShares_AreRejectedWithoutBrokerCall(int quantity)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var request = ExecutionTestFixtures.ValidBuyRequest(quantity: quantity);
        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.InvalidQuantity, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ShortSellWithoutVerifiedLongPosition_IsRejectedWithoutBrokerCall()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var sellProposal = ExecutionTestFixtures.BuyProposal(action: TradeAction.Sell);
        var decision = ExecutionTestFixtures.ApprovedDecision(sellProposal, quantity: 5);
        var shortAttempt = new PaperOrderExecutionRequest(sellProposal, decision, 150.00m, IsClosingLongPosition: false, OpenLongQuantity: 0);
        var overSellAttempt = new PaperOrderExecutionRequest(sellProposal, decision, 150.00m, IsClosingLongPosition: true, OpenLongQuantity: 4);

        var r1 = await engine.ExecuteAsync(shortAttempt, CancellationToken.None);
        var r2 = await engine.ExecuteAsync(overSellAttempt, CancellationToken.None);

        Assert.Equal(ExecutionRejectionCode.ShortingNotAllowed, r1.RejectionCode);
        Assert.Equal(ExecutionRejectionCode.ShortingNotAllowed, r2.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(150.00, null, 159.00)]
    [InlineData(150.00, 145.50, null)]
    [InlineData(150.00, 0.0, 159.00)]
    [InlineData(150.00, -5.0, 159.00)]
    [InlineData(150.00, 150.00, 159.00)]
    [InlineData(150.00, 151.00, 159.00)]
    [InlineData(150.00, 145.50, 150.00)]
    [InlineData(150.00, 145.50, 149.00)]
    public async Task MissingOrInvalidBracketBounds_AreRejectedWithoutBrokerCall(
        double entry,
        double? stopLoss,
        double? takeProfit)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var proposal = ExecutionTestFixtures.BuyProposal();
        var decision = ExecutionTestFixtures.ApprovedDecision(proposal, quantity: 10);
        var request = new PaperOrderExecutionRequest(
            proposal,
            decision,
            (decimal)entry,
            (decimal)entry,
            stopLoss.HasValue ? (decimal)stopLoss.Value : null,
            takeProfit.HasValue ? (decimal)takeProfit.Value : null);

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.InvalidBracketBounds, result.RejectionCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DeterministicClientOrderId_MatchesRiskGenerator_AndIsIdempotent()
    {
        var sentIds = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var cid = doc.RootElement.GetProperty("client_order_id").GetString()!;
            sentIds.Add(cid);

            return sentIds.Count == 1
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson(cid))
                : StubHttpMessageHandler.Json(HttpStatusCode.Conflict, """{"message":"duplicate client_order_id"}""");
        });

        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);
        var proposal = ExecutionTestFixtures.BuyProposal();
        var approvedDecision = ExecutionTestFixtures.ApprovedDecision(proposal, includeClientOrderId: true);
        var request = PaperOrderExecutionRequest.FromRiskDecision(proposal, approvedDecision, 150.00m);

        var first = await engine.ExecuteAsync(request, CancellationToken.None);
        var second = await engine.ExecuteAsync(request, CancellationToken.None);

        var expectedId = ClientOrderIdGenerator.Create(
            proposal.EventId, proposal.ProposalId, proposal.Ticker, proposal.Action, ExecutionTestFixtures.FixedUtcNow);

        Assert.True(first.Executed);
        Assert.Equal(ExecutionOutcomeStatus.Submitted, first.Status);
        Assert.Equal(expectedId, first.ClientOrderId);
        Assert.True(first.ClientOrderId!.Length <= ClientOrderIdGenerator.MaxLength);

        Assert.False(second.Executed);
        Assert.Equal(ExecutionOutcomeStatus.DuplicateOrder, second.Status);
        Assert.Equal(ExecutionRejectionCode.BrokerDuplicateOrder, second.RejectionCode);
        Assert.Equal(expectedId, second.ClientOrderId);
        Assert.Equal(2, sentIds.Count);
        Assert.Equal(sentIds[0], sentIds[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SX-20261126-AAPL-BUY-00000000")]
    [InlineData("SX-20261127-MSFT-BUY-00000000")]
    [InlineData("SX-20261127-AAPL-SELL-00000000")]
    [InlineData("SX-20261127-AAPL-BUY-deadbeef")]
    [InlineData("arbitrary-noncanonical-id")]
    public async Task NullBlankMismatchedOrStaleRiskDecisionClientOrderId_IsRejectedWithoutBrokerCall(string? suppliedClientOrderId)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Broker should not be called."));
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var proposal = ExecutionTestFixtures.BuyProposal(ticker: "AAPL");
        var decision = new RiskDecision(
            Approved: true,
            EffectiveTradingState: TradingState.Active,
            Violations: [],
            CalculatedQuantity: 10,
            ClampedStopLossPct: 0.03m,
            ClampedTakeProfitPct: 0.06m,
            ClientOrderId: suppliedClientOrderId);

        var request = PaperOrderExecutionRequest.FromRiskDecision(proposal, decision, 150.00m);
        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.InvalidClientOrderId, result.RejectionCode);
        Assert.Null(result.ClientOrderId);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task HardRiskToPaperExecution_EndToEndHappyPath_SubmitsValidPaperBracketOrder()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var cid = doc.RootElement.GetProperty("client_order_id").GetString()!;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, ExecutionTestFixtures.AcceptedOrderJson(cid, "NVDA"));
        });

        var proposal = ExecutionTestFixtures.BuyProposal(ticker: "NVDA");
        var snapshot = new PortfolioSnapshot(
            TradingState.Active,
            Equity: 100_000m,
            BuyingPower: 100_000m,
            DailyPnLPct: 0.005,
            CurrentAskPrice: 125.00m,
            TargetSector: "Technology",
            OpenPositions: [],
            PendingOrders: [],
            LastTradeTimesUtc: new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));
        var policy = new RiskPolicy
        {
            AllowedTickers = new HashSet<string>(["NVDA"], StringComparer.OrdinalIgnoreCase)
        };
        var clock = new MarketClock(ExecutionTestFixtures.FixedUtcNow);

        var riskDecision = HardRiskManager.Evaluate(proposal, snapshot, clock, policy);
        Assert.True(riskDecision.Approved);
        Assert.Equal(40, riskDecision.CalculatedQuantity); // floor(5000 / 125) = 40

        var request = PaperOrderExecutionRequest.FromRiskDecision(proposal, riskDecision, entryPrice: 125.00m);
        var engine = CreateEngine(ExecutionTestFixtures.ReadyPaperOptions(), new StaticTradingStateProvider(TradingState.Active), handler);

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.Submitted, result.Status);
        Assert.Equal(riskDecision.ClientOrderId, result.ClientOrderId);
        Assert.Equal(new Uri("https://paper-api.alpaca.markets/v2/orders"), handler.LastUri);

        using var sentJson = JsonDocument.Parse(handler.LastBody!);
        var root = sentJson.RootElement;
        Assert.Equal("NVDA", root.GetProperty("symbol").GetString());
        Assert.Equal("40", root.GetProperty("qty").GetString());
        Assert.Equal("buy", root.GetProperty("side").GetString());
        Assert.Equal("limit", root.GetProperty("type").GetString());
        Assert.Equal("bracket", root.GetProperty("order_class").GetString());
        Assert.Equal("125.00", root.GetProperty("limit_price").GetString());
        Assert.Equal("132.50", root.GetProperty("take_profit").GetProperty("limit_price").GetString());
        Assert.Equal("121.25", root.GetProperty("stop_loss").GetProperty("stop_price").GetString());
    }

    [Fact]
    public async Task DependencyInjection_DefaultRegistration_IsDisabledAndFailClosed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ALPACA_API_KEY"] = ExecutionTestFixtures.TestApiKey,
                ["ALPACA_SECRET_KEY"] = ExecutionTestFixtures.TestSecretKey
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSentiXPaperExecution(config);
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AlpacaPaperOptions>();
        var stateProvider = provider.GetRequiredService<ITradingStateProvider>();
        var engine = provider.GetRequiredService<IPaperExecutionEngine>();

        Assert.False(options.TradingEnabled);
        var state = await stateProvider.GetCurrentStateAsync(CancellationToken.None);
        Assert.False(state.Success);
        Assert.Equal(TradingState.HaltedSystemError, state.State);

        var result = await engine.ExecuteAsync(ExecutionTestFixtures.ValidBuyRequest(), CancellationToken.None);
        Assert.False(result.Executed);
        Assert.Equal(ExecutionOutcomeStatus.RejectedPreFlight, result.Status);
        Assert.Equal(ExecutionRejectionCode.TradingDisabled, result.RejectionCode);
    }

    private static PaperExecutionEngine CreateEngine(
        AlpacaPaperOptions options,
        ITradingStateProvider stateProvider,
        StubHttpMessageHandler handler)
    {
        var brokerClient = new AlpacaPaperBrokerClient(
            new HttpClient(handler),
            options,
            timeProvider: new FakeTimeProvider(ExecutionTestFixtures.FixedUtcNow));

        return new PaperExecutionEngine(
            options,
            stateProvider,
            brokerClient,
            new FakeTimeProvider(ExecutionTestFixtures.FixedUtcNow));
    }
}
