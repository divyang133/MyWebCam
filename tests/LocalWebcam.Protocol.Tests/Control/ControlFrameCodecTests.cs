using LocalWebcam.Protocol.Control;

namespace LocalWebcam.Protocol.Tests.Control;

public class ControlFrameCodecTests
{
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public async Task Encode_ThenReadFrame_RoundTrips(byte[] payload)
    {
        var encoded = ControlFrameCodec.Encode(ControlMessageType.Hello, payload);
        using var stream = new MemoryStream(encoded);

        var (type, decoded) = await ControlFrameCodec.ReadFrameAsync(stream);

        Assert.Equal(ControlMessageType.Hello, type);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public async Task ReadFrameAsync_HandlesPartialReadsAcrossMultipleWrites()
    {
        // A one-shot-write stream would hide a bug where ReadFrameAsync
        // assumes a single ReadAsync call returns the whole frame - real TCP
        // sockets routinely split a frame across several reads.
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);
        var encoded = ControlFrameCodec.Encode(ControlMessageType.ConfigureStream, payload);

        await using var pipe = new SlowStream(encoded, chunkSize: 7);

        var (type, decoded) = await ControlFrameCodec.ReadFrameAsync(pipe);

        Assert.Equal(ControlMessageType.ConfigureStream, type);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsUnsupportedVersion()
    {
        var encoded = ControlFrameCodec.Encode(ControlMessageType.Hello, [1, 2, 3]);
        encoded[0] = 99; // corrupt the version byte
        using var stream = new MemoryStream(encoded);

        await Assert.ThrowsAsync<InvalidDataException>(() => ControlFrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsOversizedLength()
    {
        var header = new byte[6];
        header[0] = ControlFrameCodec.ProtocolVersion;
        header[1] = (byte)ControlMessageType.Hello;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), ControlFrameCodec.MaxPayloadLength + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => ControlFrameCodec.ReadFrameAsync(stream));
    }

    [Fact]
    public void Encode_RejectsPayloadOverLimit()
    {
        var oversized = new byte[ControlFrameCodec.MaxPayloadLength + 1];

        Assert.Throws<ArgumentException>(() => ControlFrameCodec.Encode(ControlMessageType.Hello, oversized));
    }

    [Fact]
    public async Task ReadFrameAsync_TruncatedStream_ThrowsEndOfStream()
    {
        var encoded = ControlFrameCodec.Encode(ControlMessageType.Hello, [1, 2, 3, 4, 5]);
        using var stream = new MemoryStream(encoded[..^2]); // cut off the last 2 payload bytes

        await Assert.ThrowsAsync<EndOfStreamException>(() => ControlFrameCodec.ReadFrameAsync(stream));
    }

    /// <summary>A stream that only ever returns up to <paramref name="chunkSize"/> bytes per ReadAsync call.</summary>
    private sealed class SlowStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var remaining = data.Length - _position;
            if (remaining == 0)
            {
                return 0;
            }

            var toCopy = Math.Min(Math.Min(chunkSize, buffer.Length), remaining);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
