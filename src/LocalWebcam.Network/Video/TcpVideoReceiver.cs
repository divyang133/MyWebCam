using System.Net;
using System.Net.Sockets;
using LocalWebcam.Protocol.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Video;

/// <summary>
/// Receives whole encoded frames over a single TCP connection instead of
/// <see cref="UdpVideoReceiver"/>'s UDP reassembly - the desktop side of the
/// USB (ADB) transport (see <see cref="TcpVideoSender"/>'s doc comment).
/// Listens for exactly one inbound connection (the phone, arriving through
/// an `adb reverse` tunnel) and reads length-prefixed frames off it in
/// order - no reassembly, loss recovery, or reordering needed, since TCP
/// already guarantees all of that.
/// </summary>
public sealed class TcpVideoReceiver(int listenPort, ILogger<TcpVideoReceiver> logger) : IVideoFrameReceiver
{
    // A stalled connection (e.g. the adb forward/reverse tunnel wedging
    // without the socket ever actually closing) can otherwise leave a read
    // awaiting forever - frames should arrive every ~33ms at 30fps, so
    // several seconds of silence is unambiguously a dead connection, not a
    // real gap. Bounding each read lets this fall through to the same
    // "connection ended" handling a genuine disconnect gets, so the accept
    // loop moves on to the phone's next reconnect instead of sitting on a
    // dead stream indefinitely - see TcpVideoSender.WriteTimeout's doc
    // comment for the send-side half of this same failure mode.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public int? Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    public event EventHandler<ReassembledVideoFrame>? FrameReceived;

    /// <summary>Always 0 - TCP delivers every byte written to it or the connection drops; there is no partial/lossy middle ground to measure.</summary>
    public double PacketLossRatio => 0;

    public void ResetLossStats()
    {
        // Nothing to reset - see PacketLossRatio's doc comment.
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accepts and reads from one connection at a time - a fresh connect
    /// (e.g. a reconnect after a drop) replaces whatever was being read
    /// before, matching this app's one-phone-at-a-time model.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            await ReadFramesAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadFramesAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var timeoutCts = new CancellationTokenSource(ReadTimeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    var (frameId, timestampMicros, isKeyFrame, data) = await TcpVideoFrameCodec.ReadFrameAsync(stream, linkedCts.Token).ConfigureAwait(false);
                    FrameReceived?.Invoke(this, new ReassembledVideoFrame(frameId, data, timestampMicros, isKeyFrame));
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogInformation(ex, "TCP video connection ended");
            }
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TCP video accept loop ended with an exception");
            }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
