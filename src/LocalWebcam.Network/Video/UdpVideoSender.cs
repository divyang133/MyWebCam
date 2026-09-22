using System.Net;
using System.Net.Sockets;
using LocalWebcam.Protocol.Video;

namespace LocalWebcam.Network.Video;

/// <summary>
/// Splits each encoded frame into MTU-sized UDP datagrams (docs/VIDEO_TRANSPORT.md).
/// Frame IDs and sequence numbers are assigned monotonically so the receiver
/// can detect loss and reorder/drop stale frames.
/// </summary>
public sealed class UdpVideoSender : IVideoFrameSender
{
    /// <summary>Keeps the total UDP payload comfortably under the typical 1500-byte Ethernet/Wi-Fi MTU, avoiding IP fragmentation.</summary>
    public const int MaxFragmentPayloadSize = 1400;

    // A frame that only splits into a handful of fragments is already
    // unlikely to lose one, and the parity packet itself would be a large
    // relative overhead (up to +100% for a 1-fragment frame) for little
    // benefit - only worth sending once a frame is large enough that a
    // single lost UDP packet has a real chance of occurring. Confirmed on
    // real hardware: fast-motion content (a spinning fan, hand movement)
    // produces much larger delta frames than static content - more
    // fragments per frame, and UdpVideoReceiver requires every single one
    // to arrive before delivering the frame at all (see its class doc
    // comment), so exactly these larger, harder-to-encode frames were the
    // ones being dropped wholesale from a single lost packet.
    private const int MinFragmentsForParity = 4;

    private readonly UdpClient _client = new();
    private readonly IPEndPoint _target;
    private readonly Lock _sequenceLock = new();

    private uint _nextFrameId;
    private uint _nextSequenceNumber;

    public UdpVideoSender(IPEndPoint target)
    {
        _target = target;
    }

    public async Task SendFrameAsync(byte[] data, ulong timestampMicros, bool isKeyFrame, CancellationToken cancellationToken = default)
    {
        uint frameId;
        lock (_sequenceLock)
        {
            frameId = _nextFrameId++;
        }

        var fragmentCount = (ushort)Math.Max(1, (int)Math.Ceiling(data.Length / (double)MaxFragmentPayloadSize));
        var totalLength = (uint)data.Length;

        // XOR of every (zero-padded to MaxFragmentPayloadSize) data fragment -
        // recovers exactly one lost fragment on the receiving end, the same
        // way single-disk RAID-5 parity does. Padding with implicit zero
        // bytes past each fragment's real length is safe: XOR-ing in zeros
        // is a no-op, so it doesn't corrupt the parity for shorter fragments
        // (only ever the last one).
        byte[]? parity = fragmentCount >= MinFragmentsForParity ? new byte[MaxFragmentPayloadSize] : null;

        for (ushort fragIndex = 0; fragIndex < fragmentCount; fragIndex++)
        {
            var offset = fragIndex * MaxFragmentPayloadSize;
            var length = Math.Min(MaxFragmentPayloadSize, data.Length - offset);
            var fragment = data.AsSpan(offset, length);

            if (parity is not null)
            {
                XorInto(parity, fragment);
            }

            uint sequenceNumber;
            lock (_sequenceLock)
            {
                sequenceNumber = _nextSequenceNumber++;
            }

            var packet = VideoPacketCodec.Encode(frameId, fragIndex, fragmentCount, timestampMicros, sequenceNumber, isKeyFrame, isParity: false, totalLength, fragment);
            await _client.SendAsync(packet, _target, cancellationToken).ConfigureAwait(false);
        }

        if (parity is not null)
        {
            uint paritySequenceNumber;
            lock (_sequenceLock)
            {
                paritySequenceNumber = _nextSequenceNumber++;
            }

            var parityPacket = VideoPacketCodec.Encode(frameId, fragIndex: 0, fragmentCount, timestampMicros, paritySequenceNumber, isKeyFrame, isParity: true, totalLength, parity);
            await _client.SendAsync(parityPacket, _target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void XorInto(byte[] parity, ReadOnlySpan<byte> fragment)
    {
        for (var i = 0; i < fragment.Length; i++)
        {
            parity[i] ^= fragment[i];
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
