using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using SentiX.Domain;
using SentiX.NativeBridge;

namespace SentiX.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeSocketCollection
{
    public const string Name = "Native socket integration";
}

[Collection(NativeSocketCollection.Name)]
public sealed class NativeBridgeTests
{
    [Fact]
    public void ScoreRequestUsesCanonicalWireNamesAndUtcTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.FromHours(-5));
        var request = new IpcEnvelope<MarketEvent>(1, Guid.NewGuid(), IpcMessageType.ScoreEvent,
            timestamp, CreateEvent() with { PublishedAtUtc = timestamp });

        var wire = JsonSerializer.SerializeToElement(request, SentiXJson.Options);
        Assert.Equal("SCORE_EVENT", wire.GetProperty("type").GetString());
        Assert.Equal("2026-11-26T14:30:00Z", wire.GetProperty("timestampUtc").GetString());
        var marketEvent = wire.GetProperty("payload");
        Assert.Equal("ALPACA_BENZINGA", marketEvent.GetProperty("source").GetString());
        Assert.Equal("2026-11-26T14:30:00Z", marketEvent.GetProperty("publishedAtUtc").GetString());
        Assert.Equal("AAPL", marketEvent.GetProperty("sourceTickers")[0].GetString());
    }

    [UnixSocketFact]
    public async Task HealthAndScoreRoundTripThroughPersistentConnections()
    {
        await using var server = new MockUnixServer((stream, request, token) => request.Type switch
        {
            IpcMessageType.HealthCheck => ReplyHealthAsync(stream, request, token),
            IpcMessageType.ScoreEvent => ReplyAsync(stream, request, IpcMessageType.ScoreEventResult,
                new FastPathResult(request.Payload.GetProperty("eventId").GetString()!, false, "sha256:fake",
                    ["AAPL"], ["XLK"], new FinbertScores(0.85, 0.05, 0.10, 0.80), true, null, 20), token),
            _ => throw new InvalidDataException("Unexpected request type.")
        });

        using var bridge = new NativeBridgeClient(server.Path, poolSize: 2);
        var health = await bridge.HealthCheckAsync();
        Assert.False(health.IsFailClosed);
        Assert.Equal(NativeStatus.Ready, health.Value?.Status);

        for (int i = 0; i < 5; i++)
        {
            var scored = await bridge.ScoreEventAsync(CreateEvent($"news-{i}"));
            Assert.False(scored.IsFailClosed);
            Assert.True(scored.PassToDecisionSwarm);
            Assert.Equal($"news-{i}", scored.Value?.EventId);
        }

        Assert.Equal(2, server.ConnectionCount);
    }

    [UnixSocketFact]
    public async Task DegradedHealthPreventsSendingScoreRequest()
    {
        await using var server = new MockUnixServer((stream, request, token) =>
        {
            Assert.Equal(IpcMessageType.HealthCheck, request.Type);
            return ReplyHealthAsync(stream, request, token, NativeStatus.Degraded);
        });
        using var bridge = new NativeBridgeClient(server.Path);

        var result = await bridge.ScoreEventAsync(CreateEvent());
        Assert.True(result.IsFailClosed);
        Assert.False(result.PassToDecisionSwarm);
        Assert.Equal(NativeFailure.NotReady, result.Failure);
        Assert.Equal(1, server.RequestCount);
    }

    [UnixSocketFact]
    public async Task ScoreTimeoutAndImmediateBackoffFailClosed()
    {
        await using var server = new MockUnixServer((stream, request, token) =>
            request.Type == IpcMessageType.HealthCheck
                ? ReplyHealthAsync(stream, request, token)
                : Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var bridge = new NativeBridgeClient(server.Path, timeProvider: new ManualClock());

        var timedOut = await bridge.ScoreEventAsync(CreateEvent());
        Assert.True(timedOut.IsFailClosed);
        Assert.False(timedOut.PassToDecisionSwarm);
        Assert.Equal(NativeFailure.Timeout, timedOut.Failure);
        Assert.Equal(2, server.RequestCount);

        var backoff = await bridge.HealthCheckAsync();
        Assert.Equal(NativeFailure.ReconnectBackoff, backoff.Failure);
        Assert.Equal(1, server.ConnectionCount);
    }

    [UnixSocketFact]
    public async Task MidFlightDisconnectFailsClosed()
    {
        await using var server = new MockUnixServer((stream, request, token) =>
        {
            if (request.Type == IpcMessageType.HealthCheck)
            {
                return ReplyHealthAsync(stream, request, token);
            }

            stream.Close();
            return Task.CompletedTask;
        });
        using var bridge = new NativeBridgeClient(server.Path);

        var result = await bridge.ScoreEventAsync(CreateEvent());
        Assert.True(result.IsFailClosed);
        Assert.False(result.PassToDecisionSwarm);
        Assert.Equal(NativeFailure.Unavailable, result.Failure);
    }

    [UnixSocketFact]
    public async Task ReconnectsAfterBackoffAndResetsDelayOnSuccess()
    {
        using var socketPath = new TestSocketPath();
        var clock = new ManualClock();
        using var bridge = new NativeBridgeClient(socketPath.Path, timeProvider: clock);

        foreach (int delay in new[] { 100, 250, 500, 1000, 2000, 4000, 5000, 5000 })
        {
            var offline = await bridge.ScoreEventAsync(CreateEvent());
            Assert.True(offline.IsFailClosed);
            Assert.False(offline.PassToDecisionSwarm);
            Assert.Equal(NativeFailure.Unavailable, offline.Failure);

            clock.Advance(delay - 1);
            Assert.Equal(NativeFailure.ReconnectBackoff, (await bridge.HealthCheckAsync()).Failure);
            clock.Advance(1);
        }

        await using (var server = new MockUnixServer(
            (stream, request, token) => ReplyHealthAsync(stream, request, token), socketPath.Path))
        {
            Assert.False((await bridge.HealthCheckAsync()).IsFailClosed);
        }

        Assert.Equal(NativeFailure.Unavailable, (await bridge.HealthCheckAsync()).Failure);
        clock.Advance(100);
        Assert.Equal(NativeFailure.Unavailable, (await bridge.HealthCheckAsync()).Failure);
    }

    [UnixSocketFact]
    public async Task OversizedReplyAndRequestFailClosedWithPayloadError()
    {
        await using var server = new MockUnixServer((stream, _, token) =>
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, 1_048_577);
            return stream.WriteAsync(prefix, token).AsTask();
        });
        using var bridge = new NativeBridgeClient(server.Path);

        var oversizedRequest = await bridge.ScoreEventAsync(CreateEvent() with { Body = new string('a', 1_048_577) });
        Assert.True(oversizedRequest.IsFailClosed);
        Assert.False(oversizedRequest.PassToDecisionSwarm);
        Assert.Equal(IpcErrorCode.PayloadTooLarge, oversizedRequest.ErrorCode);
        Assert.Equal(0, server.ConnectionCount);

        var oversizedReply = await bridge.HealthCheckAsync();
        Assert.True(oversizedReply.IsFailClosed);
        Assert.Equal(IpcErrorCode.PayloadTooLarge, oversizedReply.ErrorCode);
    }

    [UnixSocketFact]
    public async Task NativeErrorAndMismatchedCorrelationBothFailClosed()
    {
        await using var server = new MockUnixServer((stream, request, token) =>
            ReplyAsync(stream, request, IpcMessageType.Error,
                new IpcError(IpcErrorCode.ModelNotReady, "Native model unavailable.", true), token));
        using var bridge = new NativeBridgeClient(server.Path);
        var response = await bridge.ScoreEventAsync(CreateEvent());
        Assert.True(response.IsFailClosed);
        Assert.False(response.PassToDecisionSwarm);
        Assert.Equal(NativeFailure.RemoteError, response.Failure);
        Assert.Equal(IpcErrorCode.ModelNotReady, response.ErrorCode);

        await using var wrong = new MockUnixServer((stream, request, token) =>
            ReplyHealthAsync(stream, request with { CorrelationId = Guid.NewGuid() }, token));
        using var otherBridge = new NativeBridgeClient(wrong.Path);
        var mismatch = await otherBridge.HealthCheckAsync();
        Assert.True(mismatch.IsFailClosed);
        Assert.Equal(NativeFailure.ProtocolError, mismatch.Failure);
    }

    [UnixSocketFact]
    public async Task ReloadUsesItsOwnMessageAndResponseContract()
    {
        await using var server = new MockUnixServer((stream, request, token) =>
        {
            Assert.Equal(IpcMessageType.ReloadConfig, request.Type);
            Assert.Empty(request.Payload.EnumerateObject());
            return ReplyAsync(stream, request, IpcMessageType.ReloadConfigAck,
                new ConfigReload("RELOADED", 1, 10, 8, 7), token);
        });
        using var bridge = new NativeBridgeClient(server.Path);

        var result = await bridge.ReloadConfigAsync();
        Assert.False(result.IsFailClosed);
        Assert.Equal(10, result.Value?.WatchlistCount);
    }

    internal static MarketEvent CreateEvent(string id = "event") => new(
        id, EventSource.AlpacaBenzinga, "Benzinga", DateTimeOffset.UtcNow,
        "Apple announces record demand", "Summary", "<p>Growth.</p>", ["AAPL"]);

    private static Task ReplyHealthAsync(Stream stream, IpcEnvelope<JsonElement> request,
        CancellationToken token, NativeStatus status = NativeStatus.Ready) =>
        ReplyAsync(stream, request, IpcMessageType.HealthCheckAck,
            new NativeHealth(status, "0.1.0", status == NativeStatus.Ready, 10, 0), token);

    private static async Task ReplyAsync<T>(Stream stream, IpcEnvelope<JsonElement> request,
        IpcMessageType responseType, T payload, CancellationToken token)
    {
        var response = new IpcEnvelope<T>(1, request.CorrelationId, responseType, DateTimeOffset.UtcNow, payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, SentiXJson.Options);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)bytes.Length);
        // Deliberately split the prefix to exercise stream reassembly.
        await stream.WriteAsync(prefix.AsMemory(0, 2), token);
        await stream.WriteAsync(prefix.AsMemory(2, 2), token);
        await stream.WriteAsync(bytes, token);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public void Advance(int delay) => milliseconds += delay;
    }
}

