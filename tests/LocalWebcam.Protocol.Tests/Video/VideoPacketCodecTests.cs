using System.Buffers.Binary;
using LocalWebcam.Protocol.Video;

namespace LocalWebcam.Protocol.Tests.Video;

public class VideoPacketCodecTests
{
    /// <summary>Hand-builds a version-1 (original, pre-FEC) packet - the format a not-yet-rebuilt phone app would still send.</summary>
    private static byte[] EncodeLegacyV1(uint frameId, ushort fragIndex, ushort fragCount, ulong timestampMicros, uint sequenceNumber, bool isKeyFrame, byte[] fragment)
    {
        var buffer = new byte[22 + fragment.Length];
        buffer[0] = 1; // legacy ProtocolVersion
        buffer[1] = isKeyFrame ? (byte)0x01 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), fragIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), fragCount);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(10, 8), timestampMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(18, 4), sequenceNumber);
        fragment.CopyTo(buffer.AsSpan(22));
        return buffer;
    }

    [Fact]
    public void TryDecode_StillUnderstandsLegacyV1Format()
    {
        var fragment = new byte[] { 10, 20, 30, 40 };
        var packet = EncodeLegacyV1(frameId: 42, fragIndex: 1, fragCount: 3, timestampMicros: 123456789, sequenceNumber: 7, isKeyFrame: true, fragment);

        var ok = VideoPacketCodec.TryDecode(packet, out var header, out var decodedFragment);

        Assert.True(ok);
        Assert.Equal(42u, header.FrameId);
        Assert.Equal(1, header.FragIndex);
        Assert.Equal(3, header.FragCount);
        Assert.Equal(123456789u, header.TimestampMicros);
        Assert.Equal(7u, header.SequenceNumber);
        Assert.True(header.IsKeyFrame);
        Assert.False(header.IsParity);
        Assert.Equal(fragment, decodedFragment.ToArray());
    }

    [Fact]
    public void Encode_ThenTryDecode_RoundTrips()
    {
        var fragment = new byte[] { 10, 20, 30, 40 };

        var packet = VideoPacketCodec.Encode(frameId: 42, fragIndex: 1, fragCount: 3, timestampMicros: 123456789, sequenceNumber: 7, isKeyFrame: true, isParity: false, totalLength: 4, fragment);

        var ok = VideoPacketCodec.TryDecode(packet, out var header, out var decodedFragment);

        Assert.True(ok);
        Assert.Equal(42u, header.FrameId);
        Assert.Equal(1, header.FragIndex);
        Assert.Equal(3, header.FragCount);
        Assert.Equal(123456789u, header.TimestampMicros);
        Assert.Equal(7u, header.SequenceNumber);
        Assert.True(header.IsKeyFrame);
        Assert.False(header.IsParity);
        Assert.Equal(4u, header.TotalLength);
        Assert.Equal(fragment, decodedFragment.ToArray());
    }

    [Fact]
    public void Encode_NonKeyFrame_DecodesIsKeyFrameFalse()
    {
        var packet = VideoPacketCodec.Encode(1, 0, 1, 0, 0, isKeyFrame: false, isParity: false, totalLength: 1, [1]);

        VideoPacketCodec.TryDecode(packet, out var header, out _);

        Assert.False(header.IsKeyFrame);
    }

    [Fact]
    public void Encode_Parity_RoundTrips()
    {
        var packet = VideoPacketCodec.Encode(1, fragIndex: 0, fragCount: 4, timestampMicros: 0, sequenceNumber: 0, isKeyFrame: false, isParity: true, totalLength: 5000, [9, 9, 9]);

        var ok = VideoPacketCodec.TryDecode(packet, out var header, out var fragment);

        Assert.True(ok);
        Assert.True(header.IsParity);
        Assert.Equal(5000u, header.TotalLength);
        Assert.Equal(new byte[] { 9, 9, 9 }, fragment.ToArray());
    }

    [Fact]
    public void TryDecode_RejectsTooShortDatagram()
    {
        var ok = VideoPacketCodec.TryDecode(new byte[] { 1, 2, 3 }, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_RejectsWrongVersion()
    {
        var packet = VideoPacketCodec.Encode(1, 0, 1, 0, 0, false, false, 3, [1, 2, 3]);
        packet[0] = 99;

        var ok = VideoPacketCodec.TryDecode(packet, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_RejectsZeroFragCount()
    {
        var packet = VideoPacketCodec.Encode(1, 0, 1, 0, 0, false, false, 3, [1, 2, 3]);
        packet[8] = 0;
        packet[9] = 0; // FragCount = 0

        var ok = VideoPacketCodec.TryDecode(packet, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_RejectsFragIndexGreaterOrEqualToFragCount()
    {
        // FragIndex=5, FragCount=3 is internally inconsistent - reject rather
        // than let a receiver index out of bounds on the caller's side.
        var packet = VideoPacketCodec.Encode(frameId: 1, fragIndex: 0, fragCount: 3, timestampMicros: 0, sequenceNumber: 0, isKeyFrame: false, isParity: false, totalLength: 1, [1]);
        packet[6] = 5;
        packet[7] = 0; // FragIndex = 5

        var ok = VideoPacketCodec.TryDecode(packet, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Encode_WithEmptyFragment_RoundTrips()
    {
        var packet = VideoPacketCodec.Encode(1, 0, 1, 0, 0, false, false, 0, ReadOnlySpan<byte>.Empty);

        var ok = VideoPacketCodec.TryDecode(packet, out _, out var fragment);

        Assert.True(ok);
        Assert.Empty(fragment.ToArray());
    }
}
