using SentiX.Domain;

namespace SentiX.NativeBridge;

public enum NativeFailure { Unavailable, Timeout, NotReady, ProtocolError, RemoteError, ReconnectBackoff }

public sealed record NativeResult<T>(T? Value, NativeFailure? Failure = null, IpcErrorCode? ErrorCode = null)
    where T : class
{
    public bool IsFailClosed => Failure.HasValue;
    public bool PassToDecisionSwarm => !IsFailClosed && Value is FastPathResult { PassToDecisionSwarm: true };
}
