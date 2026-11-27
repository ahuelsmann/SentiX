using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace SentiX.DecisionSwarm;

/// <summary>LLM tier used by a Decision-Swarm agent (Spec 007 §2).</summary>
public enum LlmTier { Fast, Judge }

/// <summary>Supported provider identifiers (Spec 007 §2).</summary>
public static class LlmProviders
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string Anthropic = "anthropic";

    public static bool IsKnown(string? provider) =>
        string.Equals(provider, OpenAiCompatible, StringComparison.Ordinal) ||
        string.Equals(provider, Anthropic, StringComparison.Ordinal);

    public static Uri DefaultBaseUrl(string provider) => provider switch
    {
        Anthropic => new Uri("https://api.anthropic.com/v1/"),
        _ => new Uri("https://api.openai.com/v1/")
    };
}

/// <summary>
/// Decision-Swarm LLM configuration (Spec 007 §2). Both tiers must be configured before any external LLM
/// traffic is allowed; otherwise the orchestrator fails closed to <c>HOLD</c>.
/// API keys are never rendered by <see cref="ToString"/>.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public const string PlaceholderApiKey = "your_key";

    public string FastProvider { get; set; } = LlmProviders.OpenAiCompatible;
    public string FastModel { get; set; } = "gpt-4o-mini";
    public string FastApiKey { get; set; } = string.Empty;
    public string? FastBaseUrl { get; set; }

    public string JudgeProvider { get; set; } = LlmProviders.Anthropic;
    public string JudgeModel { get; set; } = "claude-sonnet-4-5";
    public string JudgeApiKey { get; set; } = string.Empty;
    public string? JudgeBaseUrl { get; set; }

    /// <summary>True only when both the Fast and the Judge tier are fully and validly configured.</summary>
    public bool IsConfigured => IsTierConfigured(LlmTier.Fast) && IsTierConfigured(LlmTier.Judge);

    public bool IsTierConfigured(LlmTier tier) => TryGetTier(tier, out _);

    /// <summary>Resolves validated settings for <paramref name="tier"/>; returns false when the tier is unusable.</summary>
    public bool TryGetTier(LlmTier tier, [NotNullWhen(true)] out LlmTierSettings? settings)
    {
        settings = null;
        string? provider;
        string? model;
        string? apiKey;
        string? baseUrl;
        if (tier == LlmTier.Fast)
        {
            (provider, model, apiKey, baseUrl) = (FastProvider, FastModel, FastApiKey, FastBaseUrl);
        }
        else if (tier == LlmTier.Judge)
        {
            (provider, model, apiKey, baseUrl) = (JudgeProvider, JudgeModel, JudgeApiKey, JudgeBaseUrl);
        }
        else
        {
            return false;
        }

        provider = provider?.Trim();
        if (!LlmProviders.IsKnown(provider) || string.IsNullOrWhiteSpace(model) || !IsUsableApiKey(apiKey))
        {
            return false;
        }

        if (!TryResolveBaseUrl(provider!, baseUrl, out var resolved))
        {
            return false;
        }

        settings = new LlmTierSettings(tier, provider!, model.Trim(), apiKey!.Trim(), resolved);
        return true;
    }

    public static bool IsUsableApiKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) &&
        !string.Equals(apiKey.Trim(), PlaceholderApiKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the provider base URL (override or default). Only <c>https</c> URLs, or <c>http</c> URLs that target
    /// a loopback host, are accepted. User-info, query strings and fragments are rejected.
    /// </summary>
    public static bool TryResolveBaseUrl(string provider, string? overrideUrl, [NotNullWhen(true)] out Uri? baseUrl)
    {
        baseUrl = null;
        if (string.IsNullOrWhiteSpace(overrideUrl))
        {
            baseUrl = LlmProviders.DefaultBaseUrl(provider);
            return true;
        }

        if (!Uri.TryCreate(overrideUrl.Trim(), UriKind.Absolute, out var candidate) || !IsAllowedBaseUrl(candidate))
        {
            return false;
        }

        var text = candidate.GetLeftPart(UriPartial.Path);
        baseUrl = new Uri(text.EndsWith('/') ? text : text + "/");
        return true;
    }

    public static bool IsAllowedBaseUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>
    /// Binds from <c>Llm:*</c> keys and the flat <c>LLM_*</c> environment variables (flat variables win when set).
    /// </summary>
    public static LlmOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new LlmOptions();

        string? Read(string key, string envKey)
        {
            var flat = configuration[envKey];
            if (!string.IsNullOrWhiteSpace(flat))
            {
                return flat.Trim();
            }

            var section = configuration[$"{SectionName}:{key}"];
            return string.IsNullOrWhiteSpace(section) ? null : section.Trim();
        }

        options.FastProvider = Read(nameof(FastProvider), "LLM_FAST_PROVIDER") ?? options.FastProvider;
        options.FastModel = Read(nameof(FastModel), "LLM_FAST_MODEL") ?? options.FastModel;
        options.FastApiKey = Read(nameof(FastApiKey), "LLM_FAST_API_KEY") ?? options.FastApiKey;
        options.FastBaseUrl = Read(nameof(FastBaseUrl), "LLM_FAST_BASE_URL");
        options.JudgeProvider = Read(nameof(JudgeProvider), "LLM_JUDGE_PROVIDER") ?? options.JudgeProvider;
        options.JudgeModel = Read(nameof(JudgeModel), "LLM_JUDGE_MODEL") ?? options.JudgeModel;
        options.JudgeApiKey = Read(nameof(JudgeApiKey), "LLM_JUDGE_API_KEY") ?? options.JudgeApiKey;
        options.JudgeBaseUrl = Read(nameof(JudgeBaseUrl), "LLM_JUDGE_BASE_URL");
        return options;
    }

    public override string ToString() =>
        $"LlmOptions {{ Fast = {FastProvider}/{FastModel} (key {(IsUsableApiKey(FastApiKey) ? "set" : "missing")}), " +
        $"Judge = {JudgeProvider}/{JudgeModel} (key {(IsUsableApiKey(JudgeApiKey) ? "set" : "missing")}), " +
        $"IsConfigured = {IsConfigured} }}";
}

/// <summary>Validated, resolved settings for one tier. <see cref="ToString"/> redacts the API key.</summary>
public sealed class LlmTierSettings
{
    internal LlmTierSettings(LlmTier tier, string provider, string model, string apiKey, Uri baseUrl)
    {
        Tier = tier;
        Provider = provider;
        Model = model;
        ApiKey = apiKey;
        BaseUrl = baseUrl;
    }

    public LlmTier Tier { get; }
    public string Provider { get; }
    public string Model { get; }
    internal string ApiKey { get; }
    public Uri BaseUrl { get; }

    public override string ToString() => $"{Tier}: {Provider}/{Model} @ {BaseUrl} (key redacted)";
}
