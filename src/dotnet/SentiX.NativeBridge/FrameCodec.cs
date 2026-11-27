using System.Buffers.Binary;
using SentiX.Domain;

namespace SentiX.NativeBridge;

internal static class FrameCodec
{
    public const int MaxPayloadBytes = 1_048_576;

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        ValidateLength((uint)payload.Length);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        ValidateLength(length);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static void ValidateLength(uint length)
    {
        if (length == 0)
        {
            throw new FrameException(IpcErrorCode.InvalidFrame, "IPC frame is empty.");
        }

        if (length > MaxPayloadBytes)
        {
            throw new FrameException(IpcErrorCode.PayloadTooLarge, "IPC frame exceeds 1 MiB.");
        }
    }
}

internal sealed class FrameException(IpcErrorCode code, string message) : IOException(message)
{
    public IpcErrorCode Code { get; } = code;
}
