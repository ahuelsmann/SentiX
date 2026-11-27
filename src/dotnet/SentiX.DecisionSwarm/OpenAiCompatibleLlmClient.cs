using System.Net.Http.Headers;
using System.Text.Json;

namespace SentiX.DecisionSwarm;

/// <summary>
/// <c>openai_compatible</c> provider: <c>POST {baseUrl}/chat/completions</c> with strict
/// <c>response_format = json_schema</c> when <see cref="LlmCompletionRequest.JsonSchema"/> is set (else <c>json_object</c>).
/// The returned content must be a pure top-level JSON object (prose wrapping is rejected).
/// Pure <see cref="HttpClient"/>; the API key is only ever placed into the <c>Authorization</c> header.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private const string ProviderName = "openai_compatible";
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OpenAiCompatibleLlmClient(HttpClient http, LlmOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<LlmCompletionResult> CompleteJsonAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.TryGetTier(request.Tier, out var tier) || tier.Provider != LlmProviders.OpenAiCompatible)
        {
            throw new LlmException($"{ProviderName}: tier {request.Tier} is not configured for this provider.", isTransient: false);
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = tier.Model,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["response_format"] = BuildResponseFormat(request),
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = request.SystemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = request.UserPrompt }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(tier.BaseUrl, "chat/completions"))
        {
            Content = LlmHttp.JsonBody(payload)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tier.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = await LlmHttp.SendAsync(_http, message, ProviderName, cancellationToken).ConfigureAwait(false);
        using var document = LlmHttp.ParseEnvelope(body, ProviderName);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object ||
            !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
        {
            throw new LlmResponseFormatException($"{ProviderName}: response has no choices[0].message.content.");
        }

        var json = LlmHttp.ExtractJsonObject(content.GetString());
        root.TryGetProperty("usage", out var usage);
        var promptTokens = LlmHttp.ReadInt(usage, "prompt_tokens");
        var completionTokens = LlmHttp.ReadInt(usage, "completion_tokens");
        var totalTokens = LlmHttp.ReadInt(usage, "total_tokens");
        if (totalTokens == 0)
        {
            totalTokens = promptTokens + completionTokens;
        }

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? tier.Model
            : tier.Model;
        return new LlmCompletionResult(json, promptTokens, completionTokens, totalTokens, model);
    }

    /// <summary>
    /// Strict structured outputs (<c>json_schema</c>, <c>strict: true</c>) when a role schema is supplied — the wire
    /// format shared by OpenAI and Azure OpenAI; plain <c>json_object</c> mode otherwise.
    /// </summary>
    private static object BuildResponseFormat(LlmCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            return new Dictionary<string, string> { ["type"] = "json_object" };
        }

        return new Dictionary<string, object>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object>
            {
                ["name"] = request.EffectiveSchemaName,
                ["strict"] = true,
                ["schema"] = LlmHttp.ParseSchema(request.JsonSchema, ProviderName)
            }
        };
    }
}
