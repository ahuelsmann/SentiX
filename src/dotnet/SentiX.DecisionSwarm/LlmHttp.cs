using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SentiX.DecisionSwarm;

/// <summary>Shared HTTP + JSON helpers for the provider clients.</summary>
internal static class LlmHttp
{
    /// <summary>Hard cap for provider HTTP response bodies.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>Hard cap for the model's JSON text after extraction.</summary>
    public const int MaxJsonChars = 64 * 1024;

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    public static HttpContent JsonBody(object payload) =>
        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, JsonMediaType);

    /// <summary>
    /// Sends the request and reads the (capped) body. Network / stream failures during the send, the header phase
    /// AND the body read (e.g. a connection reset mid-body) are classified identically as transient
    /// <see cref="LlmException"/>s so the orchestrator retries them once. Caller cancellation propagates unchanged.
    /// </summary>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage message, string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new LlmException($"{provider}: HTTP {status}.", IsTransientStatus(response.StatusCode));
            }

            return await ReadCappedAsync(response.Content, provider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new LlmException($"{provider}: HTTP client timeout.", isTransient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"{provider}: network error.", isTransient: true, ex);
        }
        catch (IOException ex)
        {
            throw new LlmException($"{provider}: network stream error.", isTransient: true, ex);
        }
    }

    /// <summary>Parses a caller-supplied JSON Schema into an element that serializes verbatim into the payload.</summary>
    public static JsonElement ParseSchema(string schema, string provider)
    {
        try
        {
            using var document = JsonDocument.Parse(schema);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LlmException($"{provider}: JSON schema must be an object.", isTransient: false);
            }

            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new LlmException($"{provider}: JSON schema is not valid JSON.", isTransient: false, ex);
        }
    }

    public static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code is 408 or 425 or 429 || code >= 500;
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, string provider, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new LlmResponseFormatException($"{provider}: response exceeds {MaxResponseBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxResponseBytes)
        {
            throw new LlmResponseFormatException($"{provider}: response exceeds {MaxResponseBytes} bytes.");
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public static JsonDocument ParseEnvelope(string body, string provider)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LlmResponseFormatException($"{provider}: response envelope is not valid JSON.", ex);
        }
    }

    public static int ReadInt(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= 0
            ? number
            : 0;

    /// <summary>
    /// Validates that the model text is exactly one top-level JSON object. The ONLY tolerated wrapping is a single
    /// clean markdown fence (<c>```json\n{...}\n```</c> or <c>```\n{...}\n```</c>) around the whole trimmed payload.
    /// Any prose before/after the object or the fence, unterminated fences, non-object roots and trailing content are
    /// rejected with <see cref="LlmResponseFormatException"/>.
    /// </summary>
    public static string ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmResponseFormatException("Model returned empty content.");
        }

        if (text.Length > MaxJsonChars)
        {
            throw new LlmResponseFormatException($"Model content exceeds {MaxJsonChars} characters.");
        }

        var candidate = text.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = candidate.IndexOf('\n');
            if (firstNewLine < 0 || candidate.Length < firstNewLine + 4 ||
                !candidate.EndsWith("```", StringComparison.Ordinal))
            {
                throw new LlmResponseFormatException("Model returned an unterminated code fence.");
            }

            var language = candidate[3..firstNewLine].Trim();
            if (language.Length != 0 && !string.Equals(language, "json", StringComparison.OrdinalIgnoreCase))
            {
                throw new LlmResponseFormatException("Model returned a non-JSON code fence.");
            }

            candidate = candidate[(firstNewLine + 1)..^3].Trim();
        }

        if (!candidate.StartsWith('{') || !candidate.EndsWith('}'))
        {
            throw new LlmResponseFormatException("Model content is not a pure JSON object (prose wrapping rejected).");
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LlmResponseFormatException("Model content is not a JSON object.");
            }
        }
        catch (JsonException ex)
        {
            throw new LlmResponseFormatException("Model content is not valid JSON.", ex);
        }

        return candidate;
    }
}
