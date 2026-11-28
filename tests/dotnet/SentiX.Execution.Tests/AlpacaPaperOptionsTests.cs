using Microsoft.Extensions.Configuration;

namespace SentiX.Execution.Tests;

public sealed class AlpacaPaperOptionsTests
{
    [Fact]
    public void Defaults_AreFailClosedAndPaperOnly()
    {
        var options = new AlpacaPaperOptions();

        Assert.False(options.TradingEnabled);
        Assert.True(options.PaperMode);
        Assert.False(options.AllowShorting);
        Assert.Equal("https://paper-api.alpaca.markets", options.BaseUrl);
        Assert.False(options.HasUsableCredentials);
        Assert.False(options.TryValidateReadyToTrade(out var code, out _));
        Assert.Equal(ExecutionRejectionCode.TradingDisabled, code);
    }

    [Theory]
    [InlineData("https://paper-api.alpaca.markets", true)]
    [InlineData("https://paper-api.alpaca.markets/", true)]
    [InlineData("  https://PAPER-API.ALPACA.MARKETS/  ", true)]
    [InlineData("https://api.alpaca.markets", false)]
    [InlineData("https://api.alpaca.markets/", false)]
    [InlineData("https://api.alpaca.markets/v2/orders", false)]
    [InlineData("https://data.alpaca.markets", false)]
    [InlineData("http://paper-api.alpaca.markets", false)]
    [InlineData("http://localhost:8080", false)]
    [InlineData("https://localhost", false)]
    [InlineData("https://paper-api.alpaca.markets:8443", false)]
    [InlineData("https://paper-api.alpaca.markets.evil.com", false)]
    [InlineData("https://paper-api.alpaca.markets@api.alpaca.markets", false)]
    [InlineData("https://user:pass@paper-api.alpaca.markets", false)]
    [InlineData("https://paper-api.alpaca.markets?live=true", false)]
    [InlineData("https://paper-api.alpaca.markets#fragment", false)]
    [InlineData("https://paper-api.alpaca.markets/v2", false)]
    [InlineData("/v2/orders", false)]
    [InlineData("paper-api.alpaca.markets", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TryResolvePaperBaseUri_AllowsOnlyStrictPaperHost(string? candidate, bool expectedValid)
    {
        var valid = AlpacaPaperOptions.TryResolvePaperBaseUri(candidate, out var resolved);

        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.Equal(new Uri("https://paper-api.alpaca.markets/"), resolved);
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PK_YOUR_PAPER_KEY")]
    [InlineData("YOUR_PAPER_SECRET")]
    [InlineData("your_key")]
    [InlineData("placeholder_secret")]
    [InlineData("change_me_now")]
    public void PlaceholderCredentials_FailSafetyValidation(string placeholder)
    {
        var missingKey = ExecutionTestFixtures.ReadyPaperOptions();
        missingKey.ApiKey = placeholder;

        var missingSecret = ExecutionTestFixtures.ReadyPaperOptions();
        missingSecret.SecretKey = placeholder;

        Assert.False(missingKey.TryValidateExecutionSafety(out var keyCode, out _));
        Assert.Equal(ExecutionRejectionCode.CredentialsMissing, keyCode);
        Assert.False(missingSecret.TryValidateExecutionSafety(out var secretCode, out _));
        Assert.Equal(ExecutionRejectionCode.CredentialsMissing, secretCode);
    }

    [Fact]
    public void LivePaperModeFalse_AndAllowShortingTrue_AreRejected()
    {
        var liveAttempt = ExecutionTestFixtures.ReadyPaperOptions();
        liveAttempt.PaperMode = false;

        var shortAttempt = ExecutionTestFixtures.ReadyPaperOptions();
        shortAttempt.AllowShorting = true;

        Assert.False(liveAttempt.TryValidateReadyToTrade(out var liveCode, out _));
        Assert.Equal(ExecutionRejectionCode.PaperModeRequired, liveCode);

        Assert.False(shortAttempt.TryValidateReadyToTrade(out var shortCode, out _));
        Assert.Equal(ExecutionRejectionCode.ShortingNotAllowed, shortCode);
    }

    [Fact]
    public void ToString_RedactsCredentials()
    {
        var options = ExecutionTestFixtures.ReadyPaperOptions();
        var rendered = options.ToString();

        Assert.DoesNotContain(ExecutionTestFixtures.TestApiKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(ExecutionTestFixtures.TestSecretKey, rendered, StringComparison.Ordinal);
        Assert.Contains("Credentials = set", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_ReadsEnvAndSectionKeys_AndDefaultsTradingDisabled()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:ApiKey"] = "section_key",
                ["ALPACA_API_KEY"] = "env_key",
                ["ALPACA_SECRET_KEY"] = "env_secret",
                ["ALPACA_BASE_URL"] = "https://api.alpaca.markets"
            })
            .Build();

        var options = AlpacaPaperOptions.FromConfiguration(config);

        Assert.False(options.TradingEnabled);
        Assert.True(options.PaperMode);
        Assert.False(options.AllowShorting);
        Assert.Equal("env_key", options.ApiKey);
        Assert.Equal("env_secret", options.SecretKey);
        Assert.False(options.TryValidateExecutionSafety(out var code, out _));
        Assert.Equal(ExecutionRejectionCode.InvalidBaseUrl, code);
    }
}
