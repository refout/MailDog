using System.Buffers.Binary;
using MailDog.Contracts.Ipc;

namespace MailDog.Contracts.Serialization;

/// <summary>4 字节小端长度前缀 + UTF-8 JSON 的帧读写。</summary>
public static class IpcFrame
{
    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame size {payload.Length} is out of range.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            return null;
        }

        if (read < 4)
        {
            throw new EndOfStreamException("IPC frame header was truncated.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame size {length} is out of range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}

public static class IpcJson
{
    public static byte[] Serialize(IpcEnvelope envelope)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJsonContext.Default.IpcEnvelope);

    public static IpcEnvelope Deserialize(ReadOnlySpan<byte> payload)
        => System.Text.Json.JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcEnvelope)
           ?? throw new System.Text.Json.JsonException("IPC envelope was null.");
}
