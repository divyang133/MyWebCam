using System.Net;
using System.Net.Sockets;
using LocalWebcam.Protocol.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Video;

/// <summary>
/// Reassembles UDP video packets into frames. When a fragment for a newer
/// <c>FrameId</c> arrives, any still-incomplete older frame is dropped
/// immediately rather than waited on — the "discard, don't buffer" policy
/// from docs/VIDEO_TRANSPORT.md that keeps latency from growing
/// unboundedly under loss.
/// </summary>
public sealed class UdpVideoReceiver(int listenPort, ILogger<UdpVideoReceiver> logger) : IVideoFrameReceiver
{
    private readonly Dictionary<uint, PendingFrame> _pending = [];

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;

    private uint? _highestFrameIdSeen;
    private uint? _minSequenceSeen;
    private uint? _maxSequenceSeen;
    private long _receivedPacketCount;

    public int? Port => (_client?.Client.LocalEndPoint as IPEndPoint)?.Port;

    public event EventHandler<ReassembledVideoFrame>? FrameReceived;

    public double PacketLossRatio
    {
        get
        {
            if (_minSequenceSeen is null || _maxSequenceSeen is null)
            {
                return 0;
            }

            var expected = (long)(_maxSequenceSeen.Value - _minSequenceSeen.Value) + 1;
            if (expected <= 0)
            {
                return 0;
            }

            var lost = Math.Max(0, expected - _receivedPacketCount);
            return lost / (double)expected;
        }
    }

    /// <summary>
    /// Starts a fresh loss-measurement window: without this, PacketLossRatio
    /// is a lifetime-since-connection average, so a brief burst of loss right
    /// after connecting (confirmed on real hardware, e.g. during initial
    /// socket/routing setup) permanently biases it upward for the rest of
    /// the session - accumulating enough good packets to dilute that burst
    /// back below the adaptive-bitrate recovery threshold could take much
    /// longer than the burst itself lasted. The caller (the periodic
    /// quality-check in DesktopConnectionService) calls this after each
    /// check so the ratio reflects only the window since the last check.
    /// </summary>
    public void ResetLossStats()
    {
        _minSequenceSeen = null;
        _maxSequenceSeen = null;
        _receivedPacketCount = 0;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return Task.CompletedTask;
        }

        _client = new UdpClient(listenPort);
        _cts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UDP video receive failed");
                continue;
            }

            if (!VideoPacketCodec.TryDecode(result.Buffer, out var header, out var fragment))
            {
                continue; // malformed/foreign packet - drop silently, per spec section 22
            }

            OnPacketReceived(header, fragment);
        }
    }

    private void OnPacketReceived(VideoPacketHeader header, ReadOnlySpan<byte> fragment)
    {
        UpdateLossStats(header.SequenceNumber);
        DropStaleFramesIfThisIsNewest(header.FrameId);

        if (!_pending.TryGetValue(header.FrameId, out var pending))
        {
            pending = new PendingFrame(header.FragCount, header.TimestampMicros, header.IsKeyFrame, header.TotalLength);
            _pending[header.FrameId] = pending;
        }

        if (header.IsParity)
        {
            if (pending.Parity is not null)
            {
                return;
            }

            pending.Parity = fragment.ToArray();
        }
        else
        {
            if (header.FragIndex >= pending.Fragments.Length || pending.Fragments[header.FragIndex] is not null)
            {
                return;
            }

            pending.Fragments[header.FragIndex] = fragment.ToArray();
            pending.ReceivedCount++;
        }

        if (pending.ReceivedCount != pending.Fragments.Length && !TryRecoverOneMissingFragment(pending))
        {
            return;
        }

        _pending.Remove(header.FrameId);

        var data = new byte[pending.Fragments.Sum(f => f!.Length)];
        var offset = 0;
        foreach (var frag in pending.Fragments)
        {
            frag!.CopyTo(data, offset);
            offset += frag.Length;
        }

        FrameReceived?.Invoke(this, new ReassembledVideoFrame(header.FrameId, data, pending.TimestampMicros, pending.IsKeyFrame));
    }

    /// <summary>
    /// Reconstructs exactly one missing data fragment via XOR against the
    /// frame's parity packet (see <see cref="UdpVideoSender"/>'s doc comment)
    /// and every other fragment already received - the same recovery a
    /// single-disk RAID-5 parity stripe gives you. Cannot help if two or
    /// more fragments (or the parity packet itself) are missing; those
    /// frames are still dropped exactly as before this existed.
    /// </summary>
    private static bool TryRecoverOneMissingFragment(PendingFrame pending)
    {
        if (pending.Parity is null || pending.ReceivedCount != pending.Fragments.Length - 1)
        {
            return false;
        }

        var missingIndex = Array.IndexOf(pending.Fragments, null);
        if (missingIndex < 0)
        {
            return false;
        }

        var isLastFragment = missingIndex == pending.Fragments.Length - 1;
        var missingLength = isLastFragment
            ? (int)(pending.TotalLength - (long)(pending.Fragments.Length - 1) * UdpVideoSender.MaxFragmentPayloadSize)
            : UdpVideoSender.MaxFragmentPayloadSize;

        if (missingLength <= 0 || missingLength > UdpVideoSender.MaxFragmentPayloadSize)
        {
            return false;
        }

        var reconstructed = (byte[])pending.Parity.Clone();
        foreach (var frag in pending.Fragments)
        {
            if (frag is null)
            {
                continue;
            }

            for (var i = 0; i < frag.Length; i++)
            {
                reconstructed[i] ^= frag[i];
            }
        }

        pending.Fragments[missingIndex] = reconstructed[..missingLength];
        pending.ReceivedCount++;
        return true;
    }

    private void DropStaleFramesIfThisIsNewest(uint frameId)
    {
        if (_highestFrameIdSeen is not null && frameId <= _highestFrameIdSeen)
        {
            return;
        }

        _highestFrameIdSeen = frameId;

        foreach (var staleId in _pending.Keys.Where(id => id < frameId).ToList())
        {
            _pending.Remove(staleId);
        }
    }

    private void UpdateLossStats(uint sequenceNumber)
    {
        _minSequenceSeen = _minSequenceSeen is null ? sequenceNumber : Math.Min(_minSequenceSeen.Value, sequenceNumber);
        _maxSequenceSeen = _maxSequenceSeen is null ? sequenceNumber : Math.Max(_maxSequenceSeen.Value, sequenceNumber);
        _receivedPacketCount++;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _client?.Close();

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UDP video receive loop ended with an exception");
            }
        }

        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        _pending.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class PendingFrame(ushort fragCount, ulong timestampMicros, bool isKeyFrame, uint totalLength)
    {
        public byte[]?[] Fragments { get; } = new byte[fragCount][];

        public int ReceivedCount { get; set; }

        public ulong TimestampMicros { get; } = timestampMicros;

        public bool IsKeyFrame { get; } = isKeyFrame;

        public uint TotalLength { get; } = totalLength;

        public byte[]? Parity { get; set; }
    }
}
