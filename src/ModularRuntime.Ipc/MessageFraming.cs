using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc;

/// <summary>Protocol-level failure: malformed frame, oversized message, invalid JSON, or truncated stream.</summary>
public sealed class IpcProtocolException(string code, string message) : Exception(message)
{
    /// <summary>One of <see cref="IpcErrorCodes"/>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Reads and writes frames: <c>[uint32 little-endian length][UTF-8 JSON]</c>.
/// </summary>
/// <remarks>
/// Reads never assume that one read returns a whole frame. A clean end of stream before a frame
/// starts returns <see langword="null"/>; an end of stream inside a frame, a zero or oversized
/// length, or invalid JSON throws <see cref="IpcProtocolException"/>. The payload buffer is
/// allocated only after the length was validated, so a hostile length cannot force a large allocation.
/// </remarks>
public static class MessageFraming
{
    private const int HeaderSize = sizeof(uint);

    public static async ValueTask WriteAsync(Stream stream, IpcMessage message, int maxMessageBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcProtocol.JsonOptions);
        if (json.Length > maxMessageBytes)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.MessageTooLarge,
                $"Message of {json.Length} bytes exceeds the limit of {maxMessageBytes} bytes.");
        }

        var frame = ArrayPool<byte>.Shared.Rent(HeaderSize + json.Length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
            json.CopyTo(frame, HeaderSize);
            await stream.WriteAsync(frame.AsMemory(0, HeaderSize + json.Length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Reads one frame, or returns <see langword="null"/> on a clean end of stream.</summary>
    public static async ValueTask<IpcMessage?> ReadAsync(Stream stream, int maxMessageBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        var headerRead = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderSize)
        {
            throw new IpcProtocolException(IpcErrorCodes.InvalidMessage, "The stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            throw new IpcProtocolException(IpcErrorCodes.InvalidMessage, "Zero-length frames are not allowed.");
        }

        if (length > (uint)maxMessageBytes)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.MessageTooLarge,
                $"Frame of {length} bytes exceeds the limit of {maxMessageBytes} bytes.");
        }

        var payload = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            var payloadRead = await ReadAtLeastAsync(stream, payload.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
            if (payloadRead < length)
            {
                throw new IpcProtocolException(IpcErrorCodes.InvalidMessage, "The stream ended inside a frame payload.");
            }

            IpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<IpcMessage>(payload.AsSpan(0, (int)length), IpcProtocol.JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new IpcProtocolException(IpcErrorCodes.InvalidMessage, $"Invalid JSON message: {exception.Message}");
            }

            if (message is null || string.IsNullOrEmpty(message.Type))
            {
                throw new IpcProtocolException(IpcErrorCodes.InvalidMessage, "The message has no type.");
            }

            return message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends; returns the number of bytes read.</summary>
    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
