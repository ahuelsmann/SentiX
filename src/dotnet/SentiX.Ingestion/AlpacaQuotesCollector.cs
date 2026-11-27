using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentiX.Domain;

namespace SentiX.Ingestion;

public sealed class InMemoryMarketQuoteCache : IMarketQuoteCache
{
    private readonly ConcurrentDictionary<string, MarketQuoteSnapshot> quotes = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string symbol, out MarketQuoteSnapshot? snapshot) => quotes.TryGetValue(symbol.Trim(), out snapshot);

    public void Update(MarketQuoteSnapshot snapshot) =>
        quotes.AddOrUpdate(snapshot.Symbol.Trim(), snapshot,
            (_, existing) => snapshot.TimestampUtc >= existing.TimestampUtc ? snapshot : existing);

    public IReadOnlyCollection<MarketQuoteSnapshot> GetAll() =>
        quotes.Values.OrderBy(static q => q.Symbol, StringComparer.Ordinal).ToArray();
}

public sealed class AlpacaQuotesCollector(
    AlpacaOptions options,
    UniverseConfig universe,
    IMarketQuoteCache cache,
    TimeProvider clock,
    ILogger<AlpacaQuotesCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UniverseConfigValidator.Validate(universe);
        if (!options.HasCredentials)
        {
            logger.LogInformation("Alpaca quotes ingestion skipped because credentials are missing.");
            return;
        }

        var symbols = universe.EquitiesSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int retrySeconds = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("APCA-API-KEY-ID", options.ApiKey!.Trim());
                socket.Options.SetRequestHeader("APCA-API-SECRET-KEY", options.SecretKey!.Trim());
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);

                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(new Uri(AlpacaNewsCollector.EquitiesWebSocketEndpoint), handshake.Token);
                    await AlpacaNewsCollector.ExpectAsync(socket, "success", "authenticated", handshake.Token);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        action = "subscribe", quotes = symbols
                    }).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, handshake.Token);
                }

                while (socket.State == WebSocketState.Open)
                {
                    using var messages = await AlpacaNewsCollector.ReceiveAsync(socket, stoppingToken);
                    if (ApplyQuotes(messages.RootElement, symbols, cache) > 0)
                    {
                        retrySeconds = 1;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or OperationCanceledException)
            {
                logger.LogWarning("Alpaca quotes stream interrupted ({ErrorType}). Reconnecting in {Seconds}s.",
                    ex.GetType().Name, retrySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), clock, stoppingToken);
            retrySeconds = Math.Min(retrySeconds * 2, 60);
        }
    }

    internal static int ApplyQuotes(JsonElement messages, IReadOnlySet<string> symbols, IMarketQuoteCache cache)
    {
        int count = 0;
        foreach (var item in messages.EnumerateArray())
        {
            AlpacaNewsCollector.RejectServerError(item);
            if (AlpacaNewsCollector.ReadType(item) != "q" || !item.TryGetProperty("S", out var symbolElement) ||
                symbolElement.ValueKind != JsonValueKind.String || symbolElement.GetString() is not { } symbol ||
                !symbols.Contains(symbol) || !TryReadPrice(item, "bp", out var bid) ||
                !TryReadPrice(item, "ap", out var ask) || ask < bid ||
                !item.TryGetProperty("t", out var ts) || ts.ValueKind != JsonValueKind.String ||
                !ts.TryGetDateTimeOffset(out var timestamp))
            {
                continue;
            }

            var mid = bid + (ask - bid) / 2;
            var spreadBps = (double)(ask - bid) / (double)mid * 10_000;
            cache.Update(new MarketQuoteSnapshot(symbol.ToUpperInvariant(), bid, ask, mid, spreadBps, timestamp.ToUniversalTime()));
            count++;
        }

        return count;
    }

    private static bool TryReadPrice(JsonElement item, string property, out decimal price)
    {
        price = 0;
        return item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out price) && price > 0;
    }
}
