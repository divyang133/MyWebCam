using System.Buffers.Binary;

namespace LocalWebcam.Protocol.Video;

/// <summary>
/// Binary framing for one UDP video packet (docs/VIDEO_TRANSPORT.md). Current
/// (version 2) format: <c>[Version:1][Flags:1][FrameId:u32 LE][FragIndex:u16 LE]
/// [FragCount:u16 LE][Timestamp:u64 LE][SeqNumber:u32 LE][TotalLength:u32 LE][Payload]</c>.
/// Deliberately separate from <see cref="LocalWebcam.Protocol.Control.ControlFrameCodec"/>
/// — this is unreliable, unordered UDP, not a TCP byte stream, and callers
/// must tolerate malformed/truncated datagrams from the network without
/// crashing (spec section 22).
///
/// <c>TotalLength</c> (the full encoded frame's byte length, same value on
/// every packet of a frame including the parity one) exists solely so a
/// receiver can recover a lost fragment via XOR (see
/// <see cref="LocalWebcam.Network.Video.UdpVideoSender"/>'s parity packet)
/// even when the missing fragment is the last, variably-sized one — without
/// it, the receiver would have no way to know how many of the reconstructed
/// (zero-padded) buffer's trailing bytes to discard.
///
/// <see cref="TryDecode"/> still understands version 1 (the original,
/// 22-byte-header format, no <c>TotalLength</c>/parity) alongside version 2:
/// the sender and receiver are both built from this same shared project, but
/// they run on two independently-deployed apps (the phone and the desktop
/// app), and the phone side is not necessarily rebuilt and reinstalled at
/// the same time this project changes. Without version 1 support, an
/// out-of-date phone sender's packets would be misparsed against the new,
/// 4-bytes-longer header - reading garbage into TotalLength and returning a
/// payload shifted by 4 bytes - corrupting every frame instead of just
/// occasionally dropping one. <see cref="Encode"/> always writes the current
/// version; only decoding needs to stay two-way compatible.
/// </summary>
public static class VideoPacketCodec
{
    public const byte ProtocolVersion = 2;
    private const byte LegacyProtocolVersionV1 = 1;

    private const int LegacyHeaderLengthV1 = 1 + 1 + 4 + 2 + 2 + 8 + 4;
    private const int HeaderLength = LegacyHeaderLengthV1 + 4; // + TotalLength:u32
    private const byte KeyFrameFlag = 0x01;
    private const byte ParityFlag = 0x02;

    public static byte[] Encode(uint frameId, ushort fragIndex, ushort fragCount, ulong timestampMicros, uint sequenceNumber, bool isKeyFrame, bool isParity, uint totalLength, ReadOnlySpan<byte> fragment)
    {
        var buffer = new byte[HeaderLength + fragment.Length];
        buffer[0] = ProtocolVersion;
        buffer[1] = (byte)((isKeyFrame ? KeyFrameFlag : 0) | (isParity ? ParityFlag : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), fragIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), fragCount);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(10, 8), timestampMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(18, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(22, 4), totalLength);
        fragment.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> datagram, out VideoPacketHeader header, out ReadOnlySpan<byte> fragment)
    {
        header = default;
        fragment = default;

        if (datagram.Length < LegacyHeaderLengthV1)
        {
            return false;
        }

        if (datagram[0] == LegacyProtocolVersionV1)
        {
            return TryDecodeLegacyV1(datagram, out header, out fragment);
        }

        if (datagram[0] != ProtocolVersion || datagram.Length < HeaderLength)
        {
            return false;
        }

        var flags = datagram[1];
        var isParity = (flags & ParityFlag) != 0;
        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(2, 4));
        var fragIndex = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(6, 2));
        var fragCount = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(8, 2));
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(10, 8));
        var sequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(18, 4));
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(22, 4));

        if (fragCount == 0 || (!isParity && fragIndex >= fragCount))
        {
            return false;
        }

        header = new VideoPacketHeader(frameId, fragIndex, fragCount, timestamp, sequenceNumber, (flags & KeyFrameFlag) != 0, isParity, totalLength);
        fragment = datagram[HeaderLength..];
        return true;
    }

    /// <summary>The original wire format: no TotalLength, no parity packets - see the class doc comment for why this still has to be understood.</summary>
    private static bool TryDecodeLegacyV1(ReadOnlySpan<byte> datagram, out VideoPacketHeader header, out ReadOnlySpan<byte> fragment)
    {
        header = default;
        fragment = default;

        var flags = datagram[1];
        var frameId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(2, 4));
        var fragIndex = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(6, 2));
        var fragCount = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(8, 2));
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(10, 8));
        var sequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(18, 4));

        if (fragCount == 0 || fragIndex >= fragCount)
        {
            return false;
        }

        header = new VideoPacketHeader(frameId, fragIndex, fragCount, timestamp, sequenceNumber, (flags & KeyFrameFlag) != 0, IsParity: false, TotalLength: 0);
        fragment = datagram[LegacyHeaderLengthV1..];
        return true;
    }
}
