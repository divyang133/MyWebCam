using System.Buffers.Binary;

namespace LocalWebcam.Protocol.Video;

/// <summary>
/// Binary framing for video sent over a TCP stream (the USB/ADB transport -
/// see <c>LocalWebcam.Network.Video.TcpVideoSender</c>/<c>TcpVideoReceiver</c>):
/// <c>[FrameId:u32 LE][Flags:1][Timestamp:u64 LE][DataLength:u32 LE][Data]</c>.
/// Deliberately simpler than <see cref="VideoPacketCodec"/>: TCP already
/// guarantees in-order, complete, lossless delivery, so none of that
/// codec's fragmentation/sequence-number/parity machinery (built for
/// unreliable UDP datagrams) applies here - one call writes one whole
/// encoded frame.
/// </summary>
public static class TcpVideoFrameCodec
{
    private const int HeaderLength = 4 + 1 + 8 + 4;
    private const byte KeyFrameFlag = 0x01;

    /// <summary>Sanity cap mirroring <see cref="ControlFrameCodec"/>'s - an encoded video frame is never remotely this large.</summary>
    public const int MaxFrameLength = 32 * 1024 * 1024;

    public static byte[] Encode(uint frameId, ulong timestampMicros, bool isKeyFrame, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), frameId);
        buffer[4] = (byte)(isKeyFrame ? KeyFrameFlag : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(5, 8), timestampMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(13, 4), (uint)data.Length);
        data.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }

    public static async Task<(uint FrameId, ulong TimestampMicros, bool IsKeyFrame, byte[] Data)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await ReadExactAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);

        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        var isKeyFrame = (header[4] & KeyFrameFlag) != 0;
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(5, 8));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13, 4));

        if (length > MaxFrameLength)
        {
            throw new InvalidDataException($"TCP video frame length {length} exceeds the {MaxFrameLength}-byte limit.");
        }

        var data = length == 0 ? [] : await ReadExactAsync(stream, (int)length, cancellationToken).ConfigureAwait(false);
        return (frameId, timestamp, isKeyFrame, data);
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
                throw new EndOfStreamException("The connection was closed while reading a TCP video frame.");
            }

            offset += read;
        }

        return buffer;
    }
}
