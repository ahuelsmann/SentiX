using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace SentiX.Execution;

/// <summary>
/// Configuration and strict Paper-only endpoint validation for Alpaca Trading API v2 (Spec 008).
/// Safe defaults: <see cref="TradingEnabled"/> is <c>false</c>, <see cref="PaperMode"/> is <c>true</c>,
/// and <see cref="AllowShorting"/> is <c>false</c>. Credentials are redacted by <see cref="ToString"/>.
/// </summary>
public sealed class AlpacaPaperOptions
{
    /// <summary>Configuration section name (<c>Alpaca</c>).</summary>
    public const string SectionName = "Alpaca";

    /// <summary>The sole permitted Alpaca host for Phase 4 paper execution.</summary>
    public const string AllowedPaperHost = "paper-api.alpaca.markets";

    /// <summary>Canonical Alpaca Paper REST API base URL.</summary>
    public const string DefaultPaperBaseUrl = "https://paper-api.alpaca.markets";

    /// <summary>Default per-request timeout in seconds.</summary>
    public const int DefaultRequestTimeoutSeconds = 10;

    /// <summary>Global execution switch; defaults strictly to <c>false</c> (fail-closed).</summary>
    public bool TradingEnabled { get; set; }

    /// <summary>Must remain <c>true</c> for Phase 4 paper execution.</summary>
    public bool PaperMode { get; set; } = true;

    /// <summary>Must remain <c>false</c> for Phase 4 Long-Only execution.</summary>
    public bool AllowShorting { get; set; }

    /// <summary>Base URL for Alpaca Paper REST API. Only <c>https://paper-api.alpaca.markets</c> is accepted.</summary>
    public string BaseUrl { get; set; } = DefaultPaperBaseUrl;

    /// <summary>Alpaca Paper API key ID (<c>APCA-API-KEY-ID</c>).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Alpaca Paper API secret key (<c>APCA-API-SECRET-KEY</c>).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Maximum duration allowed for a single Alpaca REST order submission.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);

    /// <summary>True when both <see cref="ApiKey"/> and <see cref="SecretKey"/> are present and non-placeholder.</summary>
    public bool HasUsableCredentials => IsUsableSecret(ApiKey) && IsUsableSecret(SecretKey);

    /// <summary>True when credentials and strict paper-only endpoint invariants are valid (independent of <see cref="TradingEnabled"/>).</summary>
    public bool IsConfigured => TryValidateExecutionSafety(out _, out _);

    /// <summary>
    /// Validates whether the options permit paper order dispatch (ignoring <see cref="TradingEnabled"/>).
    /// </summary>
    public bool TryValidateExecutionSafety(out ExecutionRejectionCode rejectionCode, [NotNullWhen(false)] out string? error)
    {
        if (!PaperMode)
        {
            rejectionCode = ExecutionRejectionCode.PaperModeRequired;
            error = "PaperMode must be true; live trading is prohibited in Phase 4.";
            return false;
        }

        if (AllowShorting)
        {
            rejectionCode = ExecutionRejectionCode.ShortingNotAllowed;
            error = "AllowShorting must be false; Phase 4 execution is strictly Long-Only.";
            return false;
        }

        if (!TryResolvePaperBaseUri(BaseUrl, out _))
        {
            rejectionCode = ExecutionRejectionCode.InvalidBaseUrl;
            error = $"BaseUrl must be strictly https://{AllowedPaperHost} with no userinfo, query, fragment, or custom path.";
            return false;
        }

        if (!HasUsableCredentials)
        {
            rejectionCode = ExecutionRejectionCode.CredentialsMissing;
            error = "Alpaca Paper API credentials are missing or placeholder values.";
            return false;
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            rejectionCode = ExecutionRejectionCode.InvalidBaseUrl;
            error = "RequestTimeout must be positive.";
            return false;
        }

        rejectionCode = ExecutionRejectionCode.None;
        error = null;
        return true;
    }

