using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using SentiX.Domain;

namespace SentiX.NativeBridge;

public sealed class NativeBridgeClient : IDisposable
{
    public const string DefaultSocketPath = "/run/sentix/native.sock";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ScoreTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly int[] ReconnectDelaysMs = [100, 250, 500, 1000, 2000, 4000, 5000];

    private readonly UnixDomainSocketEndPoint endpoint;
    private readonly Channel<Connection> pool;
    private readonly Connection[] connections;
    private readonly TimeProvider timeProvider;
    private readonly object reconnectLock = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private long lastFailure;
    private int failureCount;
    private int failureVersion;
    private int disposed;

    public NativeBridgeClient(string socketPath = DefaultSocketPath, int poolSize = 3, TimeProvider? timeProvider = null)
    {
        if (poolSize is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize));
        }

        endpoint = new UnixDomainSocketEndPoint(socketPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        shutdownToken = shutdown.Token;
        pool = Channel.CreateBounded<Connection>(poolSize);
        connections = new Connection[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            connections[i] = new Connection();
            pool.Writer.TryWrite(connections[i]);
        }
    }

    public Task<NativeResult<NativeHealth>> HealthCheckAsync(CancellationToken cancellationToken = default) =>
        WithConnectionAsync(CheckHealthAsync, cancellationToken);

    public Task<NativeResult<ConfigReload>> ReloadConfigAsync(CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async (stream, token) =>
        {
            var response = await ExchangeAsync<ConfigReload>(
                stream, Request(IpcMessageType.ReloadConfig, new { }),
                IpcMessageType.ReloadConfigAck, ReloadTimeout, token);
            return response.Value is { Status: not "RELOADED" }
                ? new NativeResult<ConfigReload>(null, NativeFailure.ProtocolError)
                : response;
        }, cancellationToken);

    public Task<NativeResult<FastPathResult>> ScoreEventAsync(
        MarketEvent marketEvent, CancellationToken cancellationToken = default)
    {
        var request = Request(IpcMessageType.ScoreEvent, marketEvent);
        if (request.Bytes.Length > FrameCodec.MaxPayloadBytes)
        {
            return Task.FromResult(new NativeResult<FastPathResult>(
                null, NativeFailure.ProtocolError, IpcErrorCode.PayloadTooLarge));
        }

        return WithConnectionAsync(async (stream, token) =>
        {
            // A successful connection alone does not establish native readiness.
            var health = await CheckHealthAsync(stream, token);
            if (health.IsFailClosed)
            {
                return new NativeResult<FastPathResult>(null, health.Failure, health.ErrorCode);
            }

            var response = await ExchangeAsync<FastPathResult>(
                stream, request, IpcMessageType.ScoreEventResult, ScoreTimeout, token);
            if (response.Value is { } result &&
                (!string.Equals(result.EventId, marketEvent.EventId, StringComparison.Ordinal) ||
                    result.ProcessingTimeUs < 0 ||
                    (result.PassToDecisionSwarm && (result.IsDuplicate || result.DropReason.HasValue))))
            {
                return new NativeResult<FastPathResult>(null, NativeFailure.ProtocolError);
            }

            return response;
        }, cancellationToken);
    }

    private async Task<NativeResult<NativeHealth>> CheckHealthAsync(Stream stream, CancellationToken token)
    {
        var response = await ExchangeAsync<NativeHealth>(stream, Request(IpcMessageType.HealthCheck, new { }),
            IpcMessageType.HealthCheckAck, HealthTimeout, token);
        return response.Value is { } health && (health.Status != NativeStatus.Ready || !health.ModelLoaded)
            ? new NativeResult<NativeHealth>(health, NativeFailure.NotReady)
            : response;
    }

    private (Guid Id, byte[] Bytes) Request<T>(IpcMessageType type, T payload)
    {
        Guid id = Guid.NewGuid();
        var request = new IpcEnvelope<T>(1, id, type, timeProvider.GetUtcNow(), payload);
        return (id, JsonSerializer.SerializeToUtf8Bytes(request, SentiXJson.Options));
    }

    private static async Task<NativeResult<T>> ExchangeAsync<T>(
        Stream stream, (Guid Id, byte[] Bytes) request, IpcMessageType responseType,
        TimeSpan timeout, CancellationToken token) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await FrameCodec.WriteAsync(stream, request.Bytes, deadline.Token);
        var bytes = await FrameCodec.ReadAsync(stream, deadline.Token);
        var response = JsonSerializer.Deserialize<IpcEnvelope<JsonElement>>(bytes, SentiXJson.Options);

        if (response is null || response.ProtocolVersion != 1 || response.CorrelationId != request.Id)
        {
            return new NativeResult<T>(null, NativeFailure.ProtocolError);
        }

        if (response.Type == IpcMessageType.Error &&
            response.Payload.Deserialize<IpcError>(SentiXJson.Options) is { } error)
        {
            return new NativeResult<T>(null, NativeFailure.RemoteError, error.Code);
        }

        if (response.Type != responseType || response.Payload.Deserialize<T>(SentiXJson.Options) is not { } result)
        {
            return new NativeResult<T>(null, NativeFailure.ProtocolError);
        }

        return new NativeResult<T>(result);
    }

    private async Task<NativeResult<T>> WithConnectionAsync<T>(
        Func<Stream, CancellationToken, Task<NativeResult<T>>> use, CancellationToken cancellationToken)
        where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        var token = operation.Token;
        // Bound time spent waiting for a busy pool as well as network operations.
        using var checkout = CancellationTokenSource.CreateLinkedTokenSource(token);
        checkout.CancelAfter(ConnectTimeout);
        Connection connection;
        try
        {
            connection = await pool.Reader.ReadAsync(checkout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new NativeResult<T>(null, NativeFailure.Timeout);
        }

        try
        {
            int version;
            lock (reconnectLock)
            {
                if (failureCount > 0 &&
                    timeProvider.GetElapsedTime(lastFailure) <
                    TimeSpan.FromMilliseconds(ReconnectDelaysMs[failureCount - 1]))
                {
                    return new NativeResult<T>(null, NativeFailure.ReconnectBackoff);
                }

                version = failureVersion;
            }

            NativeResult<T> result;
            try
            {
                var stream = await connection.OpenAsync(endpoint, token);
                result = await use(stream, token);
            }
            catch (FrameException ex)
            {
                result = new NativeResult<T>(null, NativeFailure.ProtocolError, ex.Code);
            }
            catch (JsonException)
            {
                result = new NativeResult<T>(null, NativeFailure.ProtocolError);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                result = new NativeResult<T>(null, NativeFailure.Timeout);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                result = new NativeResult<T>(null, NativeFailure.Unavailable);
            }
            catch (OperationCanceledException)
            {
                connection.Close();
                throw;
            }

            lock (reconnectLock)
            {
                if (result.IsFailClosed)
                {
                    connection.Close();
                    if (version == failureVersion)
                    {
                        lastFailure = timeProvider.GetTimestamp();
                        failureCount = Math.Min(failureCount + 1, ReconnectDelaysMs.Length);
                        failureVersion++;
                    }
                }
                else if (version == failureVersion)
                {
                    failureCount = 0;
                }
                else
                {
                    // A concurrent failure supersedes this connection's success.
                    return new NativeResult<T>(null, NativeFailure.Unavailable);
                }
            }

            return result;
        }
        finally
        {
            if (!pool.Writer.TryWrite(connection))
            {
                connection.Close();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pool.Writer.TryComplete();
        shutdown.Cancel();
        while (pool.Reader.TryRead(out var connection))
        {
            connection.Close();
        }
        shutdown.Dispose();
    }

    private sealed class Connection
    {
        private NetworkStream? stream;

        public async Task<NetworkStream> OpenAsync(UnixDomainSocketEndPoint address, CancellationToken token)
        {
            if (stream is not null)
            {
                return stream;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ConnectTimeout);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(address, deadline.Token);
                stream = new NetworkStream(socket, ownsSocket: true);
                return stream;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void Close()
        {
            stream?.Dispose();
            stream = null;
        }
    }
}
