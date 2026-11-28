using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentiX.DecisionSwarm;
using SentiX.Domain;
using SentiX.Execution;
using SentiX.Infrastructure;
using SentiX.Ingestion;
using SentiX.NativeBridge;
using RiskTradingState = SentiX.Risk.TradingState;

namespace SentiX.Integration.Tests;

public sealed class Phase4IntegrationTests
{
    private static readonly DateTimeOffset FixedRegularMarketUtc = new(2026, 6, 4, 15, 0, 0, TimeSpan.Zero); // 11:00 AM ET

    [Fact]
    public async Task HostEndpoints_DefaultShadowMode_ExposesOrdersAndBlocksUnconfiguredKillSwitch()
    {
        using var factory = CreateHostFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "",
            ["Sentix:ConfigPath"] = UniverseAndXTests.UniversePath,
            ["Sentix:NativeSocketPath"] = Path.Combine(Path.GetTempPath(), $"sx-{Guid.NewGuid():N}.sock"),
            ["Alpaca:ApiKey"] = "PK_YOUR_PAPER_KEY",
            ["Alpaca:SecretKey"] = "YOUR_PAPER_SECRET",
            ["ALPACA_API_KEY"] = "PK_YOUR_PAPER_KEY",
            ["ALPACA_SECRET_KEY"] = "YOUR_PAPER_SECRET",
            ["ALPACA_TRADING_ENABLED"] = "false",
            ["TRADING_ENABLED"] = "false",
            ["Finnhub:ApiKey"] = "placeholder",
            ["Sec:UserAgent"] = "disabled"
        });
        using var client = factory.CreateClient();

        using var ordersRes = await client.GetAsync("/api/orders?limit=25");
        Assert.Equal(HttpStatusCode.OK, ordersRes.StatusCode);
        Assert.True(ordersRes.Headers.CacheControl?.NoStore);
        using var ordersJson = JsonDocument.Parse(await ordersRes.Content.ReadAsStringAsync());
        Assert.False(ordersJson.RootElement.GetProperty("databaseConfigured").GetBoolean());
        Assert.Empty(ordersJson.RootElement.GetProperty("items").EnumerateArray());

        // Missing confirmation header -> 400 Bad Request
        using var missingHeaderRes = await client.PostAsync("/api/system-state/halt", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingHeaderRes.StatusCode);
        using var missingHeaderJson = JsonDocument.Parse(await missingHeaderRes.Content.ReadAsStringAsync());
        Assert.Equal("missing_confirmation_header", missingHeaderJson.RootElement.GetProperty("error").GetString());

        // Valid confirmation header when DB is unconfigured -> 503 Service Unavailable
        using var haltReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/halt");
        haltReq.Headers.Add("X-SentiX-Confirm", "HALT");
        using var unconfiguredRes = await client.SendAsync(haltReq);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfiguredRes.StatusCode);
        using var unconfiguredJson = JsonDocument.Parse(await unconfiguredRes.Content.ReadAsStringAsync());
        Assert.Equal("database_not_configured", unconfiguredJson.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task KillSwitchEndpoints_EnforceOriginCsrfConfirmationAndStateTransitions()
    {
        var stateStore = new InMemoryTradingStateStore(RiskTradingState.Active, FixedRegularMarketUtc);
        using var factory = CreateHostFactory(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "",
                ["ALPACA_TRADING_ENABLED"] = "true",
                ["ALPACA_API_KEY"] = "PK_LIVE_LIKE_VALID_PAPER_KEY",
                ["ALPACA_SECRET_KEY"] = "SK_VALID_PAPER_SECRET_VALUE",
                ["Sentix:ConfigPath"] = UniverseAndXTests.UniversePath,
                ["Sentix:NativeSocketPath"] = Path.Combine(Path.GetTempPath(), $"sx-{Guid.NewGuid():N}.sock"),
                ["Sec:UserAgent"] = "disabled"
            },
            services =>
            {
                services.RemoveAll<ITradingStateStore>();
                services.RemoveAll<ITradingStateProvider>();
                services.AddSingleton<ITradingStateStore>(stateStore);
                services.AddSingleton<ITradingStateProvider>(stateStore);
            });

        using var client = factory.CreateClient();

        // Armed + ACTIVE => orderExecutionEnabled = true
        using (var healthRes = await client.GetAsync("/health"))
        {
            using var doc = JsonDocument.Parse(await healthRes.Content.ReadAsStringAsync());
            Assert.Equal("ACTIVE", doc.RootElement.GetProperty("tradingState").GetString());
            Assert.True(doc.RootElement.GetProperty("paperSwitchArmed").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("paperExecutionConfigured").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("tradingEnabled").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("orderExecutionEnabled").GetBoolean());
        }

        // Non-loopback Origin is rejected with 403 Forbidden (even with valid confirmation header)
        foreach (var badOrigin in new[] { "https://evil.example.com", "http://localhost.evil.com", "null" })
        {
            using var badOriginReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/halt");
            badOriginReq.Headers.TryAddWithoutValidation("Origin", badOrigin);
            badOriginReq.Headers.Add("X-SentiX-Confirm", "HALT");
            using var badOriginRes = await client.SendAsync(badOriginReq);
            Assert.Equal(HttpStatusCode.Forbidden, badOriginRes.StatusCode);
            using var errDoc = JsonDocument.Parse(await badOriginRes.Content.ReadAsStringAsync());
            Assert.Equal("forbidden_origin", errDoc.RootElement.GetProperty("error").GetString());
        }

        // Wrong or lowercase confirmation header -> 400 Bad Request
        foreach (var wrongConfirm in new[] { "halt", "RESUME", "" })
        {
            using var wrongConfirmReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/halt");
            wrongConfirmReq.Headers.Add("Origin", "http://127.0.0.1:8080");
            if (wrongConfirm.Length > 0)
            {
                wrongConfirmReq.Headers.Add("X-SentiX-Confirm", wrongConfirm);
            }
            using var wrongConfirmRes = await client.SendAsync(wrongConfirmReq);
            Assert.Equal(HttpStatusCode.BadRequest, wrongConfirmRes.StatusCode);
        }

        // Valid loopback Origin + X-SentiX-Confirm: HALT -> transitions to HALTED_MANUAL and disarms effective execution
        using (var haltReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/halt"))
        {
            haltReq.Headers.Add("Origin", "http://localhost:8080");
            haltReq.Headers.Add("X-SentiX-Confirm", "HALT");
            using var haltRes = await client.SendAsync(haltReq);
            Assert.Equal(HttpStatusCode.OK, haltRes.StatusCode);
            Assert.True(haltRes.Headers.CacheControl?.NoStore);
            using var doc = JsonDocument.Parse(await haltRes.Content.ReadAsStringAsync());
            Assert.Equal("HALTED_MANUAL", doc.RootElement.GetProperty("tradingState").GetString());
            Assert.Equal("OPERATOR_UI", doc.RootElement.GetProperty("updatedBy").GetString());
        }

        using (var overviewAfterHalt = await client.GetAsync("/api/overview"))
        {
            using var doc = JsonDocument.Parse(await overviewAfterHalt.Content.ReadAsStringAsync());
            Assert.Equal("HALTED_MANUAL", doc.RootElement.GetProperty("tradingState").GetString());
            Assert.True(doc.RootElement.GetProperty("paperSwitchArmed").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("tradingEnabled").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("orderExecutionEnabled").GetBoolean());
        }

        // Valid manual resume from HALTED_MANUAL -> 200 OK ACTIVE
        using (var resumeReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/resume"))
        {
            resumeReq.Headers.Add("Origin", "http://127.0.0.1:8080");
            resumeReq.Headers.Add("X-SentiX-Confirm", "RESUME");
            using var resumeRes = await client.SendAsync(resumeReq);
            Assert.Equal(HttpStatusCode.OK, resumeRes.StatusCode);
            using var doc = JsonDocument.Parse(await resumeRes.Content.ReadAsStringAsync());
            Assert.Equal("ACTIVE", doc.RootElement.GetProperty("tradingState").GetString());
        }

        // Circuit breaker states (HALTED_DAILY_LOSS and HALTED_SYSTEM_ERROR) MUST NEVER be downgraded by manual /halt
        // and MUST still block manual UI /resume with 409 Conflict!
        foreach (var nonManualHalt in new[] { RiskTradingState.HaltedDailyLoss, RiskTradingState.HaltedSystemError })
        {
            stateStore.SetState(nonManualHalt);
            var expectedPersisted = PersistedTradingStateFormat.ToDatabaseValue(nonManualHalt);

            using var haltAttemptReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/halt");
            haltAttemptReq.Headers.Add("Origin", "http://localhost:8080");
            haltAttemptReq.Headers.Add("X-SentiX-Confirm", "HALT");
            using var haltAttemptRes = await client.SendAsync(haltAttemptReq);
            Assert.Equal(HttpStatusCode.OK, haltAttemptRes.StatusCode);
            using (var haltDoc = JsonDocument.Parse(await haltAttemptRes.Content.ReadAsStringAsync()))
            {
                Assert.Equal(expectedPersisted, haltDoc.RootElement.GetProperty("tradingState").GetString());
            }

            using var resumeBlockedReq = new HttpRequestMessage(HttpMethod.Post, "/api/system-state/resume");
            resumeBlockedReq.Headers.Add("X-SentiX-Confirm", "RESUME");
            using var resumeBlockedRes = await client.SendAsync(resumeBlockedReq);
            Assert.Equal(HttpStatusCode.Conflict, resumeBlockedRes.StatusCode);
            using var doc = JsonDocument.Parse(await resumeBlockedRes.Content.ReadAsStringAsync());
            Assert.Equal("cannot_resume_state", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("CannotResumeNonManualHalt", doc.RootElement.GetProperty("code").GetString());
            Assert.Equal(expectedPersisted, doc.RootElement.GetProperty("tradingState").GetString());
        }
    }

    [Fact]
    public async Task ArmedMode_AuthoritativePortfolioSnapshot_BlocksOnUnavailableDuplicateCooldownAndSectorExposure()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var quotes = new InMemoryMarketQuoteCache();
        quotes.Update(new MarketQuoteSnapshot("AAPL", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));

        var proposal = new TradeProposal(
            "prop_arm_1",
            "evt_arm_1",
            TradeAction.Buy,
            "AAPL",
            InstrumentType.Equity,
            0.90,
            24,
            0.03m,
            0.06m,
            "Catalyst",
            "None");

        // (a) Shadow Mode uses synthetic $100k portfolio with ZERO calls to IPaperPortfolioSnapshotProvider
        var throwingPortfolioProvider = new StubPaperPortfolioProvider(_ => throw new InvalidOperationException("Must not call in Shadow Mode."));
        var shadowAuditStore = new InMemoryOrderAuditStore();
        using (var shadowPipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            new NativeBridgeClient(Path.Combine(Path.GetTempPath(), $"sx-sh-{Guid.NewGuid():N}.sock")),
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: false,
            quoteCache: quotes,
            universe: universe,
            timeProvider: new FixedClock(FixedRegularMarketUtc),
            paperOptions: new AlpacaPaperOptions { TradingEnabled = false },
            paperPortfolioProvider: throwingPortfolioProvider,
            orderAuditStore: shadowAuditStore))
        {
            var shadowDecision = MarketEventPipeline.EvaluateShadowHardRisk(
                proposal, "AAPL", quotes, universe, RiskTradingState.Active, FixedRegularMarketUtc);
            Assert.True(shadowDecision.Approved);
            Assert.Equal(0, throwingPortfolioProvider.Calls);
        }

        // (b) Armed Mode fails closed (SnapshotAvailable == false, Decision == null) when portfolio fetch fails — NEVER falls back to $100k!
        var failingPortfolioProvider = new StubPaperPortfolioProvider(_ =>
            PaperAccountSnapshotResult.Failure(ExecutionRejectionCode.BrokerTimeout, "Alpaca /v2/account timed out."));
        using (var failingPipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            new NativeBridgeClient(Path.Combine(Path.GetTempPath(), $"sx-f-{Guid.NewGuid():N}.sock")),
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: true,
            quoteCache: quotes,
            universe: universe,
            timeProvider: new FixedClock(FixedRegularMarketUtc),
            paperOptions: new AlpacaPaperOptions { TradingEnabled = true },
            paperPortfolioProvider: failingPortfolioProvider,
            orderAuditStore: shadowAuditStore))
        {
            var unavailableEval = await failingPipeline.EvaluateArmedPaperRiskAsync(
                proposal, "AAPL", RiskTradingState.Active, FixedRegularMarketUtc, CancellationToken.None);
            Assert.False(unavailableEval.SnapshotAvailable);
            Assert.Null(unavailableEval.Decision);
            Assert.Equal(1, failingPortfolioProvider.Calls);
        }

        // (c1) Armed Mode blocks duplicate position when Alpaca /v2/positions already holds AAPL
        var openPositionSnapshot = new PaperAccountSnapshot(
            Equity: 100_000m,
            LastEquity: 100_000m,
            BuyingPower: 90_000m,
            DailyPnLPct: 0.0,
            OpenPositions: [new SentiX.Risk.OpenPosition("AAPL", "Technology", 10, 2_000m, 200m)],
            PendingOrders: [],
            FetchedAtUtc: FixedRegularMarketUtc);
        var (_, _, dupPosDecision, _) = MarketEventPipeline.EvaluateArmedHardRiskFromSnapshots(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, openPositionSnapshot, [], FixedRegularMarketUtc);
        Assert.False(dupPosDecision.Approved);
        Assert.Contains(dupPosDecision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.DuplicatePosition);

        // (c2) Armed Mode blocks duplicate when local orders has an in-flight PENDING_SUBMIT intent
        var cleanAccount = openPositionSnapshot with { OpenPositions = [] };
        var inFlightPendingRow = new OrderRow(
            "SX-20260604-AAPL-BUY-prior001", null, "prop_prior", "AAPL", "BUY", "LIMIT",
            25, 200m, 194m, 212m, "PENDING_SUBMIT", null,
            FixedRegularMarketUtc.AddMinutes(-2), FixedRegularMarketUtc.AddMinutes(-2));
        var (_, _, pendingDupDecision, _) = MarketEventPipeline.EvaluateArmedHardRiskFromSnapshots(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, cleanAccount, [inFlightPendingRow], FixedRegularMarketUtc);
        Assert.False(pendingDupDecision.Approved);
        Assert.Contains(pendingDupDecision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.DuplicatePosition);
        Assert.Contains(pendingDupDecision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.TickerCooldownActive);

        // (c3) Armed Mode blocks sector over-exposure (> 25% of $100k equity in TECH sector)
        var heavyTechSnapshot = cleanAccount with
        {
            OpenPositions = [new SentiX.Risk.OpenPosition("MSFT", AlpacaPaperPortfolioSnapshotProvider.ResolveSector("MSFT", universe), 56, 22_500m, 401.79m)] // 22.5% + 5.0% = 27.5% > 25%
        };
        var (_, _, sectorBlockedDecision, _) = MarketEventPipeline.EvaluateArmedHardRiskFromSnapshots(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, heavyTechSnapshot, [], FixedRegularMarketUtc);
        Assert.False(sectorBlockedDecision.Approved);
        Assert.Contains(sectorBlockedDecision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.SectorExposureExceeded);

        // (c4) Armed Mode blocks when authoritative paper account breached fractional daily loss limit (-2.5% <= -2.0%)
        var dailyLossSnapshot = cleanAccount with { DailyPnLPct = -0.025 };
        var (_, _, dailyLossDecision, _) = MarketEventPipeline.EvaluateArmedHardRiskFromSnapshots(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, dailyLossSnapshot, [], FixedRegularMarketUtc);
        Assert.False(dailyLossDecision.Approved);
        Assert.Equal(RiskTradingState.HaltedDailyLoss, dailyLossDecision.EffectiveTradingState);
        Assert.Contains(dailyLossDecision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.DailyLossLimitReached);
    }

    [Fact]
    public async Task WriteAheadOrderIntent_BlocksBrokerOnInsertFailure_AndPreservesPendingSubmitWhenPostUpdateFails()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var quotes = new InMemoryMarketQuoteCache();
        quotes.Update(new MarketQuoteSnapshot("AAPL", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));
        var proposal = new TradeProposal(
            "prop_wal_1", "evt_wal_1", TradeAction.Buy, "AAPL", InstrumentType.Equity,
            0.90, 24, 0.03m, 0.06m, "Catalyst", "None");
        var (riskProposal, entryPrice, riskDecision) = MarketEventPipeline.EvaluateHardRiskWithInputs(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, FixedRegularMarketUtc);
        Assert.True(riskDecision.Approved);

        var engineCalls = 0;
        var fakeEngine = new StubPaperExecutionEngine(req =>
        {
            engineCalls++;
            return new PaperExecutionResult(
                ExecutionOutcomeStatus.Submitted,
                ExecutionRejectionCode.None,
                null,
                req.RiskDecision.ClientOrderId,
                RiskTradingState.Active,
                new AlpacaOrderReceipt(
                    "alp-wal-1", req.RiskDecision.ClientOrderId!, "AAPL", "accepted", "buy", "limit",
                    "bracket", "25", 200m, null, FixedRegularMarketUtc, FixedRegularMarketUtc),
                null,
                FixedRegularMarketUtc);
        });

        // (a) Write-ahead PENDING_SUBMIT insert fails -> ZERO broker calls!
        var failBeginStore = new InMemoryOrderAuditStore { FailBegin = true };
        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            failBeginStore, fakeEngine, new FixedClock(FixedRegularMarketUtc), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(0, engineCalls);
        Assert.Empty(failBeginStore.Rows);

        // (b) Broker accepts order + post-submit DB update fails -> durable PENDING_SUBMIT row remains visible!
        var failCompleteStore = new InMemoryOrderAuditStore { FailComplete = true };
        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            failCompleteStore, fakeEngine, new FixedClock(FixedRegularMarketUtc), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, engineCalls);
        var durablePending = Assert.Single(failCompleteStore.Rows.Values);
        Assert.Equal(riskDecision.ClientOrderId, durablePending.ClientOrderId);
        Assert.Equal("PENDING_SUBMIT", durablePending.Status);

        // (c) Interrupted PENDING_SUBMIT reconciles on retry and transitions to ACCEPTED, then subsequent replay skips with 0 extra calls!
        failCompleteStore.FailComplete = false;
        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            failCompleteStore, fakeEngine, new FixedClock(FixedRegularMarketUtc.AddSeconds(5)), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, engineCalls);
        Assert.Equal("ACCEPTED", Assert.Single(failCompleteStore.Rows.Values).Status);

        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            failCompleteStore, fakeEngine, new FixedClock(FixedRegularMarketUtc.AddSeconds(10)), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, engineCalls); // Skipped because already ACCEPTED!
    }

    [Fact]
    public async Task EfCoreTradingStateStore_FailsClosedWhenDatabaseThrows_AndSanitizesActor()
    {
        var store = new EfCoreTradingStateStore(
            new ThrowingContextFactory(),
            new FixedClock(FixedRegularMarketUtc),
            NullLogger<EfCoreTradingStateStore>.Instance);

        var read = await store.ReadTradingStateAsync(CancellationToken.None);
        Assert.False(read.Success);
        Assert.Equal(RiskTradingState.HaltedSystemError, read.State);

        var halt = await store.HaltManualAsync("   operator\r\n" + new string('X', 80), CancellationToken.None);
        Assert.Equal(TradingStateWriteStatus.Unavailable, halt.Status);
        Assert.True(halt.UpdatedBy.Length <= PersistedTradingStateFormat.MaxUpdatedByLength);
        Assert.DoesNotContain('\r', halt.UpdatedBy);
        Assert.DoesNotContain('\n', halt.UpdatedBy);

        var resume = await store.ResumeManualAsync("   ", CancellationToken.None);
        Assert.Equal(TradingStateWriteStatus.Unavailable, resume.Status);
        Assert.Equal(PersistedTradingStateFormat.DefaultActor, resume.UpdatedBy);
    }

    [Fact]
    public async Task DatabaseStartupInitializer_CallsEnsureReadyWhenConfigured_AndSwallowsTransientStartupErrors()
    {
        var unconfiguredPipeline = new SpyPipeline(isConfigured: false, shouldThrow: true);
        var skipInit = new SentiX.Host.DatabaseStartupInitializer(
            unconfiguredPipeline,
            NullLogger<SentiX.Host.DatabaseStartupInitializer>.Instance);
        await skipInit.StartAsync(CancellationToken.None);
        Assert.Equal(0, unconfiguredPipeline.EnsureReadyCalls);

        var healthyPipeline = new SpyPipeline(isConfigured: true, shouldThrow: false);
        var okInit = new SentiX.Host.DatabaseStartupInitializer(
            healthyPipeline,
            NullLogger<SentiX.Host.DatabaseStartupInitializer>.Instance);
        await okInit.StartAsync(CancellationToken.None);
        Assert.Equal(1, healthyPipeline.EnsureReadyCalls);

        var throwingPipeline = new SpyPipeline(isConfigured: true, shouldThrow: true);
        var resilientInit = new SentiX.Host.DatabaseStartupInitializer(
            throwingPipeline,
            NullLogger<SentiX.Host.DatabaseStartupInitializer>.Instance);
        await resilientInit.StartAsync(CancellationToken.None);
        Assert.Equal(1, throwingPipeline.EnsureReadyCalls);
    }

    [Fact]
    public void MapOrderRow_MapsSubmittedDuplicateRejectedAndFailedOutcomesDeterministically()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var quotes = new InMemoryMarketQuoteCache();
        quotes.Update(new MarketQuoteSnapshot("NVDA", 124.90m, 125.00m, 124.95m, 8.0, FixedRegularMarketUtc));

        var domainProposal = new TradeProposal(
            "prop_401",
            "evt_401",
            TradeAction.Buy,
            "nvda",
            InstrumentType.Equity,
            0.88,
            24,
            0.03m,
            0.06m,
            "Strong guidance beat",
            "Valuation elevated");

        var (riskProposal, entryPrice, riskDecision) = MarketEventPipeline.EvaluateHardRiskWithInputs(
            domainProposal, "NVDA", quotes, universe, RiskTradingState.Active, FixedRegularMarketUtc);
        Assert.True(riskDecision.Approved);
        Assert.Equal(40, riskDecision.CalculatedQuantity);

        var execRequest = PaperOrderExecutionRequest.FromRiskDecision(riskProposal, riskDecision, entryPrice);
        var brokerSubmittedAt = FixedRegularMarketUtc.AddSeconds(1);
        var receipt = new AlpacaOrderReceipt(
            "alpaca-guid-1",
            riskDecision.ClientOrderId!,
            "NVDA",
            "accepted",
            "buy",
            "limit",
            "bracket",
            "40",
            125.00m,
            null,
            brokerSubmittedAt,
            brokerSubmittedAt);

        var submittedResult = new PaperExecutionResult(
            ExecutionOutcomeStatus.Submitted,
            ExecutionRejectionCode.None,
            null,
            riskDecision.ClientOrderId,
            RiskTradingState.Active,
            receipt,
            null,
            FixedRegularMarketUtc);

        var submittedRow = MarketEventPipeline.MapOrderRow(
            domainProposal.ProposalId, "NVDA", domainProposal.Action, execRequest, submittedResult, FixedRegularMarketUtc.AddSeconds(2));
        Assert.Equal(riskDecision.ClientOrderId, submittedRow.ClientOrderId);
        Assert.Equal("alpaca-guid-1", submittedRow.AlpacaOrderId);
        Assert.Equal("NVDA", submittedRow.Ticker);
        Assert.Equal("BUY", submittedRow.Side);
        Assert.Equal("LIMIT", submittedRow.OrderType);
        Assert.Equal(40, submittedRow.Qty);
        Assert.Equal(125.00m, submittedRow.LimitPrice);
        Assert.Equal(121.25m, submittedRow.StopLossPrice);
        Assert.Equal(132.50m, submittedRow.TakeProfitPrice);
        Assert.Equal("ACCEPTED", submittedRow.Status);
        Assert.Equal(brokerSubmittedAt, submittedRow.SubmittedAtUtc);

        var duplicateRow = MarketEventPipeline.MapOrderRow(
            domainProposal.ProposalId,
            "NVDA",
            domainProposal.Action,
            execRequest,
            submittedResult with { Status = ExecutionOutcomeStatus.DuplicateOrder, RejectionCode = ExecutionRejectionCode.BrokerDuplicateOrder, Receipt = null },
            FixedRegularMarketUtc);
        Assert.Equal("DUPLICATE", duplicateRow.Status);
        Assert.Null(duplicateRow.AlpacaOrderId);

        var preflightRow = MarketEventPipeline.MapOrderRow(
            domainProposal.ProposalId,
            "NVDA",
            domainProposal.Action,
            execRequest,
            submittedResult with { Status = ExecutionOutcomeStatus.RejectedPreFlight, RejectionCode = ExecutionRejectionCode.KillSwitchActive, ClientOrderId = null, Receipt = null },
            FixedRegularMarketUtc);
        Assert.Equal("REJECTED_PREFLIGHT", preflightRow.Status);
        Assert.Equal(riskDecision.ClientOrderId, preflightRow.ClientOrderId);

        var brokerFailedRow = MarketEventPipeline.MapOrderRow(
            domainProposal.ProposalId,
            "NVDA",
            domainProposal.Action,
            execRequest,
            submittedResult with { Status = ExecutionOutcomeStatus.FailedBroker, RejectionCode = ExecutionRejectionCode.BrokerServerError, Receipt = null },
            FixedRegularMarketUtc);
        Assert.Equal("FAILED_BROKER", brokerFailedRow.Status);
    }

    [LocalPostgresFact]
    public async Task PostgresEndToEnd_StartupMigration_PersistedKillSwitch_AndPaperOrderAuditTrail()
    {
        var dbName = $"sentix_phase4_{Guid.NewGuid():N}";
        var socketOrHost = Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.SocketVariable);
        var tcpHost = Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.HostVariable);
        var adminConnection = !string.IsNullOrWhiteSpace(socketOrHost)
            ? new NpgsqlConnectionStringBuilder
            {
                Host = socketOrHost,
                Username = "postgres",
                Database = "postgres",
                Pooling = false
            }
            : new NpgsqlConnectionStringBuilder
            {
                Host = tcpHost,
                Port = int.TryParse(Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.PortVariable), out var port) ? port : 5432,
                Username = Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.UserVariable) ?? "postgres",
                Password = Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.PasswordVariable) ?? "postgres",
                Database = "postgres",
                Pooling = false
            };

        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var appConnection = new NpgsqlConnectionStringBuilder(adminConnection.ConnectionString)
            {
                Database = dbName
            }.ConnectionString;

            var brokerCalls = 0;
            var stubBroker = new RecordingBrokerClient(order =>
            {
                Interlocked.Increment(ref brokerCalls);
                return new BrokerSubmissionResult(
                    IsSuccess: true,
                    IsDuplicate: false,
                    IsRetryable: false,
                    StatusCode: HttpStatusCode.OK,
                    FailureCode: ExecutionRejectionCode.None,
                    FailureReason: null,
                    Receipt: new AlpacaOrderReceipt(
                        $"alp-{brokerCalls}",
                        order.ClientOrderId,
                        order.Symbol,
                        "accepted",
                        "buy",
                        "limit",
                        "bracket",
                        order.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        order.LimitPrice,
                        null,
                        FixedRegularMarketUtc,
                        FixedRegularMarketUtc));
            });

            var stubPortfolioProvider = new StubPaperPortfolioProvider(_ => PaperAccountSnapshotResult.Success(new PaperAccountSnapshot(
                Equity: 100_000m,
                LastEquity: 100_000m,
                BuyingPower: 100_000m,
                DailyPnLPct: 0.0,
                OpenPositions: [],
                PendingOrders: [],
                FetchedAtUtc: FixedRegularMarketUtc)));

            // 1. Fresh DB startup automatically migrates schema & seeds system_state (id=1, ACTIVE)
            using var factory = CreateHostFactory(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = appConnection,
                    ["ALPACA_TRADING_ENABLED"] = "true",
                    ["ALPACA_API_KEY"] = "PK_VALID_PAPER_TEST_KEY",
                    ["ALPACA_SECRET_KEY"] = "SK_VALID_PAPER_TEST_SECRET",
                    ["Sentix:ConfigPath"] = UniverseAndXTests.UniversePath,
                    ["Sentix:NativeSocketPath"] = Path.Combine(Path.GetTempPath(), $"sx-{Guid.NewGuid():N}.sock"),
                    ["Sec:UserAgent"] = "disabled"
                },
                services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FixedClock(FixedRegularMarketUtc));
                    services.RemoveAll<IAlpacaPaperBrokerClient>();
                    services.AddSingleton<IAlpacaPaperBrokerClient>(stubBroker);
                    services.RemoveAll<IPaperPortfolioSnapshotProvider>();
                    services.AddSingleton<IPaperPortfolioSnapshotProvider>(stubPortfolioProvider);
                });

            using var client = factory.CreateClient();

            using (var overviewRes = await client.GetAsync("/api/overview"))
            {
                Assert.Equal(HttpStatusCode.OK, overviewRes.StatusCode);
                using var doc = JsonDocument.Parse(await overviewRes.Content.ReadAsStringAsync());
                Assert.Equal("ACTIVE", doc.RootElement.GetProperty("tradingState").GetString());
                Assert.True(doc.RootElement.GetProperty("orderExecutionEnabled").GetBoolean());
            }

            // 2. Test persisted EfCoreTradingStateStore transitions (HALT -> RESUME -> DAILY_LOSS 409)
            var stateStore = factory.Services.GetRequiredService<ITradingStateStore>();
            var halted = await stateStore.HaltManualAsync("OPERATOR_UI", CancellationToken.None);
            Assert.Equal(TradingStateWriteStatus.Updated, halted.Status);
            Assert.Equal(RiskTradingState.HaltedManual, (await stateStore.ReadTradingStateAsync(CancellationToken.None)).State);

            var resumed = await stateStore.ResumeManualAsync("OPERATOR_UI", CancellationToken.None);
            Assert.Equal(TradingStateWriteStatus.Updated, resumed.Status);
            Assert.Equal(RiskTradingState.Active, (await stateStore.ReadTradingStateAsync(CancellationToken.None)).State);

            var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<SentiXDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE system_state SET trading_state = 'HALTED_DAILY_LOSS' WHERE id = 1");
            }

            // H2: HaltManualAsync MUST NOT downgrade HALTED_DAILY_LOSS to HALTED_MANUAL!
            var haltOverDailyLoss = await stateStore.HaltManualAsync("OPERATOR_UI", CancellationToken.None);
            Assert.Equal(TradingStateWriteStatus.Updated, haltOverDailyLoss.Status);
            Assert.Equal("HALTED_DAILY_LOSS", haltOverDailyLoss.CurrentState);

            var blockedResume = await stateStore.ResumeManualAsync("OPERATOR_UI", CancellationToken.None);
            Assert.Equal(TradingStateWriteStatus.CannotResumeNonManualHalt, blockedResume.Status);
            Assert.Equal("HALTED_DAILY_LOSS", blockedResume.CurrentState);

            // DB-level circuit-breaker recovery back to ACTIVE, then verify pipeline execution & write-ahead orders persistence
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE system_state SET trading_state = 'ACTIVE' WHERE id = 1");
            }

            var quotes = factory.Services.GetRequiredService<IMarketQuoteCache>();
            quotes.Update(new MarketQuoteSnapshot("AAPL", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));

            await using var nativeServer = await StubUnixNativeServer.StartAsync();
            using var nativeClient = new NativeBridgeClient(nativeServer.SocketPath, timeProvider: new FixedClock(FixedRegularMarketUtc));
            var stubSwarm = new StubDecisionSwarm(new TradeProposal(
                "prop_pg_1",
                "evt_pg_1",
                TradeAction.Buy,
                "AAPL",
                InstrumentType.Equity,
                0.90,
                24,
                0.03m,
                0.06m,
                "Catalyst confirmed",
                "None"));

            using var pipeline = new MarketEventPipeline(
                dbFactory,
                nativeClient,
                NullLogger<MarketEventPipeline>.Instance,
                isConfigured: true,
                stubSwarm,
                quotes,
                factory.Services.GetRequiredService<UniverseConfig>(),
                new FixedClock(FixedRegularMarketUtc),
                factory.Services.GetRequiredService<IPaperExecutionEngine>(),
                factory.Services.GetRequiredService<AlpacaPaperOptions>(),
                factory.Services.GetRequiredService<IPaperPortfolioSnapshotProvider>(),
                factory.Services.GetRequiredService<IPaperOrderAuditStore>());

            var marketEvent = new MarketEvent(
                "evt_pg_1",
                EventSource.SecEdgar,
                "SEC EDGAR",
                FixedRegularMarketUtc,
                "8-K Apple Guidance Raise",
                "Summary",
                "Body",
                ["AAPL"]);

            await pipeline.ProcessAsync(marketEvent, """{"cik":"0000320193"}""", CancellationToken.None);
            Assert.Equal(1, brokerCalls);

            using var ordersApiRes = await client.GetAsync("/api/orders?limit=10");
            Assert.Equal(HttpStatusCode.OK, ordersApiRes.StatusCode);
            using var ordersDoc = JsonDocument.Parse(await ordersApiRes.Content.ReadAsStringAsync());
            var persistedOrder = Assert.Single(ordersDoc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("AAPL", persistedOrder.GetProperty("symbol").GetString());
            Assert.Equal("ACCEPTED", persistedOrder.GetProperty("status").GetString());
            Assert.Equal(25, persistedOrder.GetProperty("qty").GetInt32());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {dbName}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static WebApplicationFactory<Program> CreateHostFactory(
        Dictionary<string, string?> configOverrides,
        Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configOverrides));
            if (configureServices is not null)
            {
                host.ConfigureServices(configureServices);
            }
        });

    private sealed class InMemoryTradingStateStore(RiskTradingState initialState, DateTimeOffset nowUtc) : ITradingStateStore
    {
        private readonly object gate = new();
        private RiskTradingState state = initialState;

        public bool IsConfigured => true;

        public void SetState(RiskTradingState next)
        {
            lock (gate)
            {
                state = next;
            }
        }

        public Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken) =>
            ReadTradingStateAsync(cancellationToken);

        public Task<TradingStateReadResult> ReadTradingStateAsync(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return Task.FromResult(new TradingStateReadResult(true, state));
            }
        }

        public Task<TradingStateWriteResult> HaltManualAsync(string updatedBy, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                state = PersistedTradingStateFormat.ApplyManualHalt(state);
                return Task.FromResult(new TradingStateWriteResult(
                    TradingStateWriteStatus.Updated,
                    PersistedTradingStateFormat.ToDatabaseValue(state),
                    PersistedTradingStateFormat.SanitizeActor(updatedBy),
                    nowUtc));
            }
        }

        public Task<TradingStateWriteResult> ResumeManualAsync(string updatedBy, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (state is RiskTradingState.HaltedManual or RiskTradingState.Active)
                {
                    state = RiskTradingState.Active;
                    return Task.FromResult(new TradingStateWriteResult(
                        TradingStateWriteStatus.Updated,
                        "ACTIVE",
                        PersistedTradingStateFormat.SanitizeActor(updatedBy),
                        nowUtc));
                }

                var current = PersistedTradingStateFormat.ToDatabaseValue(state);
                return Task.FromResult(new TradingStateWriteResult(
                    TradingStateWriteStatus.CannotResumeNonManualHalt,
                    current,
                    PersistedTradingStateFormat.SanitizeActor(updatedBy),
                    nowUtc,
                    "CannotResumeNonManualHalt",
                    "Cannot resume non-manual halt."));
            }
        }
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<SentiXDbContext>
    {
        public SentiXDbContext CreateDbContext() => throw new InvalidOperationException("Database unreachable.");
    }

    private sealed class SpyPipeline(bool isConfigured, bool shouldThrow) : IMarketEventPipeline
    {
        public bool IsConfigured => isConfigured;
        public int EnsureReadyCalls { get; private set; }

        public Task EnsureReadyAsync(CancellationToken token)
        {
            EnsureReadyCalls++;
            if (shouldThrow)
            {
                throw new InvalidOperationException("Transient DB startup failure.");
            }
            return Task.CompletedTask;
        }

        public Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token) => Task.FromResult(0);
        public Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingBrokerClient(Func<AlpacaBracketOrderRequest, BrokerSubmissionResult> handler) : IAlpacaPaperBrokerClient
    {
        public Task<BrokerSubmissionResult> SubmitOrderAsync(AlpacaBracketOrderRequest order, CancellationToken cancellationToken) =>
            Task.FromResult(handler(order));
    }

    private sealed class StubPaperPortfolioProvider(Func<UniverseConfig?, PaperAccountSnapshotResult> handler) : IPaperPortfolioSnapshotProvider
    {
        public int Calls { get; private set; }

        public Task<PaperAccountSnapshotResult> GetSnapshotAsync(UniverseConfig? universe, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handler(universe));
        }
    }

    private sealed class StubPaperExecutionEngine(Func<PaperOrderExecutionRequest, PaperExecutionResult> handler) : IPaperExecutionEngine
    {
        public Task<PaperExecutionResult> ExecuteAsync(PaperOrderExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class InMemoryOrderAuditStore : IPaperOrderAuditStore
    {
        public Dictionary<string, OrderRow> Rows { get; } = new(StringComparer.Ordinal);
        public bool FailBegin { get; set; }
        public bool FailComplete { get; set; }

        public Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new RecentOrderAuditReadResult(true, Rows.Values.Where(r => r.UpdatedAtUtc >= sinceUtc).ToArray()));

        public Task<OrderIntentDisposition> TryBeginOrderIntentAsync(OrderRow pendingIntentRow, CancellationToken cancellationToken)
        {
            if (FailBegin)
            {
                return Task.FromResult(OrderIntentDisposition.FailedClosed);
            }

            if (Rows.TryGetValue(pendingIntentRow.ClientOrderId, out var existing))
            {
                if (string.Equals(existing.Status, "PENDING_SUBMIT", StringComparison.OrdinalIgnoreCase))
                {
                    Rows[pendingIntentRow.ClientOrderId] = existing with { UpdatedAtUtc = pendingIntentRow.UpdatedAtUtc };
                    return Task.FromResult(OrderIntentDisposition.ProceedToSubmit);
                }

                return Task.FromResult(OrderIntentDisposition.AlreadyFinalizedSkip);
            }

            Rows[pendingIntentRow.ClientOrderId] = pendingIntentRow;
            return Task.FromResult(OrderIntentDisposition.ProceedToSubmit);
        }

        public Task CompleteOrderIntentAsync(OrderRow completedRow)
        {
            if (!FailComplete)
            {
                Rows[completedRow.ClientOrderId] = completedRow;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubDecisionSwarm(TradeProposal proposal) : IDecisionSwarmOrchestrator
    {
        public Task<DecisionDebateOutcome> EvaluateAsync(DecisionDebateRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DecisionDebateOutcome(
                Proposal: proposal,
                PromptVersion: "v1-test",
                FactJson: """{"isActionable":true}""",
                BullJson: """{"thesis":"bull"}""",
                BearJson: """{"thesis":"bear"}""",
                SoftRiskJson: """{"risk":"low"}""",
                JudgeJson: JsonSerializer.Serialize(proposal, SentiXJson.Options),
                TotalTokens: 120,
                TotalLatencyMs: 45,
                FallbackUsed: false,
                FailureReason: null));
    }

    private sealed class StubUnixNativeServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.Socket listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task acceptLoop;

        public string SocketPath { get; }

        private StubUnixNativeServer(string socketPath, System.Net.Sockets.Socket listener)
        {
            SocketPath = socketPath;
            this.listener = listener;
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static Task<StubUnixNativeServer> StartAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"sx-p4-{Guid.NewGuid():N}.sock");
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            socket.Listen(4);
            return Task.FromResult(new StubUnixNativeServer(path, socket));
        }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptAsync(cts.Token);
                    await using var stream = new System.Net.Sockets.NetworkStream(client, ownsSocket: false);
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, cts.Token);
                    var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
                    var reqBytes = new byte[len];
                    await stream.ReadExactlyAsync(reqBytes, cts.Token);

                    const string responseJson = """
                        {"type":"score_event_response","event_id":"evt_pg_1","dedup_hash":"abc12345","is_duplicate":false,
                         "matched_tickers":["AAPL"],"matched_sector_etfs":["XLK"],
                         "finbert":{"positive":0.9,"negative":0.05,"neutral":0.05,"composite_score":0.85},
                         "pass_to_decision_swarm":true,"drop_reason":null,"processing_time_us":420}
                        """;
                    var respBytes = Encoding.UTF8.GetBytes(responseJson);
                    var respFrame = new byte[4 + respBytes.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(0, 4), (uint)respBytes.Length);
                    respBytes.CopyTo(respFrame.AsSpan(4));
                    await stream.WriteAsync(respFrame, cts.Token);
                }
                catch
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            listener.Dispose();
            try
            {
                await acceptLoop;
            }
            catch
            {
                // Ignore shutdown exceptions.
            }
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }
            cts.Dispose();
        }
    }
}
