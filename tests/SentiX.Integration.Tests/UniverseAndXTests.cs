using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.Ingestion;
using SentiX.NativeBridge;

namespace SentiX.Integration.Tests;

public sealed class UniverseAndXTests
{
    internal static string UniversePath => Path.Combine(AppContext.BaseDirectory, "universe.json");

    [Fact]
    public void LoadsCanonicalConfigAndProducesOnlyAllowlistedXRules()
    {
        var config = UniverseConfigLoader.Load(UniversePath);
        Assert.Equal(19, config.EquitiesSymbols.Count);
        Assert.Equal("(from:realDonaldTrump OR from:WhiteHouse OR from:federalreserve OR from:SECGov OR from:USTreasury) -is:retweet -is:reply",
            XRuleBuilder.Build(config));

        var entry = new SocialAllowlistEntry(EventSource.XAllowlist, "@whitehouse", TrustTier.Primary);
        Assert.Equal(XRuleBuilder.Build(config), XRuleBuilder.Build(config with
        {
            SocialAllowlist = [.. config.SocialAllowlist, entry]
        }));
        Assert.Null(XRuleBuilder.Build(config with { SocialAllowlist = [] }));
        Assert.Throws<InvalidDataException>(() => XRuleBuilder.Build(config with
        {
            SocialAllowlist = [entry with { Identifier = "whitehouse OR cats" }]
        }));
    }

    [Theory]
    [InlineData(30, 30, true)]
    [InlineData(31, 30, false)]
    [InlineData(30, 29, false)]
    [InlineData(31, 31, false)]
    public void EnforcesDistinctEquitiesLimit(int count, int configuredLimit, bool valid)
    {
        var config = UniverseConfigLoader.Load(UniversePath) with
        {
            MaxEquitiesWsSymbols = configuredLimit,
            BenchmarkTicker = "Spy",
            SectorEtfs = new Dictionary<string, string> { ["TECH"] = "XLK" },
            Watchlist = Enumerable.Range(1, count - 2)
                .Select(i => new WatchlistEntry($"T{i}", "TECH", []))
                .Concat([new("xlk", "TECH", []), new("spy", "TECH", [])])
                .ToArray()
        };

        Assert.Equal(count, config.EquitiesSymbols.Count);
        if (valid)
        {
            UniverseConfigValidator.Validate(config);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => UniverseConfigValidator.Validate(config));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingXTokenLogsInformationalSkipWithoutHttpOrDatabase(string? token)
    {
        var logger = new XLogger();
        using var bridge = new NativeBridgeClient();
        using var pipeline = new MarketEventPipeline(new RejectingDbFactory(), bridge,
            NullLogger<MarketEventPipeline>.Instance, isConfigured: true);
        using var http = new HttpClient(new RejectingHandler());
        using var worker = new XIngestionWorker(new XOptions(token), UniverseConfigLoader.Load(UniversePath),
            pipeline, http, TimeProvider.System, logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(deadline.Token);
        await worker.ExecuteTask!.WaitAsync(deadline.Token);

        Assert.Equal(LogLevel.Information, logger.Level);
        Assert.Contains("skipped because the bearer token is missing", logger.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DailyCapResumesAtMidnightUtcAndHonorsPersistedCount()
    {
        var budget = new XDailyPostBudget();
        var februaryFirst = new DateTimeOffset(2026, 2, 1, 23, 59, 0, TimeSpan.Zero);
        budget.StartDay(februaryFirst, 148);
        Assert.Equal(150, budget.DailyLimit);
        Assert.Equal(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero), budget.ResetsAtUtc);

        budget.CountPost();
        Assert.False(budget.IsExhausted);
        budget.CountPost();
        Assert.True(budget.IsExhausted);
        budget.CountPost();
        Assert.Equal(150, budget.PostsSeen);

        budget.StartDay(februaryFirst.AddMinutes(1), 0);
        Assert.False(budget.IsExhausted);
        Assert.Equal(0, budget.PostsSeen);
        Assert.True(new XDailyPostBudget(0).IsExhausted);
    }

    [Fact]
    public async Task WorkerDisconnectsAtCapBeforePersistingTheLastAllowedPost()
    {
        var config = UniverseConfigLoader.Load(UniversePath);
        using var handler = new FilteredStreamHandler(XRuleBuilder.Build(config)!);
        using var http = new HttpClient(handler);
        var pipeline = new RecordingPipeline(() => Assert.True(handler.Stream.Disconnected));
        using var worker = new XIngestionWorker(new XOptions("test-token", 1), config, pipeline,
            http, TimeProvider.System, NullLogger<XIngestionWorker>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(deadline.Token);
        var marketEvent = await pipeline.Received.Task.WaitAsync(deadline.Token);
        await worker.StopAsync(deadline.Token);

        Assert.Equal(1, handler.StreamRequests);
        Assert.True(handler.Stream.Disconnected);
        Assert.Equal(1, pipeline.Processed);
        Assert.Equal("x:1", marketEvent.EventId);
        Assert.Equal("@WhiteHouse", marketEvent.Author);
        Assert.Equal("First post", marketEvent.Headline);
    }

    private sealed class FilteredStreamHandler(string rule) : HttpMessageHandler
    {
        public int StreamRequests { get; private set; }
        public TrackingStream Stream { get; } = new("""
            {"data":{"id":"1","author_id":"8","text":"First post","created_at":"2026-02-02T09:00:00Z"},"includes":{"users":[{"id":"8","username":"WhiteHouse"}]}}
            {"data":{"id":"2","author_id":"8","text":"Second post","created_at":"2026-02-02T09:00:00Z"},"includes":{"users":[{"id":"8","username":"WhiteHouse"}]}}
            """);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/rules", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { data = new[] { new { id = "r1", value = rule, tag = "sentix-allowlist" } } })
                });
            }

            StreamRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Stream) });
        }
    }

    private sealed class TrackingStream(string content) : MemoryStream(Encoding.UTF8.GetBytes(content))
    {
        public bool Disconnected { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disconnected = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingPipeline(Action onProcess) : IMarketEventPipeline
    {
        public bool IsConfigured => true;
        public int Processed { get; private set; }
        public TaskCompletionSource<MarketEvent> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureReadyAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token) => Task.FromResult(0);

        public Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token)
        {
            onProcess();
            Processed++;
            Received.TrySetResult(marketEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }

    private sealed class RejectingDbFactory : IDbContextFactory<SentiXDbContext>
    {
        public SentiXDbContext CreateDbContext() => throw new InvalidOperationException("No database access expected.");
    }

    private sealed class XLogger : ILogger<XIngestionWorker>
    {
        public LogLevel Level { get; private set; }
        public string Message { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
        }
    }
}