    /// <summary>
    /// Validates whether <see cref="TradingEnabled"/> is active AND all paper safety invariants pass.
    /// </summary>
    public bool TryValidateReadyToTrade(out ExecutionRejectionCode rejectionCode, [NotNullWhen(false)] out string? error)
    {
        if (!TradingEnabled)
        {
            rejectionCode = ExecutionRejectionCode.TradingDisabled;
            error = "TradingEnabled is false; paper order dispatch is disabled.";
            return false;
        }

        return TryValidateExecutionSafety(out rejectionCode, out error);
    }

    /// <summary>
    /// Strictly validates and normalizes an Alpaca Paper base URI.
    /// Rejects live endpoints (<c>api.alpaca.markets</c>), non-HTTPS schemes, relative URIs,
    /// userinfo (<c>@</c>), query strings, fragments, non-default ports, and unexpected paths.
    /// </summary>
    public static bool TryResolvePaperBaseUri(string? rawBaseUrl, [NotNullWhen(true)] out Uri? resolvedUri)
    {
        resolvedUri = null;
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            return false;
        }

        var trimmed = rawBaseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (!IsAllowedPaperBaseUri(candidate))
        {
            return false;
        }

        resolvedUri = new Uri($"https://{AllowedPaperHost}/", UriKind.Absolute);
        return true;
    }

    /// <summary>Checks whether an already-parsed <see cref="Uri"/> strictly targets <c>https://paper-api.alpaca.markets/</c>.</summary>
    public static bool IsAllowedPaperBaseUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.Equals(uri.Host, AllowedPaperHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, AllowedPaperHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal);
    }

    /// <summary>Checks whether a constructed request <see cref="Uri"/> strictly targets <c>https://paper-api.alpaca.markets/v2/orders</c>.</summary>
    public static bool IsAllowedPaperOrdersUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.Equals(uri.Host, AllowedPaperHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, AllowedPaperHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/v2/orders", StringComparison.Ordinal);
    }

    /// <summary>Returns true when <paramref name="secret"/> is non-empty and not a template placeholder.</summary>
    public static bool IsUsableSecret(string? secret)
    {
        var trimmed = secret?.Trim();
        return !string.IsNullOrEmpty(trimmed) &&
               !trimmed.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("change_me", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Binds options from <c>IConfiguration</c>, honoring flat environment variables over section keys.
    /// </summary>
    public static AlpacaPaperOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AlpacaPaperOptions();

        string? ReadString(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        bool? ReadBool(params string[] keys)
        {
            foreach (var key in keys)
            {
                var raw = configuration[key];
                if (!string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw.Trim(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        int? ReadInt(params string[] keys)
        {
            foreach (var key in keys)
            {
                var raw = configuration[key];
                if (!string.IsNullOrWhiteSpace(raw) &&
                    int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        options.TradingEnabled = ReadBool("TRADING_ENABLED", "ALPACA_TRADING_ENABLED", "Trading:Enabled", "Alpaca:TradingEnabled") ?? false;
        options.PaperMode = ReadBool("ALPACA_PAPER_MODE", "Alpaca:PaperMode") ?? true;
        options.AllowShorting = ReadBool("ALPACA_ALLOW_SHORTING", "Alpaca:AllowShorting") ?? false;
        options.BaseUrl = ReadString("ALPACA_BASE_URL", "Alpaca:BaseUrl") ?? DefaultPaperBaseUrl;
        options.ApiKey = ReadString("ALPACA_API_KEY", "Alpaca:ApiKey") ?? string.Empty;
        options.SecretKey = ReadString("ALPACA_SECRET_KEY", "Alpaca:SecretKey") ?? string.Empty;

        var timeoutSeconds = ReadInt("ALPACA_REQUEST_TIMEOUT_SECONDS", "Alpaca:RequestTimeoutSeconds");
        if (timeoutSeconds.HasValue)
        {
            options.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds.Value);
        }

        return options;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"AlpacaPaperOptions {{ TradingEnabled = {TradingEnabled}, PaperMode = {PaperMode}, AllowShorting = {AllowShorting}, " +
        $"BaseUrl = {BaseUrl}, Credentials = {(HasUsableCredentials ? "set" : "missing")} }}";
}
