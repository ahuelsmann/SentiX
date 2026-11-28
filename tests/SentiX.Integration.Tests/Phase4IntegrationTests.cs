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

        // (c5 / #315 H1) Held AAPL ($12k) + open pending AAPL buy ($10k) + candidate MSFT (TECH) buy ($5k on $100k equity -> $27k > 25% TECH cap)
        // MUST sum pending BUY notional onto the already-held AAPL OpenPosition and reject MSFT with 0 broker submissions!
        quotes.Update(new MarketQuoteSnapshot("MSFT", 249.90m, 250.00m, 249.95m, 4.0, FixedRegularMarketUtc));
        var msftProposal = new TradeProposal(
            "prop_msft_h1", "evt_msft_h1", TradeAction.Buy, "MSFT", InstrumentType.Equity,
            0.92, 24, 0.03m, 0.06m, "MSFT catalyst", "None");
        var heldPlusPendingSnapshot = new PaperAccountSnapshot(
            Equity: 100_000m,
            LastEquity: 100_000m,
            BuyingPower: 100_000m,
            DailyPnLPct: 0.0,
            OpenPositions: [new SentiX.Risk.OpenPosition("AAPL", "TECH", 60, 12_000m, 200m)],
            PendingOrders: [new SentiX.Risk.PendingOrder("SX-20260604-AAPL-BUY-open0001", "AAPL", SentiX.Risk.TradeAction.Buy, 50)],
            FetchedAtUtc: FixedRegularMarketUtc,
            PendingOrderLimitPrices: new Dictionary<string, decimal?> { ["SX-20260604-AAPL-BUY-open0001"] = 200m });

        var heldPlusPendingAudit = new InMemoryOrderAuditStore();
        var zeroBrokerSubmissions = 0;
        var neverCalledEngine = new StubPaperExecutionEngine(_ =>
        {
            zeroBrokerSubmissions++;
            throw new InvalidOperationException("Must not submit broker order when TECH sector cap is exceeded.");
        });
        using (var h1Pipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            new NativeBridgeClient(Path.Combine(Path.GetTempPath(), $"sx-h1-{Guid.NewGuid():N}.sock")),
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: true,
            quoteCache: quotes,
            universe: universe,
            timeProvider: new FixedClock(FixedRegularMarketUtc),
            paperExecution: neverCalledEngine,
            paperOptions: new AlpacaPaperOptions { TradingEnabled = true },
            paperPortfolioProvider: new StubPaperPortfolioProvider(_ => PaperAccountSnapshotResult.Success(heldPlusPendingSnapshot)),
            orderAuditStore: heldPlusPendingAudit))
        {
            RiskEvaluationRow? savedRisk = null;
            await h1Pipeline.ExecuteArmedEvaluationAndOrderAsync(
                msftProposal,
                "MSFT",
                FixedRegularMarketUtc,
                _ => Task.FromResult<string?>("ACTIVE"),
                (row, _) => { savedRisk = row; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.NotNull(savedRisk);
            Assert.False(savedRisk!.Approved);
            Assert.Contains("SectorExposureExceeded", savedRisk.Violations, StringComparison.Ordinal);
            Assert.Equal(0, zeroBrokerSubmissions);
        }

        // (c6 / #315 H1) Unpriceable or unknown-ticker pending BUY order MUST fail closed (SnapshotAvailable == false)
        var unpriceablePendingSnapshot = cleanAccount with
        {
            PendingOrders = [new SentiX.Risk.PendingOrder("SX-20260604-NVDA-BUY-noprice", "NVDA", SentiX.Risk.TradeAction.Buy, 10)],
            PendingOrderLimitPrices = new Dictionary<string, decimal?> { ["SX-20260604-NVDA-BUY-noprice"] = null } // NVDA has no quote in quoteCache
        };
        var unpriceableEval = MarketEventPipeline.TryEvaluateArmedHardRiskFromSnapshots(
            msftProposal, "MSFT", quotes, universe, RiskTradingState.Active, unpriceablePendingSnapshot, [], FixedRegularMarketUtc);
        Assert.False(unpriceableEval.SnapshotAvailable);

        var unknownTickerPendingSnapshot = cleanAccount with
        {
            PendingOrders = [new SentiX.Risk.PendingOrder("SX-20260604-ZZZZ-BUY-unknown", "ZZZZ", SentiX.Risk.TradeAction.Buy, 10)],
            PendingOrderLimitPrices = new Dictionary<string, decimal?> { ["SX-20260604-ZZZZ-BUY-unknown"] = 50m }
        };
        var unknownTickerEval = MarketEventPipeline.TryEvaluateArmedHardRiskFromSnapshots(
            msftProposal, "MSFT", quotes, universe, RiskTradingState.Active, unknownTickerPendingSnapshot, [], FixedRegularMarketUtc);
        Assert.False(unknownTickerEval.SnapshotAvailable);
    }

    [Fact]
    public async Task ProcessAsync_UncertainPostTimeoutKeepsPendingSubmit_AndNextProposalReconcilesReceiptWithoutDuplicatePost()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var clock = new AdvanceableClock(FixedRegularMarketUtc);
        var quotes = new InMemoryMarketQuoteCache();
        quotes.Update(new MarketQuoteSnapshot("AAPL", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));
        quotes.Update(new MarketQuoteSnapshot("JPM", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));

        await using var nativeServer = await StubUnixNativeServer.StartAsync();
        using var nativeClient = new NativeBridgeClient(nativeServer.SocketPath, timeProvider: clock);

        var postOrderCount = 0;
        var getLookupCount = 0;
        string? timedOutClientOrderId = null;

        var options = new AlpacaPaperOptions
        {
            TradingEnabled = true,
            PaperMode = true,
            AllowShorting = false,
            BaseUrl = AlpacaPaperOptions.DefaultPaperBaseUrl,
            ApiKey = "PK_TEST_E2E_TIMEOUT_RECONCILE",
            SecretKey = "SK_TEST_E2E_TIMEOUT_RECONCILE",
            RequestTimeout = TimeSpan.FromSeconds(5)
        };

        var brokerHttpHandler = new StubAlpacaHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/v2/orders")
            {
                var callNum = Interlocked.Increment(ref postOrderCount);
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var cid = doc.RootElement.GetProperty("client_order_id").GetString()!;
                var sym = doc.RootElement.GetProperty("symbol").GetString()!;

                if (callNum == 1)
                {
                    // Proposal 1 suffers an uncertain transport timeout AFTER Alpaca actually accepted it!
                    timedOutClientOrderId = cid;
                    throw new TimeoutException("Simulated uncertain POST /v2/orders timeout.");
                }

                // Proposal 2 (JPM) succeeds normally
                return JsonHttpResponse(HttpStatusCode.OK, $$$"""
                    {"id":"alp-jpm-2","client_order_id":"{{{cid}}}","symbol":"{{{sym}}}","qty":"25","side":"buy","type":"limit","order_class":"bracket","limit_price":"200.00","status":"accepted","submitted_at":"2026-06-04T15:01:05Z","created_at":"2026-06-04T15:01:05Z"}
                    """);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/v2/orders:by_client_order_id")
            {
                Interlocked.Increment(ref getLookupCount);
                Assert.NotNull(timedOutClientOrderId);
                Assert.Contains(Uri.EscapeDataString(timedOutClientOrderId!), req.RequestUri.Query, StringComparison.Ordinal);
                return JsonHttpResponse(HttpStatusCode.OK, $$$"""
                    {"id":"alp-aapl-recovered-1","client_order_id":"{{{timedOutClientOrderId}}}","symbol":"AAPL","qty":"25","side":"buy","type":"limit","order_class":"bracket","limit_price":"200.00","status":"accepted","submitted_at":"2026-06-04T15:00:00Z","created_at":"2026-06-04T15:00:00Z"}
                    """);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/v2/account")
            {
                return JsonHttpResponse(HttpStatusCode.OK, """
                    {"equity":"100000.00","last_equity":"100000.00","buying_power":"95000.00","trading_blocked":false,"account_blocked":false}
                    """);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/v2/positions")
            {
                return JsonHttpResponse(HttpStatusCode.OK, "[]");
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.PathAndQuery == "/v2/orders?status=open&limit=500")
            {
                var openJson = timedOutClientOrderId is null
                    ? "[]"
                    : $$$"""[{"id":"alp-aapl-recovered-1","client_order_id":"{{{timedOutClientOrderId}}}","symbol":"AAPL","side":"buy","qty":"25","limit_price":"200.00"}]""";
                return JsonHttpResponse(HttpStatusCode.OK, openJson);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(brokerHttpHandler);
        var brokerClient = new AlpacaPaperBrokerClient(httpClient, options, timeProvider: clock);
        var portfolioProvider = new AlpacaPaperPortfolioSnapshotProvider(httpClient, options, timeProvider: clock);
        var executionEngine = new PaperExecutionEngine(
            options,
            new StaticTradingStateProvider(RiskTradingState.Active),
            brokerClient,
            clock);

        var proposalQueue = new Queue<TradeProposal>([
            new TradeProposal("prop_timeout_1", "evt_timeout_1", TradeAction.Buy, "AAPL", InstrumentType.Equity, 0.90, 24, 0.03m, 0.06m, "AAPL 8-K", "None"),
            new TradeProposal("prop_next_2", "evt_next_2", TradeAction.Buy, "JPM", InstrumentType.Equity, 0.90, 24, 0.03m, 0.06m, "JPM 8-K", "None")
        ]);
        var queuedSwarm = new QueueDecisionSwarm(proposalQueue);
        var auditStore = new InMemoryOrderAuditStore();
        var pipelinePersistence = new InMemoryPipelinePersistence();

        using var pipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            nativeClient,
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: true,
            queuedSwarm,
            quotes,
            universe,
            clock,
            executionEngine,
            options,
            portfolioProvider,
            auditStore,
            brokerClient,
            pipelinePersistence);

        // 1. Run Proposal 1 through full MarketEventPipeline.ProcessAsync -> POST /v2/orders times out -> MUST remain PENDING_SUBMIT!
        var event1 = new MarketEvent("evt_timeout_1", EventSource.SecEdgar, "SEC", clock.GetUtcNow(), "AAPL 8-K", "Sum", "Body", ["AAPL"]);
        await pipeline.ProcessAsync(event1, """{"id":1}""", CancellationToken.None);

        Assert.Equal(1, postOrderCount);
        Assert.Equal(0, getLookupCount);
        Assert.NotNull(timedOutClientOrderId);
        Assert.Equal("PENDING_SUBMIT", auditStore.Rows[timedOutClientOrderId!].Status);

        // 2. Advance clock > 60s lease and run Proposal 2 (JPM) through full MarketEventPipeline.ProcessAsync:
        // Pre-risk reconciliation MUST resolve Proposal 1 via GET /v2/orders:by_client_order_id (200) -> ACCEPTED
        // without issuing a duplicate POST /v2/orders for Proposal 1, then submit Proposal 2 cleanly!
        clock.Advance(TimeSpan.FromSeconds(65));
        quotes.Update(new MarketQuoteSnapshot("JPM", 199.90m, 200.00m, 199.95m, 5.0, clock.GetUtcNow()));

        var event2 = new MarketEvent("evt_next_2", EventSource.SecEdgar, "SEC", clock.GetUtcNow(), "JPM 8-K", "Sum", "Body", ["JPM"]);
        await pipeline.ProcessAsync(event2, """{"id":2}""", CancellationToken.None);

        Assert.Equal(1, getLookupCount);
        Assert.Equal(2, postOrderCount); // Exactly 1 POST for AAPL + 1 POST for JPM (ZERO duplicate POSTs for AAPL!)
        Assert.Equal("ACCEPTED", auditStore.Rows[timedOutClientOrderId!].Status);
        Assert.Equal("alp-aapl-recovered-1", auditStore.Rows[timedOutClientOrderId!].AlpacaOrderId);
        Assert.Equal(2, auditStore.Rows.Count);
        Assert.All(auditStore.Rows.Values, r => Assert.Equal("ACCEPTED", r.Status));
    }

    [Fact]
    public async Task ArmedExecutionGate_SerializesConcurrentProposals_PreventingJointExposureBreach()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var quotes = new InMemoryMarketQuoteCache();
        quotes.Update(new MarketQuoteSnapshot("AAPL", 199.90m, 200.00m, 199.95m, 5.0, FixedRegularMarketUtc));
        quotes.Update(new MarketQuoteSnapshot("MSFT", 249.90m, 250.00m, 249.95m, 4.0, FixedRegularMarketUtc));

        // Account already holds $20,000 (20%) in TECH (XLK). MaxSectorExposurePct is 25% ($25,000).
        // AAPL ($5,000) and MSFT ($5,000) each individually fit (20% + 5% = 25%),
        // but jointly they would reach 30% (> 25%) if unserialized!
        var portfolioProvider = new StubPaperPortfolioProvider(_ => PaperAccountSnapshotResult.Success(new PaperAccountSnapshot(
            Equity: 100_000m,
            LastEquity: 100_000m,
            BuyingPower: 100_000m,
            DailyPnLPct: 0.0,
            OpenPositions: [new SentiX.Risk.OpenPosition("XLK", "TECH", 100, 20_000m, 200m)],
            PendingOrders: [],
            FetchedAtUtc: FixedRegularMarketUtc)));

        var auditStore = new InMemoryOrderAuditStore();
        var firstSubmitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstSubmitFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitCalls = 0;

        var slowExecutionEngine = new AsyncStubPaperExecutionEngine(async (req, _) =>
        {
            var count = Interlocked.Increment(ref submitCalls);
            firstSubmitEntered.TrySetResult();
            await allowFirstSubmitFinish.Task;
            return new PaperExecutionResult(
                ExecutionOutcomeStatus.Submitted,
                ExecutionRejectionCode.None,
                null,
                req.RiskDecision.ClientOrderId,
                RiskTradingState.Active,
                new AlpacaOrderReceipt(
                    $"alp-conc-{count}", req.RiskDecision.ClientOrderId!, req.Proposal.Ticker, "accepted",
                    "buy", "limit", "bracket", req.RiskDecision.CalculatedQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    req.LimitPrice, null, FixedRegularMarketUtc, FixedRegularMarketUtc),
                null,
                FixedRegularMarketUtc);
        });

        using var pipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            new NativeBridgeClient(Path.Combine(Path.GetTempPath(), $"sx-conc-{Guid.NewGuid():N}.sock")),
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: true,
            quoteCache: quotes,
            universe: universe,
            timeProvider: new FixedClock(FixedRegularMarketUtc),
            paperExecution: slowExecutionEngine,
            paperOptions: new AlpacaPaperOptions { TradingEnabled = true },
            paperPortfolioProvider: portfolioProvider,
            orderAuditStore: auditStore);

        var aaplProposal = new TradeProposal(
            "prop_conc_aapl", "evt_conc_1", TradeAction.Buy, "AAPL", InstrumentType.Equity,
            0.91, 24, 0.03m, 0.06m, "AAPL catalyst", "None");
        var msftProposal = new TradeProposal(
            "prop_conc_msft", "evt_conc_2", TradeAction.Buy, "MSFT", InstrumentType.Equity,
            0.91, 24, 0.03m, 0.06m, "MSFT catalyst", "None");

        var recordedRiskRows = new System.Collections.Concurrent.ConcurrentBag<RiskEvaluationRow>();

        var task1 = pipeline.ExecuteArmedEvaluationAndOrderAsync(
            aaplProposal, "AAPL", FixedRegularMarketUtc,
            _ => Task.FromResult<string?>("ACTIVE"),
            (row, _) => { recordedRiskRows.Add(row); return Task.CompletedTask; },
            CancellationToken.None);

        await firstSubmitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Launch second concurrent proposal while first broker submission is STILL in-flight!
        var task2 = pipeline.ExecuteArmedEvaluationAndOrderAsync(
            msftProposal, "MSFT", FixedRegularMarketUtc,
            _ => Task.FromResult<string?>("ACTIVE"),
            (row, _) => { recordedRiskRows.Add(row); return Task.CompletedTask; },
            CancellationToken.None);

        await task2.WaitAsync(TimeSpan.FromSeconds(5));
        allowFirstSubmitFinish.TrySetResult();
        await task1.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, submitCalls);
        Assert.Equal(2, recordedRiskRows.Count);
        Assert.Single(recordedRiskRows, r => r.Approved);
        var rejectedRisk = Assert.Single(recordedRiskRows, r => !r.Approved);
        Assert.Contains("SectorExposureExceeded", rejectedRisk.Violations, StringComparison.Ordinal);
        Assert.Single(auditStore.Rows.Values);
        Assert.Equal("ACCEPTED", Assert.Single(auditStore.Rows.Values).Status);
    }

    [Fact]
    public async Task WriteAheadOrderIntent_EnforcesSingleWriterLease_SafeBrokerReconciliation_FencedCompletion_AndUnboundedPendingRetention()
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

        var submitCalls = 0;
        var fakeEngine = new StubPaperExecutionEngine(req =>
        {
            submitCalls++;
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
        Assert.Equal(0, submitCalls);
        Assert.Empty(failBeginStore.Rows);

        // (b) Broker accepts order + post-submit DB update fails -> durable PENDING_SUBMIT row remains visible!
        var store = new InMemoryOrderAuditStore { FailComplete = true };
        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            store, fakeEngine, new FixedClock(FixedRegularMarketUtc), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, submitCalls);
        var durablePending = Assert.Single(store.Rows.Values);
        Assert.Equal(riskDecision.ClientOrderId, durablePending.ClientOrderId);
        Assert.Equal("PENDING_SUBMIT", durablePending.Status);

        // (c / H2a) Active PENDING_SUBMIT lease (< 60s old) MUST NOT trigger a second SubmitOrderAsync!
        store.FailComplete = false;
        var lookupCalls = 0;
        var brokerLookupStub = new RecordingBrokerClient(
            _ => throw new InvalidOperationException("SubmitOrderAsync must not be called during reconciliation."),
            cid =>
            {
                lookupCalls++;
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.Found,
                    HttpStatusCode.OK,
                    ExecutionRejectionCode.None,
                    null,
                    new AlpacaOrderReceipt(
                        "alp-reconciled-99", cid, "AAPL", "accepted", "buy", "limit",
                        "bracket", "25", 200m, null, FixedRegularMarketUtc, FixedRegularMarketUtc));
            });

        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            store, fakeEngine, new FixedClock(FixedRegularMarketUtc.AddSeconds(15)), NullLogger.Instance, CancellationToken.None, brokerLookupStub);
        Assert.Equal(1, submitCalls); // Still 1: active lease prevented blind resubmit!
        Assert.Equal(0, lookupCalls);
        Assert.Equal("PENDING_SUBMIT", Assert.Single(store.Rows.Values).Status);

        // (d / H2b) Expired PENDING_SUBMIT lease (> 60s old) reconciles via GetOrderByClientOrderIdAsync (HTTP 200)
        // into ACCEPTED WITHOUT calling SubmitOrderAsync!
        await MarketEventPipeline.ExecuteAndPersistPaperOrderWithWriteAheadAsync(
            proposal.ProposalId, "AAPL", proposal.Action, riskProposal, riskDecision, entryPrice, 0,
            store, fakeEngine, new FixedClock(FixedRegularMarketUtc.AddSeconds(65)), NullLogger.Instance, CancellationToken.None, brokerLookupStub);
        Assert.Equal(1, submitCalls); // ZERO extra SubmitOrderAsync calls!
        Assert.Equal(1, lookupCalls);
        var reconciled = Assert.Single(store.Rows.Values);
        Assert.Equal("ACCEPTED", reconciled.Status);
        Assert.Equal("alp-reconciled-99", reconciled.AlpacaOrderId);

        // (e / H2c) Late duplicate/error CompleteOrderIntentAsync CANNOT overwrite an already-persisted ACCEPTED receipt!
        await store.CompleteOrderIntentAsync(reconciled with
        {
            AlpacaOrderId = null,
            Status = "FAILED_BROKER",
            UpdatedAtUtc = FixedRegularMarketUtc.AddMinutes(5)
        });
        var preserved = Assert.Single(store.Rows.Values);
        Assert.Equal("ACCEPTED", preserved.Status);
        Assert.Equal("alp-reconciled-99", preserved.AlpacaOrderId);

        // (f / M1) Unresolved PENDING_SUBMIT older than 48h (72h old) is STILL retained in GetRecentOrdersAsync and PendingOrders risk input!
        var stale72hUtc = FixedRegularMarketUtc.AddHours(-72);
        var m1Store = new InMemoryOrderAuditStore();
        m1Store.Rows["SX-20260601-AAPL-BUY-stale72h"] = new OrderRow(
            "SX-20260601-AAPL-BUY-stale72h", null, "prop_old_pending", "AAPL", "BUY", "LIMIT",
            25, 200m, 194m, 212m, "PENDING_SUBMIT", null, stale72hUtc, stale72hUtc);
        m1Store.Rows["SX-20260601-MSFT-BUY-oldfilled"] = new OrderRow(
            "SX-20260601-MSFT-BUY-oldfilled", "alp-old", "prop_old_filled", "MSFT", "BUY", "LIMIT",
            20, 250m, 242.5m, 265m, "FILLED", 250m, stale72hUtc, stale72hUtc);

        var retained = await m1Store.GetRecentOrdersAsync(FixedRegularMarketUtc.AddHours(-48), CancellationToken.None);
        Assert.True(retained.IsSuccess);
        var retainedRow = Assert.Single(retained.Orders); // 72h FILLED row filtered out; 72h PENDING_SUBMIT retained!
        Assert.Equal("SX-20260601-AAPL-BUY-stale72h", retainedRow.ClientOrderId);

        var emptyBrokerAccount = new PaperAccountSnapshot(
            100_000m, 100_000m, 100_000m, 0.0, [], [], FixedRegularMarketUtc);
        var (_, _, m1Decision, _) = MarketEventPipeline.EvaluateArmedHardRiskFromSnapshots(
            proposal, "AAPL", quotes, universe, RiskTradingState.Active, emptyBrokerAccount, retained.Orders, FixedRegularMarketUtc);
        Assert.False(m1Decision.Approved);
        Assert.Contains(m1Decision.Violations, v => v.Code == SentiX.Risk.RiskViolationCode.DuplicatePosition);
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
                factory.Services.GetRequiredService<IPaperOrderAuditStore>(),
                factory.Services.GetRequiredService<IAlpacaPaperBrokerClient>());

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

    [Fact]
    public async Task ProcessAsync_PersistsRawMarketEventBeforeScoring_AndEmitsZeroOrdersWhenScoringIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var sawRawEventBeforeScore = false;
        var persistence = new InMemoryPipelinePersistence();
        await using var cancellingNative = new MockUnixServer(async (stream, request, serverToken) =>
        {
            if (request.Type == IpcMessageType.HealthCheck)
            {
                await StubUnixNativeServer.WriteTestEnvelopeAsync(
                    stream,
                    request,
                    IpcMessageType.HealthCheckAck,
                    new NativeHealth(NativeStatus.Ready, "0.1.0", true, 10, 0),
                    serverToken);
                return;
            }

            if (request.Type == IpcMessageType.ScoreEvent)
            {
                sawRawEventBeforeScore = persistence.RecordedEventIds.Contains("evt_cancel_during_score");
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), serverToken);
            }
        });

        var nativeClient = new NativeBridgeClient(cancellingNative.Path);
        var auditStore = new InMemoryOrderAuditStore();
        var brokerCalls = 0;
        var engine = new StubPaperExecutionEngine(_ =>
        {
            Interlocked.Increment(ref brokerCalls);
            throw new InvalidOperationException("Broker should not be invoked when scoring is cancelled.");
        });
        var paperOptions = new AlpacaPaperOptions
        {
            TradingEnabled = true,
            PaperMode = true,
            AllowShorting = false,
            BaseUrl = "https://paper-api.alpaca.markets",
            ApiKey = "PKTEST_KEY",
            SecretKey = "SKTEST_SECRET"
        };

        using var pipeline = new MarketEventPipeline(
            new ThrowingContextFactory(),
            nativeClient,
            NullLogger<MarketEventPipeline>.Instance,
            isConfigured: true,
            decisionSwarm: null,
            quoteCache: null,
            universe: null,
            timeProvider: new FixedClock(FixedRegularMarketUtc),
            paperExecution: engine,
            paperOptions: paperOptions,
            paperPortfolioProvider: null,
            orderAuditStore: auditStore,
            paperBrokerClient: null,
            pipelinePersistence: persistence);

        var marketEvent = new MarketEvent(
            "evt_cancel_during_score",
            EventSource.SecEdgar,
            "SEC EDGAR",
            FixedRegularMarketUtc,
            "8-K Raw Audit Before Scoring",
            "Summary",
            "Body",
            ["AAPL"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.ProcessAsync(marketEvent, """{"raw":true}""", cts.Token));

        Assert.True(sawRawEventBeforeScore);
        Assert.Equal(["evt_cancel_during_score"], persistence.RecordedEventIds);
        Assert.Empty(persistence.RecordedFastPathRows);
        Assert.Equal(0, brokerCalls);
        Assert.Empty(auditStore.Rows);
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

    private sealed class RecordingBrokerClient(
        Func<AlpacaBracketOrderRequest, BrokerSubmissionResult> submitHandler,
        Func<string, BrokerOrderLookupResult>? lookupHandler = null) : IAlpacaPaperBrokerClient
    {
        public Task<BrokerSubmissionResult> SubmitOrderAsync(AlpacaBracketOrderRequest order, CancellationToken cancellationToken) =>
            Task.FromResult(submitHandler(order));

        public Task<BrokerOrderLookupResult> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(lookupHandler is not null
                ? lookupHandler(clientOrderId)
                : new BrokerOrderLookupResult(BrokerOrderLookupOutcome.NotFound, HttpStatusCode.NotFound, ExecutionRejectionCode.None, null, null));
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

    private sealed class AsyncStubPaperExecutionEngine(
        Func<PaperOrderExecutionRequest, CancellationToken, Task<PaperExecutionResult>> handler) : IPaperExecutionEngine
    {
        public Task<PaperExecutionResult> ExecuteAsync(PaperOrderExecutionRequest request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class InMemoryOrderAuditStore : IPaperOrderAuditStore
    {
        private readonly object gate = new();
        public Dictionary<string, OrderRow> Rows { get; } = new(StringComparer.Ordinal);
        public bool FailBegin { get; set; }
        public bool FailComplete { get; set; }

        public async Task ReconcileExpiredPendingOrdersAsync(
            IAlpacaPaperBrokerClient? brokerClient,
            TimeProvider clock,
            CancellationToken cancellationToken)
        {
            if (brokerClient is null)
            {
                return;
            }

            OrderRow[] expired;
            var cutoff = clock.GetUtcNow() - EfCorePaperOrderAuditStore.PendingSubmitLeaseDuration;
            lock (gate)
            {
                expired = Rows.Values
                    .Where(r => r.Status == "PENDING_SUBMIT" && r.UpdatedAtUtc <= cutoff)
                    .OrderBy(r => r.UpdatedAtUtc)
                    .ToArray();
            }

            foreach (var row in expired)
            {
                await TryClaimAndReconcileAsync(row, brokerClient, clock, cancellationToken);
            }
        }

        public Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(DateTimeOffset finalizedSinceUtc, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var rows = Rows.Values
                    .Where(r => r.Status == "PENDING_SUBMIT" || r.UpdatedAtUtc >= finalizedSinceUtc)
                    .ToArray();
                return Task.FromResult(new RecentOrderAuditReadResult(true, rows));
            }
        }

        public async Task<OrderIntentReservationStatus> TryReserveOrderIntentAsync(
            OrderRow pendingIntentRow,
            IAlpacaPaperBrokerClient? brokerClient,
            TimeProvider clock,
            CancellationToken cancellationToken)
        {
            OrderRow? existingSnapshot;
            var nowUtc = clock.GetUtcNow();

            lock (gate)
            {
                if (FailBegin)
                {
                    return OrderIntentReservationStatus.FailedClosed;
                }

                if (!Rows.TryGetValue(pendingIntentRow.ClientOrderId, out existingSnapshot))
                {
                    Rows[pendingIntentRow.ClientOrderId] = pendingIntentRow;
                    return OrderIntentReservationStatus.ReservedForSubmit;
                }
            }

            if (!string.Equals(existingSnapshot.Status, "PENDING_SUBMIT", StringComparison.Ordinal))
            {
                return OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
            }

            if (existingSnapshot.UpdatedAtUtc > nowUtc - EfCorePaperOrderAuditStore.PendingSubmitLeaseDuration || brokerClient is null)
            {
                return OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
            }

            var outcome = await TryClaimAndReconcileAsync(existingSnapshot, brokerClient, clock, cancellationToken);
            return outcome == BrokerOrderLookupOutcome.Found
                ? OrderIntentReservationStatus.ReconciledExistingReceipt
                : OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
        }

        public Task CompleteOrderIntentAsync(OrderRow completedRow)
        {
            lock (gate)
            {
                if (!FailComplete &&
                    Rows.TryGetValue(completedRow.ClientOrderId, out var existing) &&
                    string.Equals(existing.Status, "PENDING_SUBMIT", StringComparison.Ordinal))
                {
                    Rows[completedRow.ClientOrderId] = completedRow;
                }
            }

            return Task.CompletedTask;
        }

        private async Task<BrokerOrderLookupOutcome> TryClaimAndReconcileAsync(
            OrderRow expectedRow,
            IAlpacaPaperBrokerClient brokerClient,
            TimeProvider clock,
            CancellationToken cancellationToken)
        {
            var claimNow = clock.GetUtcNow();
            OrderRow claimedRow;
            lock (gate)
            {
                if (!Rows.TryGetValue(expectedRow.ClientOrderId, out var current) ||
                    !string.Equals(current.Status, "PENDING_SUBMIT", StringComparison.Ordinal) ||
                    current.UpdatedAtUtc != expectedRow.UpdatedAtUtc)
                {
                    return BrokerOrderLookupOutcome.AmbiguousFailure;
                }

                claimedRow = current with { UpdatedAtUtc = claimNow };
                Rows[expectedRow.ClientOrderId] = claimedRow;
            }

            var lookup = await brokerClient.GetOrderByClientOrderIdAsync(claimedRow.ClientOrderId, cancellationToken);
            if (lookup.Outcome == BrokerOrderLookupOutcome.Found && lookup.Receipt is not null)
            {
                await CompleteOrderIntentAsync(claimedRow with
                {
                    AlpacaOrderId = lookup.Receipt.OrderId.Trim(),
                    Status = lookup.Receipt.Status.Trim().ToUpperInvariant(),
                    FilledAvgPrice = lookup.Receipt.FilledAvgPrice,
                    SubmittedAtUtc = lookup.Receipt.SubmittedAtUtc ?? claimNow,
                    UpdatedAtUtc = clock.GetUtcNow()
                });
                return BrokerOrderLookupOutcome.Found;
            }

            if (lookup.Outcome == BrokerOrderLookupOutcome.NotFound)
            {
                await CompleteOrderIntentAsync(claimedRow with
                {
                    AlpacaOrderId = null,
                    Status = "FAILED_BROKER",
                    UpdatedAtUtc = clock.GetUtcNow()
                });
                return BrokerOrderLookupOutcome.NotFound;
            }

            return BrokerOrderLookupOutcome.AmbiguousFailure;
        }
    }

    private static HttpResponseMessage JsonHttpResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class AdvanceableClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private long ticks = initialUtc.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
    }

    private sealed class StubAlpacaHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class QueueDecisionSwarm(Queue<TradeProposal> proposals) : IDecisionSwarmOrchestrator
    {
        public Task<DecisionDebateOutcome> EvaluateAsync(DecisionDebateRequest request, CancellationToken cancellationToken)
        {
            var next = proposals.Dequeue();
            return Task.FromResult(new DecisionDebateOutcome(
                Proposal: next,
                PromptVersion: "v1-test",
                FactJson: """{"isActionable":true}""",
                BullJson: """{"thesis":"bull"}""",
                BearJson: """{"thesis":"bear"}""",
                SoftRiskJson: """{"risk":"low"}""",
                JudgeJson: JsonSerializer.Serialize(next, SentiXJson.Options),
                TotalTokens: 100,
                TotalLatencyMs: 30,
                FallbackUsed: false,
                FailureReason: null));
        }
    }

    private sealed class InMemoryPipelinePersistence : IPipelinePersistence
    {
        private readonly HashSet<string> proposalIds = new(StringComparer.Ordinal);
        public List<string> RecordedEventIds { get; } = [];
        public List<FastPathResultRow> RecordedFastPathRows { get; } = [];
        public string TradingStateRaw { get; set; } = "ACTIVE";

        public Task RecordMarketEventAsync(MarketEvent marketEvent, string source, string rawPayload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RecordedEventIds.Add(marketEvent.EventId);
            return Task.CompletedTask;
        }

        public Task RecordFastPathResultAsync(FastPathResultRow fastPathRow, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RecordedFastPathRows.Add(fastPathRow);
            return Task.CompletedTask;
        }

        public Task<string[]> GetUpcomingScheduleDescriptionsAsync(DateTimeOffset nowUtc, CancellationToken token) =>
            Task.FromResult(Array.Empty<string>());

        public Task<bool> ProposalExistsAsync(string proposalId, CancellationToken token) =>
            Task.FromResult(proposalIds.Contains(proposalId));

        public Task<string?> ReadPersistedTradingStateAsync(CancellationToken token) =>
            Task.FromResult<string?>(TradingStateRaw);

        public Task PersistDebateAndRiskAsync(DecisionDebateRow debateRow, RiskEvaluationRow riskRow, CancellationToken token)
        {
            proposalIds.Add(debateRow.ProposalId);
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
        private readonly MockUnixServer inner;

        public string SocketPath => inner.Path;

        private StubUnixNativeServer()
        {
            inner = new MockUnixServer((stream, request, token) => request.Type switch
            {
                IpcMessageType.HealthCheck => WriteEnvelopeAsync(
                    stream,
                    request,
                    IpcMessageType.HealthCheckAck,
                    new NativeHealth(NativeStatus.Ready, "0.1.0", true, 10, 0),
                    token),
                IpcMessageType.ScoreEvent => WriteEnvelopeAsync(
                    stream,
                    request,
                    IpcMessageType.ScoreEventResult,
                    new FastPathResult(
                        request.Payload.GetProperty("eventId").GetString()!,
                        false,
                        "sha256:test",
                        ["AAPL"],
                        ["XLK"],
                        new FinbertScores(0.90, 0.05, 0.05, 0.85),
                        true,
                        null,
                        420),
                    token),
                _ => throw new InvalidDataException($"Unexpected IPC message type {request.Type}.")
            });
        }

        public static Task<StubUnixNativeServer> StartAsync() => Task.FromResult(new StubUnixNativeServer());

        internal static Task WriteTestEnvelopeAsync<T>(
            Stream stream,
            IpcEnvelope<JsonElement> request,
            IpcMessageType responseType,
            T payload,
            CancellationToken token) =>
            WriteEnvelopeAsync(stream, request, responseType, payload, token);

        private static async Task WriteEnvelopeAsync<T>(
            Stream stream,
            IpcEnvelope<JsonElement> request,
            IpcMessageType responseType,
            T payload,
            CancellationToken token)
        {
            var response = new IpcEnvelope<T>(1, request.CorrelationId, responseType, DateTimeOffset.UtcNow, payload);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, SentiXJson.Options);
            var prefix = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)bytes.Length);
            await stream.WriteAsync(prefix, token);
            await stream.WriteAsync(bytes, token);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
