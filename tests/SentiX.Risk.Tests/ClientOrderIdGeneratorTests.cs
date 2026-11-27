using System.Text.RegularExpressions;

namespace SentiX.Risk.Tests;

public sealed partial class ClientOrderIdGeneratorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 17, 0, 0, TimeSpan.Zero);

    [GeneratedRegex("^SX-[0-9]{8}-[A-Z0-9.]{1,22}-(BUY|SELL)-[0-9a-f]{8}$")]
    private static partial Regex Format();

    [Fact]
    public void Create_MatchesSpecExample_WithKnownSha256Prefix()
    {
        // SHA256("evt_98a7f6prop_12345") = ad2c81a8...
        var id = ClientOrderIdGenerator.Create("evt_98a7f6", "prop_12345", "xle", TradeAction.Buy, Noon);

        Assert.Equal("SX-20261126-XLE-BUY-ad2c81a8", id);
        Assert.Matches(Format(), id);
        Assert.True(id.Length <= ClientOrderIdGenerator.MaxLength);
    }

    [Fact]
    public void Create_IsDeterministic_AndSensitiveToEventAndProposal()
    {
        var a = ClientOrderIdGenerator.Create("evt_1", "prop_1", "AAPL", TradeAction.Sell, Noon);
        var b = ClientOrderIdGenerator.Create("evt_1", "prop_1", "AAPL", TradeAction.Sell, Noon);
        var c = ClientOrderIdGenerator.Create("evt_1", "prop_2", "AAPL", TradeAction.Sell, Noon);

        Assert.Equal("SX-20261126-AAPL-SELL-c915e268", a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData("2026-12-01T04:59:59Z", "20261130")] // 23:59:59 EST previous day
    [InlineData("2026-12-01T05:00:00Z", "20261201")] // 00:00 EST
    [InlineData("2026-07-16T03:59:59Z", "20260715")] // 23:59:59 EDT previous day
    [InlineData("2026-07-16T04:00:00Z", "20260716")] // 00:00 EDT
    public void Create_UsesNewYorkDate_NotUtcDate(string utc, string expectedDate)
    {
        var id = ClientOrderIdGenerator.Create("e", "p", "MSFT", TradeAction.Buy, DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

        Assert.StartsWith($"SX-{expectedDate}-MSFT-BUY-", id, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MaxTickerLength_YieldsExactly48Chars()
    {
        var id = ClientOrderIdGenerator.Create("e", "p", new string('Q', ClientOrderIdGenerator.MaxTickerLength), TradeAction.Sell, Noon);

        Assert.Equal(22, ClientOrderIdGenerator.MaxTickerLength);
        Assert.Equal(48, id.Length);
    }

    [Fact]
    public void Create_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentNullException>(() => ClientOrderIdGenerator.Create(null!, "p", "AAPL", TradeAction.Buy, Noon));
        Assert.Throws<ArgumentNullException>(() => ClientOrderIdGenerator.Create("e", null!, "AAPL", TradeAction.Buy, Noon));
        Assert.Throws<ArgumentNullException>(() => ClientOrderIdGenerator.Create("e", "p", null!, TradeAction.Buy, Noon));
        Assert.Throws<ArgumentException>(() => ClientOrderIdGenerator.Create("e", "p", "  ", TradeAction.Buy, Noon));
        Assert.Throws<ArgumentException>(() => ClientOrderIdGenerator.Create("e", "p", new string('Q', 23), TradeAction.Buy, Noon));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientOrderIdGenerator.Create("e", "p", "AAPL", TradeAction.Hold, Noon));
    }
}
