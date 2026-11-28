using System.Net;
using System.Text;
using System.Text.Json;
using SentiX.Risk;

namespace SentiX.Execution.Tests;

public sealed class AlpacaPaperBrokerClientTests
{
    private static AlpacaBracketOrderRequest SampleBracketOrder(string clientOrderId = "SX-20261127-AAPL-BUY-01234567") => new(
        ClientOrderId: clientOrderId,
        Symbol: "AAPL",
        Quantity: 10,
        Side: TradeAction.Buy,
        OrderType: ExecutionOrderType.Limit,
        TimeInForce: ExecutionTimeInForce.Day,
        OrderClass: ExecutionOrderClass.Bracket,
        LimitPrice: 150.00m,
        StopLossPrice: 145.50m,
        TakeProfitPrice: 159.00m);

    [Theory]
    [InlineData("https://api.alpaca.markets")]
    [InlineData("http://paper-api.alpaca.markets")]
    [InlineData("https://paper-api.alpaca.markets@api.alpaca.markets")]
    [InlineData("not-a-uri")]
    public async Task NonPaperBaseUrl_NeverDispatchesHttp(string invalidBaseUrl)
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson("SX-20261127-AAPL-BUY-01234567")));
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.BaseUrl = invalidBaseUrl;
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), options);

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionRejectionCode.InvalidBaseUrl, result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task InjectedHttpClient_WithLiveBaseAddress_IsRejectedBeforeDispatch()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson("SX-20261127-AAPL-BUY-01234567")));
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.alpaca.markets/")
        };
        var client = new AlpacaPaperBrokerClient(httpClient, ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionRejectionCode.InvalidBaseUrl, result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TradingDisabled_NeverDispatchesHttp()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson("SX-20261127-AAPL-BUY-01234567")));
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.TradingEnabled = false;
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), options);

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionRejectionCode.TradingDisabled, result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Conflict409_MapsToDuplicateOrder_WithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.Conflict, """{"code":40910000,"message":"client_order_id already exists"}"""));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsDuplicate);
        Assert.False(result.IsRetryable);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(ExecutionRejectionCode.BrokerDuplicateOrder, result.FailureCode);
    }

    [Fact]
    public async Task UnprocessableEntity422_DuplicateClientOrderId_MapsToDuplicateOrder()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.UnprocessableEntity, """{"code":40010001,"message":"client_order_id must be unique"}"""));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsDuplicate);
        Assert.False(result.IsRetryable);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
        Assert.Equal(ExecutionRejectionCode.BrokerDuplicateOrder, result.FailureCode);
    }

    [Fact]
    public async Task UnprocessableEntity422_NonDuplicateValidation_MapsToBrokerRejected()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.UnprocessableEntity, """{"code":40310000,"message":"insufficient buying power"}"""));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.False(result.IsRetryable);
        Assert.Equal(ExecutionRejectionCode.BrokerRejected, result.FailureCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ExecutionRejectionCode.BrokerRateLimited, true)]
    [InlineData(HttpStatusCode.InternalServerError, ExecutionRejectionCode.BrokerServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, ExecutionRejectionCode.BrokerServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ExecutionRejectionCode.BrokerServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, ExecutionRejectionCode.BrokerRejected, false)]
    [InlineData(HttpStatusCode.Forbidden, ExecutionRejectionCode.BrokerRejected, false)]
    [InlineData(HttpStatusCode.BadRequest, ExecutionRejectionCode.BrokerRejected, false)]
    [InlineData(HttpStatusCode.Redirect, ExecutionRejectionCode.BrokerRejected, false)]
    public async Task HttpErrorStatuses_FailClosedCleanly_WithoutLeakingCredentials(
        HttpStatusCode status,
        ExecutionRejectionCode expectedCode,
        bool expectedRetryable)
    {
        const string rawBodyMarker = "RAW_BROKER_SECRET_ECHO_MARKER";
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            status,
            $$$"""{"message":"{{{rawBodyMarker}}} {{{ExecutionTestFixtures.TestSecretKey}}}"}"""));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.Equal(expectedRetryable, result.IsRetryable);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Equal(status, result.StatusCode);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain(ExecutionTestFixtures.TestSecretKey, result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(ExecutionTestFixtures.TestApiKey, result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(rawBodyMarker, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpErrors_NeverSurfaceRawBodyOrEchoedSecrets_AndAllowOnlyKnownNumericCodes()
    {
        const string sensitiveMarker = "ECHOED_AUTH_HEADER_SENSITIVE_MARKER_9911";
        const string syntheticSlugSecret = "secret_slug_token_1234";

        var withKnownNumericCodeHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            $$$"""{"code":40310000,"message":"echo {{{sensitiveMarker}}} {{{ExecutionTestFixtures.TestSecretKey}}}"}"""));
        var withSlugSecretHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            $$$"""{"code":"{{{syntheticSlugSecret}}}","message":"raw {{{sensitiveMarker}}}"}"""));
        var withUnknownNumericCodeHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"code":999999999999,"message":"unrecognized code"}"""));

        var numericClient = new AlpacaPaperBrokerClient(new HttpClient(withKnownNumericCodeHandler), ExecutionTestFixtures.ReadyPaperOptions());
        var slugClient = new AlpacaPaperBrokerClient(new HttpClient(withSlugSecretHandler), ExecutionTestFixtures.ReadyPaperOptions());
        var unknownNumericClient = new AlpacaPaperBrokerClient(new HttpClient(withUnknownNumericCodeHandler), ExecutionTestFixtures.ReadyPaperOptions());

        var numericResult = await numericClient.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);
        var slugResult = await slugClient.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);
        var unknownNumericResult = await unknownNumericClient.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(numericResult.IsSuccess);
        Assert.Equal("HTTP 422 order rejected by Alpaca Paper API (alpaca_code=40310000).", numericResult.FailureReason);
        Assert.DoesNotContain(sensitiveMarker, numericResult.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(ExecutionTestFixtures.TestSecretKey, numericResult.FailureReason, StringComparison.Ordinal);

        Assert.False(slugResult.IsSuccess);
        Assert.Equal("HTTP 400 rejected by Alpaca Paper API.", slugResult.FailureReason);
        Assert.DoesNotContain(syntheticSlugSecret, slugResult.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, slugResult.FailureReason, StringComparison.Ordinal);

        Assert.False(unknownNumericResult.IsSuccess);
        Assert.Equal("HTTP 400 rejected by Alpaca Paper API.", unknownNumericResult.FailureReason);
        Assert.DoesNotContain("999999999999", unknownNumericResult.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_FailsClosedAsRetryableTimeout()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(30);
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), options);

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRetryable);
        Assert.Equal(ExecutionRejectionCode.BrokerTimeout, result.FailureCode);
    }

    [Fact]
    public async Task NetworkAndStreamErrors_FailClosedAsRetryableNetworkError()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("DNS failure"));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRetryable);
        Assert.Equal(ExecutionRejectionCode.BrokerNetworkError, result.FailureCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{not-json}")]
    [InlineData("""{"id":"ord_1","symbol":"AAPL","status":"accepted"}""")]
    [InlineData("""{"id":"","client_order_id":"SX-20261127-AAPL-BUY-01234567","symbol":"AAPL","status":"accepted"}""")]
    [InlineData("""{"id":"ord_1","client_order_id":"SX-DIFFERENT","symbol":"AAPL","status":"accepted"}""")]
    [InlineData("""{"id":"ord_1","client_order_id":"SX-20261127-AAPL-BUY-01234567","symbol":"","status":"accepted"}""")]
    [InlineData("""{"id":"ord_1","client_order_id":"SX-20261127-AAPL-BUY-01234567","symbol":"MSFT","status":"accepted"}""")]
    public async Task MalformedNullOrMismatchedBrokerJson_FailsClosed(string responseBody)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, responseBody));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRetryable);
        Assert.Equal(ExecutionRejectionCode.MalformedBrokerResponse, result.FailureCode);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task ReceiptSymbol_MatchesCaseInsensitively_AndNormalizesToUpper()
    {
        const string clientOrderId = "SX-20261127-AAPL-BUY-01234567";
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson(clientOrderId, symbol: "aapl")));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(clientOrderId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Receipt);
        Assert.Equal("AAPL", result.Receipt.Symbol);
    }

    [Fact]
    public async Task OversizedBrokerResponse_FailsClosed()
    {
        var oversized = new string('x', AlpacaPaperBrokerClient.MaxResponseBytes + 128);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        });
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ExecutionRejectionCode.MalformedBrokerResponse, result.FailureCode);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesOperationCanceledException()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson("SX-20261127-AAPL-BUY-01234567")));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SubmitOrderAsync(SampleBracketOrder(), cts.Token));
    }

    [Fact]
    public async Task HappyPath_SendsExactPaperBracketPayloadAndHeaders()
    {
        const string clientOrderId = "SX-20261127-AAPL-BUY-01234567";
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, ExecutionTestFixtures.AcceptedOrderJson(clientOrderId)));
        var client = new AlpacaPaperBrokerClient(new HttpClient(handler), ExecutionTestFixtures.ReadyPaperOptions());

        var result = await client.SubmitOrderAsync(SampleBracketOrder(clientOrderId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionRejectionCode.None, result.FailureCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(new Uri("https://paper-api.alpaca.markets/v2/orders"), handler.LastUri);
        Assert.Equal(ExecutionTestFixtures.TestApiKey, handler.LastHeaders["APCA-API-KEY-ID"]);
        Assert.Equal(ExecutionTestFixtures.TestSecretKey, handler.LastHeaders["APCA-API-SECRET-KEY"]);

        using var sentJson = JsonDocument.Parse(handler.LastBody!);
        var root = sentJson.RootElement;
        Assert.Equal("AAPL", root.GetProperty("symbol").GetString());
        Assert.Equal("10", root.GetProperty("qty").GetString());
        Assert.Equal("buy", root.GetProperty("side").GetString());
        Assert.Equal("limit", root.GetProperty("type").GetString());
        Assert.Equal("day", root.GetProperty("time_in_force").GetString());
        Assert.Equal("bracket", root.GetProperty("order_class").GetString());
        Assert.Equal(clientOrderId, root.GetProperty("client_order_id").GetString());
        Assert.Equal("150.00", root.GetProperty("limit_price").GetString());
        Assert.Equal("159.00", root.GetProperty("take_profit").GetProperty("limit_price").GetString());
        Assert.Equal("145.50", root.GetProperty("stop_loss").GetProperty("stop_price").GetString());

        Assert.NotNull(result.Receipt);
        Assert.Equal("ord_paper_001", result.Receipt.OrderId);
        Assert.Equal(clientOrderId, result.Receipt.ClientOrderId);
        Assert.Equal("accepted", result.Receipt.Status);
        Assert.Equal(150.00m, result.Receipt.LimitPrice);
    }
}
