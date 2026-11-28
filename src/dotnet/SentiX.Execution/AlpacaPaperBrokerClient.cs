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

            return MapResponse(response.StatusCode, bodyResult.Body!, order.ClientOrderId);
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

    private BrokerSubmissionResult MapResponse(HttpStatusCode statusCode, string body, string expectedClientOrderId)
    {
        var code = (int)statusCode;

        if (statusCode == HttpStatusCode.Conflict)
        {
            _logger.LogInformation("Alpaca Paper reported HTTP 409 duplicate for {ClientOrderId}.", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerDuplicateOrder,
                ExtractSafeErrorMessage(statusCode, body) ?? "HTTP 409 duplicate client_order_id.",
                statusCode,
                isDuplicate: true,
                isRetryable: false);
        }

        if (statusCode == HttpStatusCode.UnprocessableEntity)
        {
            var safeMessage = ExtractSafeErrorMessage(statusCode, body);
            if (IsDuplicateClientOrderError(safeMessage ?? body))
            {
                _logger.LogInformation("Alpaca Paper reported HTTP 422 duplicate client_order_id for {ClientOrderId}.", expectedClientOrderId);
                return Fail(
                    ExecutionRejectionCode.BrokerDuplicateOrder,
                    safeMessage ?? "HTTP 422 duplicate client_order_id.",
                    statusCode,
                    isDuplicate: true,
                    isRetryable: false);
            }

            _logger.LogWarning("Alpaca Paper rejected order {ClientOrderId} with HTTP 422.", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerRejected,
                safeMessage ?? "HTTP 422 order rejected by broker.",
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Alpaca Paper rate limited order {ClientOrderId} (HTTP 429).", expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerRateLimited,
                "HTTP 429 rate limit exceeded on Alpaca Paper API.",
                statusCode,
                isDuplicate: false,
                isRetryable: true);
        }

        if (code >= 500)
        {
            _logger.LogWarning("Alpaca Paper server error HTTP {StatusCode} for {ClientOrderId}.", code, expectedClientOrderId);
            return Fail(
                ExecutionRejectionCode.BrokerServerError,
                FormattableString.Invariant($"HTTP {code} server error from Alpaca Paper API."),
                statusCode,
                isDuplicate: false,
                isRetryable: true);
        }

        if (!IsSuccessStatusCode(code))
        {
            var safeMessage = ExtractSafeErrorMessage(statusCode, body);
            _logger.LogWarning("Alpaca Paper rejected order {ClientOrderId} with HTTP {StatusCode}.", expectedClientOrderId, code);
            return Fail(
                ExecutionRejectionCode.BrokerRejected,
                safeMessage ?? FormattableString.Invariant($"HTTP {code} rejected by Alpaca Paper API."),
                statusCode,
                isDuplicate: false,
                isRetryable: false);
        }

        return ParseSuccessReceipt(statusCode, body, expectedClientOrderId);
    }

    private BrokerSubmissionResult ParseSuccessReceipt(HttpStatusCode statusCode, string body, string expectedClientOrderId)
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

    private static bool IsDuplicateClientOrderError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasClientOrderRef = text.Contains("client_order_id", StringComparison.OrdinalIgnoreCase);
        var hasDuplicateSignal =
            text.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("already exists", StringComparison.OrdinalIgnoreCase);

        return hasClientOrderRef && hasDuplicateSignal;
    }

    private static string? ExtractSafeErrorMessage(HttpStatusCode statusCode, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                var raw = msg.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var bounded = raw.Length > 240 ? raw[..240] : raw;
                    return FormattableString.Invariant($"HTTP {(int)statusCode}: {bounded}");
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed error JSON and fall back to status-only message.
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
