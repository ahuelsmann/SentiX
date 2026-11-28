using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Domain;
using SentiX.Risk;
using RiskTradeAction = SentiX.Risk.TradeAction;

namespace SentiX.Execution;

/// <summary>
/// Authoritative paper-account snapshot fetched from <c>https://paper-api.alpaca.markets</c> for armed Phase 4 risk evaluation.
/// </summary>
public sealed record PaperAccountSnapshot(
    decimal Equity,
    decimal LastEquity,
    decimal BuyingPower,
    double DailyPnLPct,
    IReadOnlyList<OpenPosition> OpenPositions,
    IReadOnlyList<PendingOrder> PendingOrders,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyDictionary<string, decimal?>? PendingOrderLimitPrices = null);

/// <summary>Non-throwing structured result of fetching an authoritative paper portfolio snapshot.</summary>
public sealed record PaperAccountSnapshotResult(
    bool IsSuccess,
    ExecutionRejectionCode FailureCode,
    string? FailureReason,
    PaperAccountSnapshot? Snapshot)
{
    /// <summary>Creates a successful <see cref="PaperAccountSnapshotResult"/>.</summary>
    public static PaperAccountSnapshotResult Success(PaperAccountSnapshot snapshot) =>
        new(true, ExecutionRejectionCode.None, null, snapshot);

    /// <summary>Creates a fail-closed <see cref="PaperAccountSnapshotResult"/>.</summary>
    public static PaperAccountSnapshotResult Failure(ExecutionRejectionCode code, string reason) =>
        new(false, code, reason, null);
}

/// <summary>Fetches authoritative Alpaca Paper account, open positions, and open orders for armed risk gating.</summary>
public interface IPaperPortfolioSnapshotProvider
{
    /// <summary>
    /// Fetches the current paper account equity, buying power, fractional daily PnL, open positions, and open orders.
    /// Never throws on network, timeout, HTTP, or JSON errors (fails closed).
    /// </summary>
    Task<PaperAccountSnapshotResult> GetSnapshotAsync(UniverseConfig? universe, CancellationToken cancellationToken);
}

/// <summary>
/// Strict paper-only HTTP implementation of <see cref="IPaperPortfolioSnapshotProvider"/> targeting
/// <c>https://paper-api.alpaca.markets/v2/account</c>, <c>/v2/positions</c>, and <c>/v2/orders?status=open&amp;limit=500</c>.
/// </summary>
public sealed class AlpacaPaperPortfolioSnapshotProvider : IPaperPortfolioSnapshotProvider
{
    /// <summary>Hard cap on response payload size (256 KiB).</summary>
    public const int MaxResponseBytes = 256 * 1024;

    private const int OpenOrdersLimit = 500;
    private const string AccountPath = "/v2/account";
    private const string PositionsPath = "/v2/positions";
    private const string OpenOrdersPathAndQuery = "/v2/orders?status=open&limit=500";

