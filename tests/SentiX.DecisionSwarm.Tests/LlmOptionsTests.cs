using Microsoft.Extensions.Configuration;

namespace SentiX.DecisionSwarm.Tests;

public sealed class LlmOptionsTests
{
    [Fact]
    public void Defaults_MatchSpec007_AndAreUnconfigured()
    {
        var options = new LlmOptions();

        Assert.Equal("openai_compatible", options.FastProvider);
        Assert.Equal("gpt-4o-mini", options.FastModel);
        Assert.Equal("anthropic", options.JudgeProvider);
        Assert.Equal("claude-sonnet-4-5", options.JudgeModel);
        Assert.Equal(string.Empty, options.FastApiKey);
        Assert.Equal(string.Empty, options.JudgeApiKey);
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("your_key")]
    [InlineData(" YOUR_KEY ")]
    public void PlaceholderOrEmptyKey_IsUnconfigured(string key)
    {
        var fastMissing = new LlmOptions { FastApiKey = key, JudgeApiKey = Fixtures.JudgeKey };
        var judgeMissing = new LlmOptions { FastApiKey = Fixtures.FastKey, JudgeApiKey = key };

        Assert.False(fastMissing.IsConfigured);
        Assert.False(fastMissing.IsTierConfigured(LlmTier.Fast));
        Assert.True(fastMissing.IsTierConfigured(LlmTier.Judge));
        Assert.False(judgeMissing.IsConfigured);
        Assert.False(judgeMissing.IsTierConfigured(LlmTier.Judge));
    }

    [Fact]
    public void BothTiersWithRealKeys_IsConfigured_WithDefaultProviderUrls()
    {
        var options = Fixtures.ConfiguredOptions();

        Assert.True(options.IsConfigured);
        Assert.True(options.TryGetTier(LlmTier.Fast, out var fast));
        Assert.True(options.TryGetTier(LlmTier.Judge, out var judge));
        Assert.Equal(new Uri("https://api.openai.com/v1/"), fast.BaseUrl);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/"), judge.BaseUrl);
        Assert.Equal("gpt-4o-mini", fast.Model);
        Assert.Equal(LlmProviders.Anthropic, judge.Provider);
    }

    [Theory]
    [InlineData("https://llm.internal.example/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8000/v1/", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://api.openai.com/v1", false)]
    [InlineData("ftp://localhost/v1", false)]
    [InlineData("https://user:pass@api.openai.com/v1", false)]
    [InlineData("https://api.openai.com/v1?key=abc", false)]
    [InlineData("not a url", false)]
    public void BaseUrlOverride_RequiresHttpsOrLoopback(string url, bool expected)
    {
        var options = Fixtures.ConfiguredOptions();
        options.FastBaseUrl = url;
        options.JudgeBaseUrl = url;

        Assert.Equal(expected, options.IsTierConfigured(LlmTier.Fast));
        Assert.Equal(expected, options.IsTierConfigured(LlmTier.Judge));
        Assert.Equal(expected, options.IsConfigured);
    }

    [Fact]
    public void BaseUrlOverride_IsNormalizedWithTrailingSlash()
    {
        var options = Fixtures.ConfiguredOptions();
        options.FastBaseUrl = "http://localhost:11434/v1";

        Assert.True(options.TryGetTier(LlmTier.Fast, out var fast));
        Assert.Equal("http://localhost:11434/v1/", fast.BaseUrl.ToString());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("")]
    [InlineData("ANTHROPIC")]
    public void UnknownProvider_IsUnconfigured(string provider)
    {
        var options = Fixtures.ConfiguredOptions();
        options.JudgeProvider = provider;

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void EmptyModel_IsUnconfigured()
    {
        var options = Fixtures.ConfiguredOptions();
        options.FastModel = " ";

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void ToString_NeverRendersApiKeys()
    {
        var options = Fixtures.ConfiguredOptions();
        Assert.True(options.TryGetTier(LlmTier.Fast, out var fast));

        Assert.DoesNotContain(Fixtures.FastKey, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixtures.JudgeKey, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixtures.FastKey, fast.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_ReadsSectionAndFlatEnvironmentKeys_FlatWins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:FastProvider"] = "anthropic",
                ["Llm:FastModel"] = "section-model",
                ["Llm:FastApiKey"] = "section-key",
                ["LLM_FAST_MODEL"] = "env-model",
                ["LLM_JUDGE_PROVIDER"] = "openai_compatible",
                ["LLM_JUDGE_API_KEY"] = "env-judge-key",
                ["LLM_JUDGE_BASE_URL"] = "http://localhost:9000/v1"
            })
            .Build();

        var options = LlmOptions.FromConfiguration(configuration);

        Assert.Equal("anthropic", options.FastProvider);
        Assert.Equal("env-model", options.FastModel);
        Assert.Equal("section-key", options.FastApiKey);
        Assert.Equal("openai_compatible", options.JudgeProvider);
        Assert.Equal("claude-sonnet-4-5", options.JudgeModel);
        Assert.Equal("env-judge-key", options.JudgeApiKey);
        Assert.Equal("http://localhost:9000/v1", options.JudgeBaseUrl);
        Assert.Null(options.FastBaseUrl);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void FromConfiguration_WithPlaceholderKeys_IsUnconfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LLM_FAST_API_KEY"] = "your_key",
                ["LLM_JUDGE_API_KEY"] = "your_key"
            })
            .Build();

        Assert.False(LlmOptions.FromConfiguration(configuration).IsConfigured);
    }
}
