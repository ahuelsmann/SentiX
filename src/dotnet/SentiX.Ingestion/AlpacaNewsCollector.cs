using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentiX.Domain;

namespace SentiX.Ingestion;

public sealed class AlpacaNewsCollector(
    AlpacaOptions options,
    UniverseConfig universe,
    IMarketEventPipeline pipeline,
    ILogger<AlpacaNewsCollector> logger) : BackgroundService
{
    public const string NewsWebSocketEndpoint = "wss://stream.data.alpaca.markets/v1beta1/news";
    public const string EquitiesWebSocketEndpoint = "wss://stream.data.alpaca.markets/v2/iex";
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.HasCredentials)
        {
            logger.LogInformation("Alpaca news ingestion skipped because credentials are missing.");
            return;
        }

        if (!pipeline.IsConfigured)
        {
            logger.LogInformation("Alpaca news ingestion skipped because PostgreSQL is unconfigured.");
            return;
        }

        int retrySeconds = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pipeline.EnsureReadyAsync(stoppingToken);
                await CollectAsync(() => retrySeconds = 1, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or
                NpgsqlException or DbUpdateException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("Alpaca news stream interrupted ({ErrorType}). Reconnecting in {Seconds}s.",
                    ex.GetType().Name, retrySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
            retrySeconds = Math.Min(retrySeconds * 2, 60);
        }
    }

    private async Task CollectAsync(Action receivedNews, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("APCA-API-KEY-ID", options.ApiKey!.Trim());
        socket.Options.SetRequestHeader("APCA-API-SECRET-KEY", options.SecretKey!.Trim());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri(NewsWebSocketEndpoint), handshake.Token);
            await ExpectAsync(socket, "success", "authenticated", handshake.Token);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
            {
                action = "subscribe",
                news = universe.EquitiesSymbols
            }).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, handshake.Token);
        }

        while (socket.State == WebSocketState.Open)
        {
            using var messages = await ReceiveAsync(socket, token);
            foreach (var item in messages.RootElement.EnumerateArray())
            {
                RejectServerError(item);
                if (ReadType(item) == "n")
                {
                    NewsArticle? article;
                    try
                    {
                        article = item.Deserialize<NewsArticle>(SentiXJson.Options);
                    }
                    catch (JsonException)
                    {
                        logger.LogWarning("Skipped an invalid Alpaca news article.");
                        continue;
                    }

                    if (article is not null)
                    {
                        // Each publisher revision keeps its own raw article and scoring audit.
                        var revision = (article.UpdatedAt ?? article.CreatedAt).UtcTicks;
                        var marketEvent = new MarketEvent(
                            FormattableString.Invariant($"alpaca:{article.Id}:{revision}"), EventSource.AlpacaBenzinga,
                            article.Author ?? "", article.CreatedAt, article.Headline, article.Summary ?? "",
                            article.Content ?? "", article.Symbols ?? []);
                        await pipeline.ProcessAsync(marketEvent, item.GetRawText(), token);
                        receivedNews();
                    }
                }
            }
        }
    }

    private static async Task ExpectAsync(ClientWebSocket socket, string type, string message, CancellationToken token)
    {
        while (true)
        {
            using var frame = await ReceiveAsync(socket, token);
            foreach (var item in frame.RootElement.EnumerateArray())
            {
                RejectServerError(item);
                if (ReadType(item) == type && item.TryGetProperty("msg", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == message)
                {
                    return;
                }
            }
        }
    }

    private static string? ReadType(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("T", out var type) &&
        type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static void RejectServerError(JsonElement item)
    {
        if (ReadType(item) == "error")
        {
            throw new IOException("Alpaca rejected the WebSocket operation.");
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var message = new ArrayBufferWriter<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(message.GetMemory(8192), token);
            if (result.MessageType != WebSocketMessageType.Text || message.WrittenCount + result.Count > MaxMessageBytes)
            {
                throw new IOException("Unexpected or oversized Alpaca WebSocket message.");
            }

            message.Advance(result.Count);
        }
        while (!result.EndOfMessage);

        var json = JsonDocument.Parse(message.WrittenMemory);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            json.Dispose();
            throw new JsonException("Expected an array of Alpaca WebSocket messages.");
        }

        return json;
    }

    private sealed record NewsArticle(
        long Id, string Headline, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null,
        string? Author = null, string? Summary = null, string? Content = null, string[]? Symbols = null);
}
