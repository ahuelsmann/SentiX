using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SentiX.DecisionSwarm;

/// <summary>
/// <c>anthropic</c> provider: <c>POST {baseUrl}/messages</c> (Messages API, <c>anthropic-version: 2023-06-01</c>).
/// Pure <see cref="HttpClient"/>; the API key is only ever placed into the <c>x-api-key</c> header.
/// When <see cref="LlmCompletionRequest.JsonSchema"/> is set, output is schema-enforced through a single tool whose
/// <c>input_schema</c> is the role schema plus a forced <c>tool_choice = {type: "tool"}</c>; otherwise the text
/// blocks must form a pure JSON object (prose wrapping is rejected).
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

        var structured = !string.IsNullOrWhiteSpace(request.JsonSchema);
        var toolName = request.EffectiveSchemaName;
        if (structured)
        {
            // Schema enforcement via a single forced tool call: the model MUST answer with tool_use input that
            // conforms to the Spec 003 role schema.
            payload["tools"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = toolName,
                    ["description"] = $"Emit the {request.Role} result as structured JSON conforming to the schema.",
                    ["input_schema"] = LlmHttp.ParseSchema(request.JsonSchema!, ProviderName)
                }
            };
            payload["tool_choice"] = new Dictionary<string, string> { ["type"] = "tool", ["name"] = toolName };
        }

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

        var json = structured ? ExtractStructuredOutput(content, toolName) : ExtractText(content);
        root.TryGetProperty("usage", out var usage);
        var promptTokens = LlmHttp.ReadInt(usage, "input_tokens");
        var completionTokens = LlmHttp.ReadInt(usage, "output_tokens");
        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? tier.Model
            : tier.Model;
        return new LlmCompletionResult(json, promptTokens, completionTokens, promptTokens + completionTokens, model);
    }

    /// <summary>
    /// Schema mode: prefers the forced <c>tool_use</c> block's <c>input</c> object; if no <c>tool_use</c> block is
    /// present, accepts a strict JSON <c>text</c> block only when no prose wraps it.
    /// </summary>
    private static string ExtractStructuredOutput(JsonElement content, string toolName)
    {
        var sawToolUse = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() == "tool_use")
            {
                sawToolUse = true;
                if (block.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() == toolName &&
                    block.TryGetProperty("input", out var input))
                {
                    if (input.ValueKind != JsonValueKind.Object)
                    {
                        throw new LlmResponseFormatException($"{ProviderName}: tool_use input is not a JSON object.");
                    }

                    return LlmHttp.ExtractJsonObject(input.GetRawText());
                }
            }
        }

        if (sawToolUse)
        {
            throw new LlmResponseFormatException($"{ProviderName}: response has no forced tool_use block '{toolName}'.");
        }

        return ExtractText(content);
    }

    /// <summary>Free-text mode: concatenated text blocks must be a pure JSON object (optionally in one clean fence).</summary>
    private static string ExtractText(JsonElement content)
    {
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

        return LlmHttp.ExtractJsonObject(text.ToString());
    }
}
