using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Domain;
using SentiX.Ingestion;

namespace SentiX.Integration.Tests;

public sealed class Phase2IngestionTests
{
    private static readonly UniverseConfig Universe = new(1, 30, new FastPathConfig(0.5, 90),
        "SPY", new Dictionary<string, string> { ["Technology"] = "XLK" },
        [new WatchlistEntry("AAPL", "Technology", ["Apple", "Apple Inc."])], []);

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("DISABLED")]
    public async Task SecSkipsNetworkWhenUserAgentIsDisabled(string? userAgent)
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected request.")));
        using var collector = new SecEdgarCollector(new SecOptions(userAgent), Universe, new RecordingPipeline(), http,
            TimeProvider.System, NullLogger<SecEdgarCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);
        await collector.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SecRetriesThenNormalizesAllowedFilings()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>8-K - Apple Inc. (0000320193) (Filer)</title>
                <category term="8-K"/>
                <id>urn:tag:sec.gov,2008:accession-number=0000320193-26-000001</id>
                <updated>2026-06-04T15:00:00-04:00</updated>
                <summary type="html">&lt;b&gt;Apple &amp;amp; partners&lt;/b&gt;</summary>
              </entry>
              <entry>
                <title>8-K - Unrelated Corp. (0000000001) (Filer)</title>
                <category term="8-K"/>
                <id>0000000001-26-000002</id>
                <updated>2026-06-04T15:00:00-04:00</updated>
              </entry>
              <entry>
                <title>10-K - Apple Inc. (0000320193) (Filer)</title>
                <category term="10-K"/>
                <id>0000320193-26-000003</id>
                <updated>2026-06-04T15:00:00-04:00</updated>
              </entry>
            </feed>
            """;
        var clock = new ImmediateDelayClock();
        var pipeline = new RecordingPipeline();
        var requests = 0;
        using var http = new HttpClient(new StubHandler(req =>
        {
            Assert.Equal(SecEdgarCollector.FeedUrl, req.RequestUri!.AbsoluteUri);
            Assert.Equal("SentiX test@example.com", Assert.Single(req.Headers.GetValues("User-Agent")));
            return ++requests switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                3 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) },
                _ => throw new InvalidOperationException("Unexpected extra request.")
            };
        }));
        using var collector = new SecEdgarCollector(new SecOptions("SentiX test@example.com"), Universe, pipeline, http,
            clock, NullLogger<SecEdgarCollector>.Instance);
        await collector.StartAsync(CancellationToken.None);
        var captured = await pipeline.FirstEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await collector.StopAsync(CancellationToken.None);

        Assert.Equal("sec-edgar-0000320193-26-000001", captured.Event.EventId);
        Assert.Equal(EventSource.SecEdgar, captured.Event.Source);
        Assert.Equal("SEC EDGAR", captured.Event.Author);
        Assert.Equal(new DateTimeOffset(2026, 6, 4, 19, 0, 0, TimeSpan.Zero), captured.Event.PublishedAtUtc);
        Assert.Equal(["AAPL"], captured.Event.SourceTickers);
        Assert.Equal("Apple & partners", captured.Event.Summary);
        using var raw = JsonDocument.Parse(captured.Raw);
        Assert.Equal("0000320193", raw.RootElement.GetProperty("cik").GetString());
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], clock.Delays.Take(2));
        Assert.Equal(1, pipeline.EventsRecorded);
    }

    [Theory]
    [InlineData(2026, 3, 7, 12, 3, 8, 10)]
    [InlineData(2026, 10, 31, 12, 11, 1, 11)]
    [InlineData(2026, 6, 4, 9, 6, 4, 10)]
    public void CalendarScheduleRespectsEasternDaylightSaving(int year, int month, int day, int hour,
        int expectedMonth, int expectedDay, int expectedHour)
    {
        var now = new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(year, expectedMonth, expectedDay, expectedHour, 0, 0, TimeSpan.Zero),
            ScheduledEventsSyncWorker.NextSyncUtc(now));
    }

    [Fact]
    public async Task CalendarFiltersToWatchedSymbolsAndUsMacroWithinFourteenDays()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new StubHandler(req =>
        {
            Assert.Equal("synthetic", Assert.Single(req.Headers.GetValues("X-Finnhub-Token")));
            Assert.Equal("?from=2026-06-04&to=2026-06-17", req.RequestUri!.Query);
            calls.Add(req.RequestUri.AbsolutePath);
            var json = req.RequestUri.AbsolutePath.EndsWith("/earnings", StringComparison.Ordinal)
                ? """
                  {"earningsCalendar":[{"symbol":"AAPL","date":"2026-06-04","hour":"amc"},
                    {"symbol":"AAPL","date":"2026-06-04","hour":"amc"},
                    {"symbol":"OTHER","date":"2026-06-04","hour":"bmo"},
                    {"symbol":"AAPL","date":"2026-06-18","hour":"bmo"}]}
                  """
                : """
                  {"economicCalendar":[{"country":"US","event":"Consumer Price Index","time":"2026-06-05 12:30:00"},
                    {"country":"CA","event":"CPI","time":"2026-06-05 12:30:00"}]}
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        using var worker = new ScheduledEventsSyncWorker(new FinnhubOptions("synthetic"), Universe,
            new RecordingPipeline(), null!, http,
            new FixedClock(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ScheduledEventsSyncWorker>.Instance);
        var events = await worker.FetchAsync(CancellationToken.None);

        Assert.Equal(["/api/v1/calendar/earnings", "/api/v1/calendar/economic"], calls);
        var earnings = Assert.Single(events, row => row.EventType == "EARNINGS");
        Assert.Equal("AAPL", earnings.Ticker);
        Assert.Equal(new DateTimeOffset(2026, 6, 4, 20, 0, 0, TimeSpan.Zero), earnings.ScheduledAtUtc);
        var cpi = Assert.Single(events, row => row.EventType == "CPI");
        Assert.Null(cpi.Ticker);
        Assert.Equal(new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero), cpi.ScheduledAtUtc);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void QuotesIgnoreInvalidAndUnsubscribedFramesAndDoNotReplaceNewerQuotes()
    {
        using var json = JsonDocument.Parse("""
            [{"T":"q","S":"AAPL","bp":99,"ap":101,"t":"2026-06-04T12:00:00.123456789Z"},
             {"T":"q","S":"AAPL","bp":1,"ap":2,"t":"2026-06-04T11:00:00Z"},
             {"T":"q","S":"SPY","bp":0,"ap":1,"t":"2026-06-04T12:00:00Z"},
             {"T":"q","S":"XLK","bp":2,"ap":1,"t":"2026-06-04T12:00:00Z"},
             {"T":"q","S":"XLK","bp":"2","ap":3,"t":"2026-06-04T12:00:00Z"},
             {"T":"q","S":"OTHER","bp":1,"ap":2,"t":"2026-06-04T12:00:00Z"},
             {"T":"q","S":"XLK","bp":1,"ap":2,"t":"bad"}]
            """);
        var cache = new InMemoryMarketQuoteCache();
        var symbols = Universe.EquitiesSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, AlpacaQuotesCollector.ApplyQuotes(json.RootElement, symbols, cache));
        Assert.True(cache.TryGet("aapl", out var quote));
        Assert.NotNull(quote);
        Assert.Equal(99m, quote.BidPrice);
        Assert.Equal(101m, quote.AskPrice);
        Assert.Equal(100m, quote.MidPrice);
        Assert.Equal(200d, quote.SpreadBps);
        Assert.False(cache.TryGet("SPY", out _));
        Assert.False(cache.TryGet("XLK", out _));
        Assert.False(cache.TryGet("OTHER", out _));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class RecordingPipeline : IMarketEventPipeline
    {
        public TaskCompletionSource<(MarketEvent Event, string Raw)> FirstEvent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EventsRecorded { get; private set; }
        public bool IsConfigured => true;
        public Task EnsureReadyAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token) => Task.FromResult(0);

        public Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token)
        {
            EventsRecorded++;
            FirstEvent.TrySetResult((marketEvent, rawPayload));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ImmediateDelayClock : TimeProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Enqueue(dueTime);
            // Run the first two retries quickly. After a successful poll, park until the test stops the worker.
            var due = Delays.Count <= 2 ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;
            return TimeProvider.System.CreateTimer(callback, state, due, period);
        }
    }
}
