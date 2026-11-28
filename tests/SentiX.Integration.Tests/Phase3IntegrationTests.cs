using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.Ingestion;

namespace SentiX.Integration.Tests;

public sealed class Phase3IntegrationTests
{
    [Fact]
    public async Task HostPhase3EndpointsReturnSafeShadowDefaultsAndCachedQuotes()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
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
            })));

        var quotes = factory.Services.GetRequiredService<IMarketQuoteCache>();
        var quoteTime = new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);
        quotes.Update(new MarketQuoteSnapshot("AAPL", 230.10m, 230.20m, 230.15m, 4.34, quoteTime));

        using var client = factory.CreateClient();

        using var overviewRes = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.OK, overviewRes.StatusCode);
        Assert.True(overviewRes.Headers.CacheControl?.NoStore);
        using var overview = JsonDocument.Parse(await overviewRes.Content.ReadAsStringAsync());
        Assert.Equal(4, overview.RootElement.GetProperty("phase").GetInt32());
        Assert.False(overview.RootElement.GetProperty("paperSwitchArmed").GetBoolean());
        Assert.False(overview.RootElement.GetProperty("orderExecutionEnabled").GetBoolean());
        Assert.False(overview.RootElement.GetProperty("databaseConfigured").GetBoolean());
        Assert.Equal(1, overview.RootElement.GetProperty("counts").GetProperty("cachedQuotes").GetInt32());

        using var quotesRes = await client.GetAsync("/api/quotes");
        Assert.Equal(HttpStatusCode.OK, quotesRes.StatusCode);
        using var quotesJson = JsonDocument.Parse(await quotesRes.Content.ReadAsStringAsync());
        var quoteItem = Assert.Single(quotesJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("AAPL", quoteItem.GetProperty("symbol").GetString());
        Assert.Equal(230.15m, quoteItem.GetProperty("midPrice").GetDecimal());

        using var debatesRes = await client.GetAsync("/api/debates?limit=25");
        Assert.Equal(HttpStatusCode.OK, debatesRes.StatusCode);
        using var debatesJson = JsonDocument.Parse(await debatesRes.Content.ReadAsStringAsync());
        Assert.Empty(debatesJson.RootElement.GetProperty("items").EnumerateArray());

        using var calendarRes = await client.GetAsync("/api/calendar");
        Assert.Equal(HttpStatusCode.OK, calendarRes.StatusCode);
        using var calendarJson = JsonDocument.Parse(await calendarRes.Content.ReadAsStringAsync());
        Assert.Empty(calendarJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void EvaluateShadowHardRiskUsesShadowPortfolioWithoutCreatingOrders()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var quotes = new InMemoryMarketQuoteCache();
        var now = new DateTimeOffset(2026, 6, 4, 15, 0, 0, TimeSpan.Zero); // 11:00 AM ET regular session
        quotes.Update(new MarketQuoteSnapshot("AAPL", 200m, 200.10m, 200.05m, 5.0, now));

        var proposal = new TradeProposal(
            "prop_test_1",
            "evt_1",
            TradeAction.Buy,
            "AAPL",
            InstrumentType.Equity,
            0.85,
            24,
            0.03m,
            0.06m,
            "Strong SEC 8-K catalyst",
            "Minor margin risk");

        var decision = MarketEventPipeline.EvaluateShadowHardRisk(
            proposal, "AAPL", quotes, universe, Risk.TradingState.Active, now);
        Assert.True(decision.Approved);
        Assert.True(decision.CalculatedQuantity > 0);
        Assert.NotNull(decision.ClientOrderId);

        var blockedOutsideHours = MarketEventPipeline.EvaluateShadowHardRisk(
            proposal, "AAPL", quotes, universe, Risk.TradingState.Active, new DateTimeOffset(2026, 6, 6, 15, 0, 0, TimeSpan.Zero));
        Assert.False(blockedOutsideHours.Approved);
        Assert.Contains(blockedOutsideHours.Violations, v => v.Code == Risk.RiskViolationCode.OutsideTradingHours);

        var missingQuoteDecision = MarketEventPipeline.EvaluateShadowHardRisk(
            proposal, "MSFT", quotes, universe, Risk.TradingState.Active, now);
        Assert.False(missingQuoteDecision.Approved);
        Assert.Equal(0, missingQuoteDecision.CalculatedQuantity);
        Assert.Null(missingQuoteDecision.ClientOrderId);
        Assert.Contains(missingQuoteDecision.Violations, v => v.Code == Risk.RiskViolationCode.InsufficientBuyingPower);

        var haltedDecision = MarketEventPipeline.EvaluateShadowHardRisk(
            proposal, "AAPL", quotes, universe, MarketEventPipeline.ParsePersistedTradingState("HALTED_MANUAL"), now);
        Assert.False(haltedDecision.Approved);
        Assert.Equal(Risk.TradingState.HaltedManual, haltedDecision.EffectiveTradingState);
        Assert.Contains(haltedDecision.Violations, v => v.Code == Risk.RiskViolationCode.KillSwitchActive);

        Assert.Equal(Risk.TradingState.HaltedDailyLoss, MarketEventPipeline.ParsePersistedTradingState("HALTED_DAILY_LOSS"));
        Assert.Equal(Risk.TradingState.HaltedSystemError, MarketEventPipeline.ParsePersistedTradingState(null));
        Assert.Equal(Risk.TradingState.HaltedSystemError, MarketEventPipeline.ParsePersistedTradingState("UNKNOWN_STATE"));
    }

    [Fact]
    public async Task ScheduledEventsSyncWorkerRejectsMissingEarningsArrayAndPreservesEconomicOnEmptyObject()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var now = new DateTimeOffset(2026, 6, 4, 14, 0, 0, TimeSpan.Zero);
        using var pipeline = new DummyPipeline();

        using var badEarningsWorker = new ScheduledEventsSyncWorker(
            new FinnhubOptions("finnhub-key"),
            universe,
            pipeline,
            new ScheduledEventStore(new ThrowingDbFactory()),
            new HttpClient(new CustomFinnhubHandler("""{}""", """{"economicCalendar":[]}""")),
            new FixedClock(now),
            NullLogger<ScheduledEventsSyncWorker>.Instance);

        await Assert.ThrowsAsync<JsonException>(() => badEarningsWorker.FetchSnapshotAsync(CancellationToken.None));

        using var emptyMacroObjectWorker = new ScheduledEventsSyncWorker(
            new FinnhubOptions("finnhub-key"),
            universe,
            pipeline,
            new ScheduledEventStore(new ThrowingDbFactory()),
            new HttpClient(new CustomFinnhubHandler(
                """{"earningsCalendar":[{"symbol":"AAPL","date":"2026-06-05","hour":"amc"}]}""",
                """{}""")),
            new FixedClock(now),
            NullLogger<ScheduledEventsSyncWorker>.Instance);

        var snapshotMissingMacroArray = await emptyMacroObjectWorker.FetchSnapshotAsync(CancellationToken.None);
        Assert.False(snapshotMissingMacroArray.IncludesEconomicCalendar);
        Assert.Single(snapshotMissingMacroArray.Events);

        using var explicitEmptyMacroArrayWorker = new ScheduledEventsSyncWorker(
            new FinnhubOptions("finnhub-key"),
            universe,
            pipeline,
            new ScheduledEventStore(new ThrowingDbFactory()),
            new HttpClient(new CustomFinnhubHandler(
                """{"earningsCalendar":[{"symbol":"AAPL","date":"2026-06-05","hour":"amc"}]}""",
                """{"economicCalendar":[]}""")),
            new FixedClock(now),
            NullLogger<ScheduledEventsSyncWorker>.Instance);

        var snapshotEmptyMacroArray = await explicitEmptyMacroArrayWorker.FetchSnapshotAsync(CancellationToken.None);
        Assert.True(snapshotEmptyMacroArray.IncludesEconomicCalendar);
    }

    private sealed class CustomFinnhubHandler(string earningsJson, string economicJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = uri.Contains("/calendar/earnings", StringComparison.Ordinal)
                ? earningsJson
                : economicJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task ScheduledEventsSyncWorkerFetchSnapshotTracksHalfOpenWindowAndEconomicCoverage()
    {
        var universe = UniverseConfigLoader.Load(UniverseAndXTests.UniversePath);
        var now = new DateTimeOffset(2026, 6, 4, 14, 0, 0, TimeSpan.Zero); // 10:00 ET
        var http = new HttpClient(new StubFinnhubHandler(includeEconomic: false));
        using var pipeline = new DummyPipeline();
        using var worker = new ScheduledEventsSyncWorker(
            new FinnhubOptions("finnhub-key"),
            universe,
            pipeline,
            new ScheduledEventStore(new ThrowingDbFactory()),
            http,
            new FixedClock(now),
            NullLogger<ScheduledEventsSyncWorker>.Instance);

        var snapshotForbidden = await worker.FetchSnapshotAsync(CancellationToken.None);
        Assert.False(snapshotForbidden.IncludesEconomicCalendar);
        Assert.Equal(new DateTimeOffset(2026, 6, 4, 4, 0, 0, TimeSpan.Zero), snapshotForbidden.FromDateUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 18, 4, 0, 0, TimeSpan.Zero), snapshotForbidden.ToDateUtc);
        var earnings = Assert.Single(snapshotForbidden.Events);
        Assert.Equal("AAPL", earnings.Ticker);

        using var workerWithMacro = new ScheduledEventsSyncWorker(
            new FinnhubOptions("finnhub-key"),
            universe,
            pipeline,
            new ScheduledEventStore(new ThrowingDbFactory()),
            new HttpClient(new StubFinnhubHandler(includeEconomic: true)),
            new FixedClock(now),
            NullLogger<ScheduledEventsSyncWorker>.Instance);

        var snapshotWithMacro = await workerWithMacro.FetchSnapshotAsync(CancellationToken.None);
        Assert.True(snapshotWithMacro.IncludesEconomicCalendar);
        Assert.Equal(2, snapshotWithMacro.Events.Count);
        Assert.Contains(snapshotWithMacro.Events, e => e.EventType == "CPI" && e.Ticker is null);
    }

    private sealed class StubFinnhubHandler(bool includeEconomic) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (uri.Contains("/calendar/earnings", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"earningsCalendar":[{"symbol":"AAPL","date":"2026-06-05","hour":"amc"}]}"""));
            }

            if (uri.Contains("/calendar/economic", StringComparison.Ordinal))
            {
                return Task.FromResult(includeEconomic
                    ? JsonResponse("""{"economicCalendar":[{"country":"US","event":"Consumer Price Index","time":"2026-06-10 12:30:00"}]}""")
                    : new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DummyPipeline : IMarketEventPipeline, IDisposable
    {
        public bool IsConfigured => false;
        public Task EnsureReadyAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token) => Task.FromResult(0);
        public Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<SentiXDbContext>
    {
        public SentiXDbContext CreateDbContext() => throw new InvalidOperationException("No DB expected.");
    }
}
