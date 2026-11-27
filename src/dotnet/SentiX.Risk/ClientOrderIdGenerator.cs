using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SentiX.Risk;

/// <summary>
/// Deterministic, idempotent Alpaca <c>client_order_id</c>:
/// <c>SX-{etDate:yyyyMMdd}-{UPPER_TICKER}-{BUY|SELL}-{sha256LowerHex8}</c> (specs/004 §4, max. 48 characters).
/// </summary>
public static class ClientOrderIdGenerator
{
    /// <summary>Maximum length accepted by Alpaca for <c>client_order_id</c> per SentiX contract.</summary>
    public const int MaxLength = 48;

    /// <summary>Fixed characters around the ticker: <c>"SX-" + 8 + "-" + "-" + 4 + "-" + 8</c>.</summary>
    private const int FixedLengthWithoutTicker = 3 + 8 + 1 + 1 + 4 + 1 + 8;

    /// <summary>Longest ticker that still yields a <see cref="MaxLength"/>-compliant id (<c>22</c>).</summary>
    public const int MaxTickerLength = MaxLength - FixedLengthWithoutTicker;

    /// <summary>Builds the deterministic client order id.</summary>
    /// <exception cref="ArgumentException">Ticker empty or longer than <see cref="MaxTickerLength"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="side"/> is <see cref="TradeAction.Hold"/>.</exception>
    public static string Create(string eventId, string proposalId, string ticker, TradeAction side, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var upperTicker = ticker.Trim().ToUpperInvariant();
        if (upperTicker.Length > MaxTickerLength)
        {
            throw new ArgumentException($"Ticker must not exceed {MaxTickerLength} characters.", nameof(ticker));
        }

        var sideText = side switch
        {
            TradeAction.Buy => "BUY",
            TradeAction.Sell => "SELL",
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Only BUY or SELL orders receive a client order id."),
        };

        var etDate = NewYorkTime.EasternDate(utcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(eventId + proposalId));
        var hex8 = Convert.ToHexStringLower(hash, 0, 4);

        return $"SX-{etDate}-{upperTicker}-{sideText}-{hex8}";
    }
}
