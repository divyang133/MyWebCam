using System.Net;
using System.Net.Sockets;
using LocalWebcam.Protocol.Video;

namespace LocalWebcam.Network.Video;

/// <summary>
/// Sends encoded frames over a single TCP connection instead of
/// <see cref="UdpVideoSender"/>'s UDP datagrams - used for the USB (ADB)
/// transport, where the phone reaches the desktop through an
/// `adb reverse` tunnel to <paramref name="target"/> (always the loopback
/// address there), and only TCP can ride that tunnel at all (`adb reverse`/
/// `adb forward` don't carry UDP). No fragmentation, sequencing, or parity
/// needed - TCP already guarantees in-order, complete, lossless delivery
/// of whatever is written to the stream.
/// </summary>
public sealed class TcpVideoSender(IPEndPoint target) : IVideoFrameSender
{
    // Gates SendFrameAsync so at most one write is ever in flight - not
    // just to serialize access to _nextFrameId, but because
    // NetworkStream.WriteAsync is not safe to call concurrently from
    // overlapping SendFrameAsync calls (PhoneConnectionService.OnFrameEncoded
    // is `async void`, so a slow write for one frame doesn't block the next
    // frame's event from arriving and starting its own SendFrameAsync call).
    // Two concurrent writers interleave their bytes on the wire, corrupting
    // not just one frame but desyncing TcpVideoFrameCodec's length-prefixed
    // framing for every frame after it - confirmed as the actual cause of
    // reported frame drops and video quality degradation over this
    // transport. WaitAsync(0) - drop the frame rather than queue behind the
    // in-flight write - matches this project's general drop-stale-data
    // policy for real-time video (e.g. UdpVideoReceiver's "discard, don't
    // buffer" reassembly policy): a queued backlog would only grow latency
    // without bound if the network can't keep up, which TCP (unlike UDP)
    // has no other way to signal back to the sender.
    // A stalled/dead connection (e.g. the adb reverse tunnel wedging
    // without ever actually closing the socket) can leave
    // NetworkStream.WriteAsync awaiting forever with nothing to time it
    // out on its own. Confirmed live: once that happens, _sendGate below
    // never gets released (its `finally` only runs once the awaited write
    // completes or throws - never, in this case), so every later
    // SendFrameAsync call's WaitAsync(0) returns false and silently drops
    // that frame forever - streaming just stops, permanently, with no
    // error anywhere and nothing to reconnect it. This bounds every write
    // so a stalled one fails fast instead, and DisposeStream() below then
    // tears the connection down so the *next* frame reconnects fresh
    // rather than retrying the same dead stream forever.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private uint _nextFrameId;

    public async Task SendFrameAsync(byte[] data, ulong timestampMicros, bool isKeyFrame, CancellationToken cancellationToken = default)
    {
        if (!await _sendGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var stream = await GetStreamAsync(cancellationToken).ConfigureAwait(false);
            var packet = TcpVideoFrameCodec.Encode(_nextFrameId++, timestampMicros, isKeyFrame, data);

            using var timeoutCts = new CancellationTokenSource(WriteTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await stream.WriteAsync(packet, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The write itself timed out, not an external cancellation -
            // the connection is presumed dead; drop it so the next frame
            // reconnects instead of hanging on the same stalled stream.
            DisposeStream();
        }
        catch (IOException)
        {
            DisposeStream();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void DisposeStream()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    /// <summary>
    /// Connects lazily, on the first frame - not in the constructor - so
    /// construction can't fail/block before the caller has actually started
    /// streaming (matches <see cref="UdpVideoSender"/>'s constructor, which
    /// never touches the network either since UDP has no connection to
    /// establish).
    /// </summary>
    private async Task<NetworkStream> GetStreamAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return _stream;
        }

        var client = new TcpClient();
        await client.ConnectAsync(target.Address, target.Port, cancellationToken).ConfigureAwait(false);
        _client = client;
        _stream = client.GetStream();
        return _stream;
    }

    public ValueTask DisposeAsync()
    {
        DisposeStream();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
