using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SentiX.DecisionSwarm;

/// <summary>
/// <c>anthropic</c> provider: <c>POST {baseUrl}/messages</c> (Messages API, <c>anthropic-version: 2023-06-01</c>).
/// Pure <see cref="HttpClient"/>; the API key is only ever placed into the <c>x-api-key</c> header.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string ApiVersion = "2023-06-01";
    private const string ProviderName = "anthropic";
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public AnthropicLlmClient(HttpClient http, LlmOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<LlmCompletionResult> CompleteJsonAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.TryGetTier(request.Tier, out var tier) || tier.Provider != LlmProviders.Anthropic)
        {
            throw new LlmException($"{ProviderName}: tier {request.Tier} is not configured for this provider.", isTransient: false);
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = tier.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["system"] = request.SystemPrompt,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "user", ["content"] = request.UserPrompt }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(tier.BaseUrl, "messages"))
        {
            Content = LlmHttp.JsonBody(payload)
        };
        message.Headers.Add("x-api-key", tier.ApiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = await LlmHttp.SendAsync(_http, message, ProviderName, cancellationToken).ConfigureAwait(false);
        using var document = LlmHttp.ParseEnvelope(body, ProviderName);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new LlmResponseFormatException($"{ProviderName}: response has no content array.");
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() == "text" &&
                block.TryGetProperty("text", out var part) && part.ValueKind == JsonValueKind.String)
            {
                text.Append(part.GetString());
            }
        }

        var json = LlmHttp.ExtractJsonObject(text.ToString());
        root.TryGetProperty("usage", out var usage);
        var promptTokens = LlmHttp.ReadInt(usage, "input_tokens");
        var completionTokens = LlmHttp.ReadInt(usage, "output_tokens");
        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? tier.Model
            : tier.Model;
        return new LlmCompletionResult(json, promptTokens, completionTokens, promptTokens + completionTokens, model);
    }
}
