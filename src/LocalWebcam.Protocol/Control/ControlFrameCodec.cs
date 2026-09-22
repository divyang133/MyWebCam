using System.Buffers.Binary;

namespace LocalWebcam.Protocol.Control;

/// <summary>
/// Binary framing for the control channel (docs/PROTOCOL.md):
/// <c>[Version:1][MsgType:1][Length:u32 LE][Payload]</c>. Handles TCP's
/// partial-read behavior correctly (a <see cref="Stream.ReadAsync"/> call is
/// not guaranteed to return the full frame in one call) and validates
/// version/length before trusting anything from the wire (spec section 22).
/// </summary>
public static class ControlFrameCodec
{
    public const byte ProtocolVersion = 1;

    private const int HeaderLength = 1 + 1 + 4;

    /// <summary>Sanity cap on control payload size; video never rides this channel, so control messages are always small.</summary>
    public const int MaxPayloadLength = 1024 * 1024;

    public static byte[] Encode(ControlMessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException($"Control payload of {payload.Length} bytes exceeds the {MaxPayloadLength}-byte limit.", nameof(payload));
        }

        var buffer = new byte[HeaderLength + payload.Length];
        buffer[0] = ProtocolVersion;
        buffer[1] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }

    public static async Task<(ControlMessageType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await ReadExactAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);

        var version = header[0];
        if (version != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported control protocol version {version} (expected {ProtocolVersion}).");
        }

        var type = (ControlMessageType)header[1];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        if (length > MaxPayloadLength)
        {
            throw new InvalidDataException($"Control frame payload length {length} exceeds the {MaxPayloadLength}-byte limit.");
        }

        var payload = length == 0 ? [] : await ReadExactAsync(stream, (int)length, cancellationToken).ConfigureAwait(false);
        return (type, payload);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The connection was closed while reading a control frame.");
            }

            offset += read;
        }

        return buffer;
    }
}
