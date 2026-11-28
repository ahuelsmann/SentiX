using System.Net;
using SentiX.Domain;
using SentiX.Risk;

namespace SentiX.Execution.Tests;

public sealed class PaperPortfolioSnapshotProviderTests
{
    private static readonly UniverseConfig TestUniverse = new(
        Version: 1,
        MaxEquitiesWsSymbols: 30,
        FastPath: new FastPathConfig(0.5, 90),
        BenchmarkTicker: "SPY",
        SectorEtfs: new Dictionary<string, string> { ["Technology"] = "XLK" },
        Watchlist:
        [
            new WatchlistEntry("AAPL", "Technology", ["Apple"]),
            new WatchlistEntry("NVDA", "Technology", ["Nvidia"])
        ],
        SocialAllowlist: []);

    [Fact]
    public async Task GetSnapshotAsync_ParsesAuthoritativeAccountPositionsAndOpenOrders_WithFractionalDailyPnL()
    {
        var seenUris = new List<Uri>();
        var handler = new StubHttpMessageHandler(req =>
        {
            seenUris.Add(req.RequestUri!);
            Assert.Equal(ExecutionTestFixtures.TestApiKey, Assert.Single(req.Headers.GetValues("APCA-API-KEY-ID")));
            Assert.Equal(ExecutionTestFixtures.TestSecretKey, Assert.Single(req.Headers.GetValues("APCA-API-SECRET-KEY")));

            return req.RequestUri!.PathAndQuery switch
            {
                "/v2/account" => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"equity":"97500.00","last_equity":"100000.00","buying_power":"85000.00","trading_blocked":false,"account_blocked":false}
                    """),
                "/v2/positions" => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    [{"symbol":"AAPL","qty":"20","market_value":"4000.00","current_price":"200.00"},
                     {"symbol":"XLK","qty":"10","market_value":"2100.00","current_price":"210.00"}]
                    """),
                "/v2/orders?status=open&limit=500" => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    [{"id":"ord-1","client_order_id":"SX-20261127-NVDA-BUY-12345678","symbol":"NVDA","side":"buy","qty":"15","limit_price":"125.50"}]
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var provider = new AlpacaPaperPortfolioSnapshotProvider(
            new HttpClient(handler),
            ExecutionTestFixtures.ReadyPaperOptions(),
            timeProvider: new FakeTimeProvider(ExecutionTestFixtures.FixedUtcNow));

        var result = await provider.GetSnapshotAsync(TestUniverse, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(3, seenUris.Count);
        Assert.All(seenUris, uri => Assert.Equal("paper-api.alpaca.markets", uri.Host));
        Assert.Equal(97_500m, result.Snapshot!.Equity);
        Assert.Equal(85_000m, result.Snapshot.BuyingPower);
        // Fractional -0.025 (-2.5%) matching HardRiskManager MaxDailyLossPct (-0.02)
        Assert.Equal(-0.025d, result.Snapshot.DailyPnLPct, precision: 6);

        Assert.Equal(2, result.Snapshot.OpenPositions.Count);
        Assert.Contains(result.Snapshot.OpenPositions, p => p.Ticker == "AAPL" && p.Sector == "Technology" && p.Quantity == 20 && p.MarketValue == 4_000m);
        Assert.Contains(result.Snapshot.OpenPositions, p => p.Ticker == "XLK" && p.Sector == "ETF" && p.Quantity == 10);

        var pending = Assert.Single(result.Snapshot.PendingOrders);
        Assert.Equal("SX-20261127-NVDA-BUY-12345678", pending.ClientOrderId);
        Assert.Equal("NVDA", pending.Ticker);
        Assert.Equal(SentiX.Risk.TradeAction.Buy, pending.Side);
        Assert.Equal(15, pending.Quantity);
        Assert.Equal(125.50m, result.Snapshot.PendingOrderLimitPrices!["SX-20261127-NVDA-BUY-12345678"]);
    }

    [Theory]
    [InlineData(false, "https://paper-api.alpaca.markets", ExecutionRejectionCode.TradingDisabled)]
    [InlineData(true, "https://api.alpaca.markets", ExecutionRejectionCode.InvalidBaseUrl)]
    [InlineData(true, "http://paper-api.alpaca.markets", ExecutionRejectionCode.InvalidBaseUrl)]
    public async Task GetSnapshotAsync_BlocksDisabledOrNonPaperUrl_WithZeroHttpCalls(
        bool tradingEnabled,
        string baseUrl,
        ExecutionRejectionCode expectedCode)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not call HTTP."));
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.TradingEnabled = tradingEnabled;
        options.BaseUrl = baseUrl;

        var provider = new AlpacaPaperPortfolioSnapshotProvider(new HttpClient(handler), options);
        var result = await provider.GetSnapshotAsync(TestUniverse, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000","trading_blocked":true}""", "[]", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000","account_blocked":true}""", "[]", "[]")]
    [InlineData("""{"equity":"0","last_equity":"100000","buying_power":"100000"}""", "[]", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"0","buying_power":"100000"}""", "[]", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000"}""", """[{"symbol":"AAPL","qty":"-5","market_value":"1000"}]""", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000"}""", """[{"symbol":"AAPL","qty":"1.5","market_value":"300"}]""", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000"}""", """[{"symbol":"ZZZZ","qty":"5","market_value":"500"}]""", "[]")]
    [InlineData("""{"equity":"100000","last_equity":"100000","buying_power":"100000"}""", "[]", """[{"id":"1","symbol":"AAPL","side":"buy","qty":"0.5"}]""")]
    public async Task GetSnapshotAsync_FailsClosedOnBlockedAccountShortOrFractionalState(
        string accountJson,
        string positionsJson,
        string ordersJson)
    {
        var handler = new StubHttpMessageHandler(req => req.RequestUri!.PathAndQuery switch
        {
            "/v2/account" => StubHttpMessageHandler.Json(HttpStatusCode.OK, accountJson),
            "/v2/positions" => StubHttpMessageHandler.Json(HttpStatusCode.OK, positionsJson),
            "/v2/orders?status=open&limit=500" => StubHttpMessageHandler.Json(HttpStatusCode.OK, ordersJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var provider = new AlpacaPaperPortfolioSnapshotProvider(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());
        var result = await provider.GetSnapshotAsync(TestUniverse, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Snapshot);
        Assert.Equal(ExecutionRejectionCode.MalformedBrokerResponse, result.FailureCode);
    }
}