internal sealed class UnixSocketFactAttribute : FactAttribute
{
    public UnixSocketFactAttribute()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Skip = "The operating system does not support AF_UNIX.";
        }
    }
}

internal sealed class TestSocketPath : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sx-").FullName;
    public string Path => System.IO.Path.Combine(directory, "native.sock");
    public void Dispose()
    {
        File.Delete(Path);
        Directory.Delete(directory);
    }
}

internal sealed class MockUnixServer : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly Task server;
    private readonly TestSocketPath? ownedPath;
    private readonly Func<Stream, IpcEnvelope<JsonElement>, CancellationToken, Task> handle;
    private int connectionCount;
    private int requestCount;

    public MockUnixServer(Func<Stream, IpcEnvelope<JsonElement>, CancellationToken, Task> handle, string? path = null)
    {
        this.handle = handle;
        ownedPath = path is null ? new TestSocketPath() : null;
        Path = path ?? ownedPath!.Path;
        listener.Bind(new UnixDomainSocketEndPoint(Path));
        listener.Listen(4);
        server = RunAsync(shutdown.Token);
    }

    public string Path { get; }
    public int ConnectionCount => Volatile.Read(ref connectionCount);
    public int RequestCount => Volatile.Read(ref requestCount);

    private async Task RunAsync(CancellationToken token)
    {
        var clients = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptAsync(token);
                Interlocked.Increment(ref connectionCount);
                clients.Add(ReadRequestsAsync(socket, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients);
        }
    }

    private async Task ReadRequestsAsync(Socket socket, CancellationToken token)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var prefix = new byte[4];
            while (stream.CanRead)
            {
                await stream.ReadExactlyAsync(prefix, token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                Assert.InRange(length, 1, 1_048_576);
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, token);
                var request = JsonSerializer.Deserialize<IpcEnvelope<JsonElement>>(bytes, SentiXJson.Options);
                Assert.NotNull(request);
                Assert.Equal(1, request.ProtocolVersion);
                Interlocked.Increment(ref requestCount);
                await handle(stream, request, token);
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        try
        {
            await server;
        }
        finally
        {
            listener.Dispose();
            File.Delete(Path);
            ownedPath?.Dispose();
            shutdown.Dispose();
        }
    }
}
