using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Risk;

namespace SentiX.Execution;

/// <summary>
/// HTTP client for Alpaca Paper REST API (<c>https://paper-api.alpaca.markets/v2/orders</c>, Spec 008).
/// Fails closed without throwing on invalid endpoints, timeouts, HTTP 409/422/429/5xx, network errors,
/// oversized payloads, or malformed/null broker JSON.
/// </summary>
public sealed class AlpacaPaperBrokerClient : IAlpacaPaperBrokerClient
{
    /// <summary>Hard cap on broker response payload size (256 KiB).</summary>
    public const int MaxResponseBytes = 256 * 1024;

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    private readonly HttpClient _httpClient;
    private readonly AlpacaPaperOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new <see cref="AlpacaPaperBrokerClient"/>.</summary>
    public AlpacaPaperBrokerClient(
        HttpClient httpClient,
        AlpacaPaperOptions options,
        ILogger<AlpacaPaperBrokerClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<BrokerSubmissionResult> SubmitOrderAsync(
        AlpacaBracketOrderRequest order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.TryValidateReadyToTrade(out var optionsCode, out var optionsError))
        {
            return Fail(optionsCode, optionsError, statusCode: null, isDuplicate: false, isRetryable: false);
        }

        if (_httpClient.BaseAddress is not null && !AlpacaPaperOptions.IsAllowedPaperBaseUri(_httpClient.BaseAddress))
        {
            return Fail(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Injected HttpClient BaseAddress is not strictly https://paper-api.alpaca.markets.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: false);
        }

        if (!AlpacaPaperOptions.TryResolvePaperBaseUri(_options.BaseUrl, out var baseUri))
        {
            return Fail(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Alpaca BaseUrl failed strict paper endpoint resolution.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: false);
        }

        var requestUri = new Uri(baseUri, "v2/orders");
        if (!AlpacaPaperOptions.IsAllowedPaperOrdersUri(requestUri))
        {
            return Fail(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Resolved order URI is not strictly https://paper-api.alpaca.markets/v2/orders.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: false);
        }

        if (!ValidateOrderPayload(order, out var payloadCode, out var payloadError))
        {
            return Fail(payloadCode, payloadError, statusCode: null, isDuplicate: false, isRetryable: false);
        }

        var jsonPayload = BuildOrderJson(order);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", _options.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", _options.SecretKey.Trim());
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, JsonMediaType);

        // Final dispatch guard directly on HttpRequestMessage.RequestUri before sending.
        if (!AlpacaPaperOptions.IsAllowedPaperOrdersUri(request.RequestUri))
        {
            return Fail(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Final pre-dispatch RequestUri guard rejected non-paper URI.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: false);
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            var bodyResult = await ReadCappedBodyAsync(response.Content, linkedCts.Token).ConfigureAwait(false);
            if (!bodyResult.Success)
            {
                return Fail(
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    bodyResult.Error ?? "Broker response exceeded size limit.",
                    response.StatusCode,
                    isDuplicate: false,
                    isRetryable: false);
            }

            return MapResponse(response.StatusCode, bodyResult.Body!, order.ClientOrderId, order.Symbol.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Alpaca Paper order submission timed out for {ClientOrderId}.", order.ClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerTimeout,
                "Alpaca Paper API request timed out.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: true);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Alpaca Paper order submission timed out for {ClientOrderId}.", order.ClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerTimeout,
                "Alpaca Paper API request timed out.",
                statusCode: null,
                isDuplicate: false,
                isRetryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Alpaca Paper network error for {ClientOrderId}: {ErrorType}.", order.ClientOrderId, ex.GetType().Name);
            return Fail(
                ExecutionRejectionCode.BrokerNetworkError,
                $"Alpaca Paper API network failure ({ex.GetType().Name}).",
                statusCode: ex.StatusCode,
                isDuplicate: false,
                isRetryable: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Alpaca Paper stream error for {ClientOrderId}: {ErrorType}.", order.ClientOrderId, ex.GetType().Name);
            return Fail(
                ExecutionRejectionCode.BrokerNetworkError,
                $"Alpaca Paper API stream failure ({ex.GetType().Name}).",
                statusCode: null,
                isDuplicate: false,
                isRetryable: true);
        }
    }

    /// <inheritdoc />
    public async Task<BrokerOrderLookupResult> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(clientOrderId) ||
            clientOrderId.Trim().Length > ClientOrderIdGenerator.MaxLength ||
            clientOrderId.Trim().Any(static ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.InvalidClientOrderId,
                "client_order_id is missing or invalid for broker lookup.",
                null);
        }

        if (!_options.TryValidateReadyToTrade(out var optionsCode, out var optionsError))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                optionsCode,
                optionsError,
                null);
        }

        if (_httpClient.BaseAddress is not null && !AlpacaPaperOptions.IsAllowedPaperBaseUri(_httpClient.BaseAddress))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.InvalidBaseUrl,
                "Injected HttpClient BaseAddress is not strictly https://paper-api.alpaca.markets.",
                null);
        }

        if (!AlpacaPaperOptions.TryResolvePaperBaseUri(_options.BaseUrl, out var baseUri))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.InvalidBaseUrl,
                "Alpaca BaseUrl failed strict paper endpoint resolution.",
                null);
        }

        var trimmedId = clientOrderId.Trim();
        var expectedPathAndQuery = $"/v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(trimmedId)}";
        var requestUri = new Uri(baseUri, expectedPathAndQuery.TrimStart('/'));
        if (!IsAllowedPaperOrderLookupUri(requestUri, expectedPathAndQuery))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.InvalidBaseUrl,
                "Resolved order lookup URI failed strict paper endpoint validation.",
                null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", _options.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", _options.SecretKey.Trim());

        if (!IsAllowedPaperOrderLookupUri(request.RequestUri, expectedPathAndQuery))
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.InvalidBaseUrl,
                "Final pre-dispatch lookup RequestUri guard rejected non-paper URI.",
                null);
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.NotFound,
                    HttpStatusCode.NotFound,
                    ExecutionRejectionCode.None,
                    null,
                    null);
            }

            var code = (int)response.StatusCode;
            if (!IsSuccessStatusCode(code))
            {
                var failureCode = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? ExecutionRejectionCode.BrokerRateLimited
                    : code >= 500
                        ? ExecutionRejectionCode.BrokerServerError
                        : ExecutionRejectionCode.BrokerRejected;
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.AmbiguousFailure,
                    response.StatusCode,
                    failureCode,
                    FormattableString.Invariant($"HTTP {code} during order lookup."),
                    null);
            }

            var bodyResult = await ReadCappedBodyAsync(response.Content, linkedCts.Token).ConfigureAwait(false);
            if (!bodyResult.Success || string.IsNullOrWhiteSpace(bodyResult.Body))
            {
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.AmbiguousFailure,
                    response.StatusCode,
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    bodyResult.Error ?? "Empty broker lookup response.",
                    null);
            }

            return ParseLookupReceipt(response.StatusCode, bodyResult.Body, trimmedId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning("Alpaca Paper order lookup timed out for {ClientOrderId}.", trimmedId);
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.BrokerTimeout,
                "Alpaca Paper order lookup timed out.",
                null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Alpaca Paper order lookup network error for {ClientOrderId}: {ErrorType}.", trimmedId, ex.GetType().Name);
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                ex.StatusCode,
                ExecutionRejectionCode.BrokerNetworkError,
                $"Alpaca Paper order lookup network failure ({ex.GetType().Name}).",
                null);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Alpaca Paper order lookup stream error for {ClientOrderId}: {ErrorType}.", trimmedId, ex.GetType().Name);
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                null,
                ExecutionRejectionCode.BrokerNetworkError,
                $"Alpaca Paper order lookup stream failure ({ex.GetType().Name}).",
                null);
        }
    }

    internal static bool IsAllowedPaperOrderLookupUri(Uri? uri, string expectedPathAndQuery)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.Equals(uri.Host, AlpacaPaperOptions.AllowedPaperHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, AlpacaPaperOptions.AllowedPaperHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/v2/orders:by_client_order_id", StringComparison.Ordinal) &&
               string.Equals(uri.PathAndQuery, expectedPathAndQuery, StringComparison.Ordinal);
    }

    private static BrokerOrderLookupResult ParseLookupReceipt(
        HttpStatusCode statusCode,
        string body,
        string expectedClientOrderId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.AmbiguousFailure,
                    statusCode,
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    "Broker lookup returned non-object JSON.",
                    null);
            }

            var root = doc.RootElement;
            if (!TryReadRequiredString(root, "id", out var orderId) ||
                !TryReadRequiredString(root, "client_order_id", out var clientOrderId) ||
                !TryReadRequiredString(root, "symbol", out var symbol) ||
                !TryReadRequiredString(root, "status", out var status) ||
                !string.Equals(clientOrderId, expectedClientOrderId, StringComparison.Ordinal))
            {
                return new BrokerOrderLookupResult(
                    BrokerOrderLookupOutcome.AmbiguousFailure,
                    statusCode,
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    "Broker lookup receipt is missing required fields or has mismatched client_order_id.",
                    null);
            }

            var receipt = new AlpacaOrderReceipt(
                orderId,
                clientOrderId,
                symbol.ToUpperInvariant(),
                status,
                ReadOptionalString(root, "side") ?? string.Empty,
                ReadOptionalString(root, "type") ?? string.Empty,
                ReadOptionalString(root, "order_class"),
                ReadOptionalString(root, "qty"),
                ReadOptionalDecimal(root, "limit_price"),
                ReadOptionalDecimal(root, "filled_avg_price"),
                ReadOptionalTimestamp(root, "submitted_at"),
                ReadOptionalTimestamp(root, "created_at"));

            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.Found,
                statusCode,
                ExecutionRejectionCode.None,
                null,
                receipt);
        }
        catch (JsonException)
        {
            return new BrokerOrderLookupResult(
                BrokerOrderLookupOutcome.AmbiguousFailure,
                statusCode,
                ExecutionRejectionCode.MalformedBrokerResponse,
                "Broker lookup returned malformed JSON.",
                null);
        }
    }

    private BrokerSubmissionResult MapResponse(
        HttpStatusCode statusCode,
        string body,
        string expectedClientOrderId,
        string expectedSymbol)
    {
        var code = (int)statusCode;

        if (statusCode == HttpStatusCode.Conflict)
        {
            _logger.LogInformation("Alpaca Paper reported HTTP 409 duplicate for {ClientOrderId}.", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerDuplicateOrder,
                FormatSafeErrorReason(statusCode, "duplicate client_order_id", body),
                statusCode,
                isDuplicate: true,
                isRetryable: false);
        }

        if (statusCode == HttpStatusCode.UnprocessableEntity)
        {
            if (IsDuplicateClientOrderBody(body))
            {
                _logger.LogInformation("Alpaca Paper reported HTTP 422 duplicate client_order_id for {ClientOrderId}.", expectedClientOrderId);
                return Fail(
                    ExecutionRejectionCode.BrokerDuplicateOrder,
                    FormatSafeErrorReason(statusCode, "duplicate client_order_id", body),
                    statusCode,
                    isDuplicate: true,
                    isRetryable: false);
            }

            _logger.LogWarning("Alpaca Paper rejected order {ClientOrderId} with HTTP 422.", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerRejected,
                FormatSafeErrorReason(statusCode, "order rejected by Alpaca Paper API", body),
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Alpaca Paper rate limited order {ClientOrderId} (HTTP 429).", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerRateLimited,
                FormatSafeErrorReason(statusCode, "rate limit exceeded on Alpaca Paper API", body),
                statusCode,
                isDuplicate: false,
                isRetryable: true);
        }

        if (code >= 500)
        {
            _logger.LogWarning("Alpaca Paper server error HTTP {StatusCode} for {ClientOrderId}.", code, expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerServerError,
                FormatSafeErrorReason(statusCode, "server error from Alpaca Paper API", body),
                statusCode,
                isDuplicate: false,
                isRetryable: true);
        }

        if (!IsSuccessStatusCode(code))
        {
            _logger.LogWarning("Alpaca Paper rejected order {ClientOrderId} with HTTP {StatusCode}.", expectedClientOrderId, code);
            return Fail(
                ExecutionRejectionCode.BrokerRejected,
                FormatSafeErrorReason(statusCode, "rejected by Alpaca Paper API", body),
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }

        return ParseSuccessReceipt(statusCode, body, expectedClientOrderId, expectedSymbol);
    }

    private BrokerSubmissionResult ParseSuccessReceipt(
        HttpStatusCode statusCode,
        string body,
        string expectedClientOrderId,
        string expectedSymbol)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Fail(
                ExecutionRejectionCode.MalformedBrokerResponse,
                "Broker returned an empty response body on 2xx.",
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    $"Broker returned non-object JSON ({doc.RootElement.ValueKind}).",
                    statusCode,
                    isDuplicate: false,
                    isRetryable: false);
            }

            var root = doc.RootElement;
            if (!TryReadRequiredString(root, "id", out var orderId) ||
                !TryReadRequiredString(root, "client_order_id", out var clientOrderId) ||
                !TryReadRequiredString(root, "symbol", out var symbol) ||
                !TryReadRequiredString(root, "status", out var status))
            {
                return Fail(
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    "Broker JSON receipt is missing required fields (id, client_order_id, symbol, status).",
                    statusCode,
                    isDuplicate: false,
                    isRetryable: false);
            }

            if (!string.Equals(clientOrderId, expectedClientOrderId, StringComparison.Ordinal))
            {
                return Fail(
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    "Broker receipt client_order_id does not match submitted client_order_id.",
                    statusCode,
                    isDuplicate: false,
                    isRetryable: false);
            }

            if (!string.Equals(symbol, expectedSymbol, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    ExecutionRejectionCode.MalformedBrokerResponse,
                    "Broker receipt symbol does not match submitted symbol.",
                    statusCode,
                    isDuplicate: false,
                    isRetryable: false);
            }

            var side = ReadOptionalString(root, "side") ?? string.Empty;
            var type = ReadOptionalString(root, "type") ?? string.Empty;
            var orderClass = ReadOptionalString(root, "order_class");
            var qty = ReadOptionalString(root, "qty");
            var limitPrice = ReadOptionalDecimal(root, "limit_price");
            var filledAvgPrice = ReadOptionalDecimal(root, "filled_avg_price");
            var submittedAt = ReadOptionalTimestamp(root, "submitted_at");
            var createdAt = ReadOptionalTimestamp(root, "created_at");

            var receipt = new AlpacaOrderReceipt(
                orderId,
                clientOrderId,
                symbol.ToUpperInvariant(),
                status,
                side,
                type,
                orderClass,
                qty,
                limitPrice,
                filledAvgPrice,
                submittedAt,
                createdAt);

            return new BrokerSubmissionResult(
                IsSuccess: true,
                IsDuplicate: false,
                IsRetryable: false,
                StatusCode: statusCode,
                FailureCode: ExecutionRejectionCode.None,
                FailureReason: null,
                Receipt: receipt);
        }
        catch (JsonException)
        {
            return Fail(
                ExecutionRejectionCode.MalformedBrokerResponse,
                "Broker returned malformed JSON.",
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }
    }

    internal static string BuildOrderJson(AlpacaBracketOrderRequest order)
    {
        var payload = new Dictionary<string, object?>(capacity: 10)
        {
            ["symbol"] = order.Symbol.Trim().ToUpperInvariant(),
            ["qty"] = order.Quantity.ToString(CultureInfo.InvariantCulture),
            ["side"] = order.Side == TradeAction.Buy ? "buy" : "sell",
            ["type"] = order.OrderType == ExecutionOrderType.Limit ? "limit" : "market",
            ["time_in_force"] = order.TimeInForce == ExecutionTimeInForce.Gtc ? "gtc" : "day",
            ["client_order_id"] = order.ClientOrderId
        };

        if (order.OrderType == ExecutionOrderType.Limit && order.LimitPrice is { } limitPrice)
        {
            payload["limit_price"] = AlpacaBracketOrderRequest.FormatPrice(limitPrice);
        }

        if (order.OrderClass == ExecutionOrderClass.Bracket)
        {
            payload["order_class"] = "bracket";
            payload["take_profit"] = new Dictionary<string, string>
            {
                ["limit_price"] = AlpacaBracketOrderRequest.FormatPrice(order.TakeProfitPrice!.Value)
            };
            payload["stop_loss"] = new Dictionary<string, string>
            {
                ["stop_price"] = AlpacaBracketOrderRequest.FormatPrice(order.StopLossPrice!.Value)
            };
        }

        return JsonSerializer.Serialize(payload);
    }

    private static bool ValidateOrderPayload(
        AlpacaBracketOrderRequest order,
        out ExecutionRejectionCode code,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            code = ExecutionRejectionCode.InvalidTicker;
            error = "Order symbol is required.";
            return false;
        }

        if (order.Quantity < 1)
        {
            code = ExecutionRejectionCode.InvalidQuantity;
            error = "Order quantity must be at least 1 whole share.";
            return false;
        }

        if (order.Side is not (TradeAction.Buy or TradeAction.Sell))
        {
            code = ExecutionRejectionCode.RiskNotApproved;
            error = "Order side must be Buy or Sell.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(order.ClientOrderId) || order.ClientOrderId.Length > ClientOrderIdGenerator.MaxLength)
        {
            code = ExecutionRejectionCode.InvalidClientOrderId;
            error = $"client_order_id must be non-empty and at most {ClientOrderIdGenerator.MaxLength} characters.";
            return false;
        }

        if (order.OrderType == ExecutionOrderType.Limit && (order.LimitPrice is null || order.LimitPrice <= 0m))
        {
            code = ExecutionRejectionCode.InvalidEntryPrice;
            error = "Limit orders require a positive limit_price.";
            return false;
        }

        if (order.OrderClass == ExecutionOrderClass.Bracket)
        {
            if (order.StopLossPrice is null or <= 0m || order.TakeProfitPrice is null or <= 0m)
            {
                code = ExecutionRejectionCode.InvalidBracketBounds;
                error = "Bracket orders require positive stop_loss and take_profit prices.";
                return false;
            }

            if (order.LimitPrice is { } limitPrice &&
                (order.StopLossPrice.Value >= limitPrice || order.TakeProfitPrice.Value <= limitPrice))
            {
                code = ExecutionRejectionCode.InvalidBracketBounds;
                error = "Bracket bounds must satisfy 0 < stop_loss < limit_price < take_profit.";
                return false;
            }
        }

        code = ExecutionRejectionCode.None;
        error = string.Empty;
        return true;
    }

    private static async Task<(bool Success, string? Body, string? Error)> ReadCappedBodyAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return (true, string.Empty, null);
        }

        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            return (false, null, $"Broker response exceeds {MaxResponseBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxResponseBytes)
        {
            return (false, null, $"Broker response exceeds {MaxResponseBytes} bytes.");
        }

        return (true, Encoding.UTF8.GetString(buffer, 0, total), null);
    }

    private static bool IsSuccessStatusCode(int statusCode) => statusCode is >= 200 and <= 299;

    private static bool IsDuplicateClientOrderBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var textToInspect = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                textToInspect = msg.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Non-JSON 422 body: inspect raw text only as a boolean predicate without surfacing it.
        }

        var hasClientOrderRef = textToInspect.Contains("client_order_id", StringComparison.OrdinalIgnoreCase);
        var hasDuplicateSignal =
            textToInspect.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
            textToInspect.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            textToInspect.Contains("already exists", StringComparison.OrdinalIgnoreCase);

        return hasClientOrderRef && hasDuplicateSignal;
    }

    private static string FormatSafeErrorReason(HttpStatusCode statusCode, string summary, string body)
    {
        var safeCode = TryExtractSafeErrorCode(body);
        return safeCode is null
            ? FormattableString.Invariant($"HTTP {(int)statusCode} {summary}.")
            : FormattableString.Invariant($"HTTP {(int)statusCode} {summary} (alpaca_code={safeCode}).");
    }

    private static string? TryExtractSafeErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("code", out var element) ||
                element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt64(out var numeric))
            {
                return null;
            }

            return numeric switch
            {
                40010001 => "40010001",
                40310000 => "40310000",
                40410000 => "40410000",
                40910000 => "40910000",
                42210000 => "42210000",
                42910000 => "42910000",
                50010000 => "50010000",
                _ => null
            };
        }
        catch (JsonException)
        {
            // Never surface arbitrary or malformed broker error payloads.
        }

        return null;
    }

    private static bool TryReadRequiredString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static decimal? ReadOptionalDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.TryGetDateTimeOffset(out var value) ? value.ToUniversalTime() : null;
    }

    private static BrokerSubmissionResult Fail(
        ExecutionRejectionCode code,
        string reason,
        HttpStatusCode? statusCode,
        bool isDuplicate,
        bool isRetryable) =>
        new(
            IsSuccess: false,
            IsDuplicate: isDuplicate,
            IsRetryable: isRetryable,
            StatusCode: statusCode,
            FailureCode: code,
            FailureReason: reason,
            Receipt: null);
}