    private readonly HttpClient _httpClient;
    private readonly AlpacaPaperOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Initializes a new <see cref="AlpacaPaperPortfolioSnapshotProvider"/>.</summary>
    public AlpacaPaperPortfolioSnapshotProvider(
        HttpClient httpClient,
        AlpacaPaperOptions options,
        ILogger<AlpacaPaperPortfolioSnapshotProvider>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<PaperAccountSnapshotResult> GetSnapshotAsync(
        UniverseConfig? universe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.TryValidateReadyToTrade(out var optionsCode, out var optionsError))
        {
            return PaperAccountSnapshotResult.Failure(optionsCode, optionsError);
        }

        if (_httpClient.BaseAddress is not null && !AlpacaPaperOptions.IsAllowedPaperBaseUri(_httpClient.BaseAddress))
        {
            return PaperAccountSnapshotResult.Failure(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Injected HttpClient BaseAddress is not strictly https://paper-api.alpaca.markets.");
        }

        if (!AlpacaPaperOptions.TryResolvePaperBaseUri(_options.BaseUrl, out var baseUri))
        {
            return PaperAccountSnapshotResult.Failure(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Alpaca BaseUrl failed strict paper endpoint resolution.");
        }

        var accountUri = new Uri(baseUri, "v2/account");
        var positionsUri = new Uri(baseUri, "v2/positions");
        var ordersUri = new Uri(baseUri, "v2/orders?status=open&limit=500");

        if (!IsAllowedPaperReadUri(accountUri, AccountPath) ||
            !IsAllowedPaperReadUri(positionsUri, PositionsPath) ||
            !IsAllowedPaperReadUri(ordersUri, OpenOrdersPathAndQuery))
        {
            return PaperAccountSnapshotResult.Failure(
                ExecutionRejectionCode.InvalidBaseUrl,
                "Resolved paper portfolio snapshot URI failed strict paper endpoint check.");
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var accountRes = await GetJsonAsync(accountUri, AccountPath, linkedCts.Token, cancellationToken).ConfigureAwait(false);
        if (!accountRes.Success)
        {
            return PaperAccountSnapshotResult.Failure(accountRes.FailureCode, accountRes.Error!);
        }

        var positionsRes = await GetJsonAsync(positionsUri, PositionsPath, linkedCts.Token, cancellationToken).ConfigureAwait(false);
        if (!positionsRes.Success)
        {
            return PaperAccountSnapshotResult.Failure(positionsRes.FailureCode, positionsRes.Error!);
        }

        var ordersRes = await GetJsonAsync(ordersUri, OpenOrdersPathAndQuery, linkedCts.Token, cancellationToken).ConfigureAwait(false);
        if (!ordersRes.Success)
        {
            return PaperAccountSnapshotResult.Failure(ordersRes.FailureCode, ordersRes.Error!);
        }

        if (!TryParseAccount(accountRes.Body!, out var equity, out var lastEquity, out var buyingPower, out var dailyPnLPct, out var accountError))
        {
            return PaperAccountSnapshotResult.Failure(ExecutionRejectionCode.MalformedBrokerResponse, accountError);
        }

        if (!TryParsePositions(positionsRes.Body!, universe, out var openPositions, out var positionsError))
        {
            return PaperAccountSnapshotResult.Failure(ExecutionRejectionCode.MalformedBrokerResponse, positionsError);
        }

        if (!TryParsePendingOrders(ordersRes.Body!, out var pendingOrders, out var pendingLimitPrices, out var ordersError))
        {
            return PaperAccountSnapshotResult.Failure(ExecutionRejectionCode.MalformedBrokerResponse, ordersError);
        }

        return PaperAccountSnapshotResult.Success(new PaperAccountSnapshot(
            equity,
            lastEquity,
            buyingPower,
            dailyPnLPct,
            openPositions,
            pendingOrders,
            _timeProvider.GetUtcNow(),
            pendingLimitPrices));
    }

    internal static bool IsAllowedPaperReadUri(Uri? uri, string expectedPathAndQuery)
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

        return string.Equals(uri.PathAndQuery, expectedPathAndQuery, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the sector for <paramref name="symbol"/> strictly from <paramref name="universe"/>:
    /// watchlist equities return their configured sector, sector/benchmark ETFs return <c>ETF</c>,
    /// and unknown tickers return <c>false</c> (fail-closed).
    /// </summary>
    public static bool TryResolveKnownSector(string? symbol, UniverseConfig? universe, out string sector)
    {
        sector = string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var trimmed = symbol.Trim();
        if (universe is null)
        {
            sector = "ETF";
            return true;
        }

        var match = universe.Watchlist.FirstOrDefault(
            entry => string.Equals(entry.Ticker, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !string.IsNullOrWhiteSpace(match.Sector))
        {
            sector = match.Sector;
            return true;
        }

        if (string.Equals(universe.BenchmarkTicker, trimmed, StringComparison.OrdinalIgnoreCase) ||
            universe.SectorEtfs.Values.Any(etf => string.Equals(etf, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            sector = "ETF";
            return true;
        }

        return false;
    }

    /// <summary>Resolves the sector name for <paramref name="symbol"/> from <paramref name="universe"/> (defaulting to <c>ETF</c> for shadow fallback).</summary>
    public static string ResolveSector(string symbol, UniverseConfig? universe) =>
        TryResolveKnownSector(symbol, universe, out var sector) ? sector : "ETF";

    private async Task<(bool Success, string? Body, ExecutionRejectionCode FailureCode, string? Error)> GetJsonAsync(
        Uri requestUri,
        string expectedPathAndQuery,
        CancellationToken requestToken,
        CancellationToken callerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", _options.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", _options.SecretKey.Trim());

        if (!IsAllowedPaperReadUri(request.RequestUri, expectedPathAndQuery))
        {
            return (false, null, ExecutionRejectionCode.InvalidBaseUrl, "Pre-dispatch guard rejected non-paper portfolio URI.");
        }

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return (false, null, ExecutionRejectionCode.BrokerRateLimited, "Alpaca Paper portfolio read hit HTTP 429 rate limit.");
            }

            if (statusCode >= 500)
            {
                return (false, null, ExecutionRejectionCode.BrokerServerError, FormattableString.Invariant($"Alpaca Paper portfolio read returned HTTP {statusCode}."));
            }

            if (statusCode is < 200 or > 299)
            {
                return (false, null, ExecutionRejectionCode.BrokerRejected, FormattableString.Invariant($"Alpaca Paper portfolio read rejected with HTTP {statusCode}."));
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return (false, null, ExecutionRejectionCode.MalformedBrokerResponse, "Alpaca Paper portfolio response exceeded 256 KiB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), requestToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxResponseBytes)
            {
                return (false, null, ExecutionRejectionCode.MalformedBrokerResponse, "Alpaca Paper portfolio response exceeded 256 KiB.");
            }

            var body = Encoding.UTF8.GetString(buffer, 0, total);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (false, null, ExecutionRejectionCode.MalformedBrokerResponse, "Alpaca Paper portfolio response body was empty.");
            }

            return (true, body, ExecutionRejectionCode.None, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Alpaca Paper portfolio snapshot request timed out ({Path}).", expectedPathAndQuery);
            return (false, null, ExecutionRejectionCode.BrokerTimeout, "Alpaca Paper portfolio snapshot request timed out.");
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Alpaca Paper portfolio snapshot request timed out ({Path}).", expectedPathAndQuery);
            return (false, null, ExecutionRejectionCode.BrokerTimeout, "Alpaca Paper portfolio snapshot request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Alpaca Paper portfolio network error ({Path}): {ErrorType}.", expectedPathAndQuery, ex.GetType().Name);
            return (false, null, ExecutionRejectionCode.BrokerNetworkError, $"Alpaca Paper portfolio network failure ({ex.GetType().Name}).");
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Alpaca Paper portfolio stream error ({Path}): {ErrorType}.", expectedPathAndQuery, ex.GetType().Name);
            return (false, null, ExecutionRejectionCode.BrokerNetworkError, $"Alpaca Paper portfolio stream failure ({ex.GetType().Name}).");
        }
    }

    private static bool TryParseAccount(
        string json,
        out decimal equity,
        out decimal lastEquity,
        out decimal buyingPower,
        out double dailyPnLPct,
        out string error)
    {
        equity = 0m;
        lastEquity = 0m;
        buyingPower = 0m;
        dailyPnLPct = 0d;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Alpaca /v2/account payload is not a JSON object.";
                return false;
            }

            var root = doc.RootElement;
            if (ReadOptionalBool(root, "trading_blocked") == true ||
                ReadOptionalBool(root, "account_blocked") == true)
            {
                error = "Alpaca Paper account is marked trading_blocked or account_blocked.";
                return false;
            }

            if (!TryReadDecimal(root, "equity", out equity) || equity <= 0m)
            {
                error = "Alpaca Paper account equity is missing or non-positive.";
                return false;
            }

            if (!TryReadDecimal(root, "last_equity", out lastEquity) || lastEquity <= 0m)
            {
                error = "Alpaca Paper account last_equity is missing or non-positive.";
                return false;
            }

            if (!TryReadDecimal(root, "buying_power", out buyingPower) || buyingPower < 0m)
            {
                error = "Alpaca Paper account buying_power is missing or negative.";
                return false;
            }

            dailyPnLPct = (double)((equity - lastEquity) / lastEquity);
            if (!double.IsFinite(dailyPnLPct))
            {
                error = "Computed paper account DailyPnLPct is non-finite.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Alpaca /v2/account returned malformed JSON.";
            return false;
        }
    }

    private static bool TryParsePositions(
        string json,
        UniverseConfig? universe,
        out IReadOnlyList<OpenPosition> positions,
        out string error)
    {
        positions = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Alpaca /v2/positions payload is not a JSON array.";
                return false;
            }

            var list = new List<OpenPosition>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "Alpaca /v2/positions entry is not a JSON object.";
                    return false;
                }

                if (!TryReadString(item, "symbol", out var rawSymbol))
                {
                    error = "Alpaca /v2/positions entry is missing symbol.";
                    return false;
                }

                if (!TryReadDecimal(item, "qty", out var qtyDecimal))
                {
                    error = "Alpaca /v2/positions entry is missing numeric qty.";
                    return false;
                }

                if (qtyDecimal == 0m)
                {
                    continue;
                }

                // Phase 4 is strictly Long-Only whole shares; fail closed if account holds shorts or fractional shares.
                if (qtyDecimal < 0m || qtyDecimal != Math.Truncate(qtyDecimal) || qtyDecimal > int.MaxValue)
                {
                    error = "Alpaca Paper account holds an unexpected short or fractional position; failing closed.";
                    return false;
                }

                var qty = (int)qtyDecimal;
                if (!TryReadDecimal(item, "market_value", out var marketValue) || marketValue < 0m)
                {
                    error = "Alpaca /v2/positions entry has missing or negative market_value.";
                    return false;
                }

                var currentPrice = TryReadDecimal(item, "current_price", out var parsedPrice) && parsedPrice > 0m
                    ? parsedPrice
                    : marketValue / qty;

                var symbol = rawSymbol.Trim().ToUpperInvariant();
                if (!TryResolveKnownSector(symbol, universe, out var resolvedSector))
                {
                    error = $"Alpaca /v2/positions symbol '{symbol}' is not in the configured SentiX universe.";
                    return false;
                }

                list.Add(new OpenPosition(
                    symbol,
                    resolvedSector,
                    qty,
                    marketValue,
                    currentPrice));
            }

            positions = list;
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Alpaca /v2/positions returned malformed JSON.";
            return false;
        }
    }

    private static bool TryParsePendingOrders(
        string json,
        out IReadOnlyList<PendingOrder> orders,
        out IReadOnlyDictionary<string, decimal?> limitPrices,
        out string error)
    {
        orders = [];
        limitPrices = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Alpaca /v2/orders?status=open payload is not a JSON array.";
                return false;
            }

            var length = doc.RootElement.GetArrayLength();
            if (length >= OpenOrdersLimit)
            {
                error = "Alpaca open orders reached page cap (500); failing closed to avoid partial visibility.";
                return false;
            }

            var list = new List<PendingOrder>(capacity: length);
            var priceMap = new Dictionary<string, decimal?>(capacity: length, StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "Alpaca open order entry is not a JSON object.";
                    return false;
                }

                var hasClientId = TryReadString(item, "client_order_id", out var clientOrderId);
                if (!hasClientId && !TryReadString(item, "id", out clientOrderId))
                {
                    error = "Alpaca open order entry is missing client_order_id and id.";
                    return false;
                }

                if (!TryReadString(item, "symbol", out var rawSymbol) ||
                    !TryReadString(item, "side", out var rawSide))
                {
                    error = "Alpaca open order entry is missing symbol or side.";
                    return false;
                }

                var side = rawSide.Trim().ToLowerInvariant() switch
                {
                    "buy" => RiskTradeAction.Buy,
                    "sell" => RiskTradeAction.Sell,
                    _ => RiskTradeAction.Hold
                };
                if (side == RiskTradeAction.Hold)
                {
                    error = "Alpaca open order entry has unsupported side.";
                    return false;
                }

                if (!TryReadDecimal(item, "qty", out var qtyDecimal) ||
                    qtyDecimal < 1m ||
                    qtyDecimal != Math.Truncate(qtyDecimal) ||
                    qtyDecimal > int.MaxValue)
                {
                    error = "Alpaca open order entry has missing, fractional, or non-positive qty.";
                    return false;
                }

                var trimmedId = clientOrderId.Trim();
                var parsedLimitPrice = TryReadDecimal(item, "limit_price", out var lp) && lp > 0m ? lp : (decimal?)null;
                priceMap[trimmedId] = parsedLimitPrice;

                list.Add(new PendingOrder(
                    trimmedId,
                    rawSymbol.Trim().ToUpperInvariant(),
                    side,
                    (int)qtyDecimal));
            }

            orders = list;
            limitPrices = priceMap;
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Alpaca /v2/orders?status=open returned malformed JSON.";
            return false;
        }
    }

    private static bool TryReadString(JsonElement obj, string property, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryReadDecimal(JsonElement obj, string property, out decimal value)
    {
        value = 0m;
        if (!obj.TryGetProperty(property, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.String &&
               decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool? ReadOptionalBool(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
