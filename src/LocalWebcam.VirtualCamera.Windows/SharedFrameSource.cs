using System.IO.Pipes;
using System.Runtime.InteropServices;
using DirectN;
using LocalWebcam.Shared.VirtualCamera;
using LocalWebcam.VirtualCamera.Windows.Utilities;

namespace LocalWebcam.VirtualCamera.Windows;

/// <summary>
/// Replaces the reference sample's synthetic pattern generator
/// (FrameGenerator.cs). We already have real, already-NV12 decoded frames
/// from <c>LocalWebcam.Windows.Video.WindowsVideoDecoder</c> — there is no
/// image to render, just a background reader that keeps the single latest
/// frame from the IPC pipe (<see cref="VirtualCameraFrameChannel"/>) ready,
/// and a synchronous copy into whatever sample <see cref="MediaStream.RequestSample"/>
/// handed us. This runs inside the Windows Frame Server process, connecting
/// out to <c>LocalWebcam.Desktop</c> (the pipe server) — see
/// docs/WINDOWS_VIRTUAL_CAMERA.md for why that direction was chosen.
/// </summary>
internal sealed class SharedFrameSource : IDisposable
{
    // NOTE: an earlier version of this class disconnected its pipe
    // connection whenever Generate() hadn't been called for 5+ seconds
    // (treating that as "Frame Server is holding this instance dormant" -
    // see docs/WINDOWS_VIRTUAL_CAMERA.md/project history for the real,
    // measured connection-accumulation problem that was meant to fix).
    // Reverted: confirmed on real hardware to cause a persistent gray
    // screen with a genuinely active consumer (Windows Camera app) - the
    // reader ended up cycling connect/disconnect roughly every 500ms
    // indefinitely instead of settling into steady frame delivery, and
    // never got far enough to receive a first real frame. The exact
    // interaction wasn't root-caused before reverting (no live ETW
    // capture was set up to see this class's own diagnostic log,
    // separate from LocalWebcam.Desktop's log - see EventProvider's doc
    // comment for why it's ETW, not file-based, here) - a working virtual
    // camera matters more than the CPU savings, and this needs proper
    // instrumentation before being retried, not another blind attempt.
    private readonly object _lock = new();
    private readonly byte[] _latestFrame;
    private bool _hasFrame;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;

    public SharedFrameSource()
    {
        _latestFrame = new byte[VirtualCameraFrameChannel.Nv12FrameByteSize(VirtualCameraFrameChannel.FrameResolution)];
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var readBuffer = new byte[_latestFrame.Length];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", VirtualCameraFrameChannel.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await client.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                EventProvider.LogInfo("Connected to LocalWebcam.Desktop frame channel");

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!VirtualCameraFrameChannel.TryReadFrame(client, readBuffer, out _, out _, out _, out var length))
                    {
                        EventProvider.LogInfo("Frame channel disconnected");
                        break;
                    }

                    if (length != _latestFrame.Length)
                    {
                        // A resolution/quality change on the Desktop side can
                        // land here before this out-of-process media source
                        // has picked up the new size (it only reloads
                        // VirtualCameraFrameChannel's config when a fresh
                        // MediaStream is activated, not live) - or vice versa.
                        // Copying a differently-sized frame into this
                        // fixed-size buffer would either overflow it or leave
                        // its tail as whatever it was before, which for a
                        // freshly allocated buffer is zeros - Y=0/U=0/V=0
                        // NV12 renders as solid green, which is exactly the
                        // artifact this was seen producing. Drop the frame
                        // instead: the last good frame (or the gray
                        // placeholder) keeps showing until sizes agree again.
                        EventProvider.LogInfo($"Dropping a {length}-byte frame; expected {_latestFrame.Length} bytes for the currently configured resolution");
                        continue;
                    }

                    lock (_lock)
                    {
                        Array.Copy(readBuffer, _latestFrame, length);
                        _hasFrame = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                EventProvider.LogInfo("Frame channel not available yet: " + ex.Message);
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Copies the latest available frame (or a mid-gray placeholder if none has arrived yet) into <paramref name="sample"/>'s buffer.</summary>
    public void Generate(IComObject<IMFSample> sample)
    {
        sample.Object.GetBufferByIndex(0, out var buffer).ThrowOnError();
        using var comBuffer = new ComObject<IMFMediaBuffer>(buffer);

        // DirectN only exposes IMFMediaBuffer.Lock's max/current-length outputs as raw
        // IntPtr; IMFMediaBufferExtensions.Lock is the friendlier out-uint overload.
        var ptr = comBuffer.Object.Lock(out var maxLength, out _);
        try
        {
            var toCopy = (int)Math.Min(maxLength, (uint)_latestFrame.Length);

            lock (_lock)
            {
                if (_hasFrame)
                {
                    Marshal.Copy(_latestFrame, 0, ptr, toCopy);
                }
                else
                {
                    FillPlaceholder(ptr, toCopy);
                }
            }

            comBuffer.Object.SetCurrentLength((uint)toCopy);
        }
        finally
        {
            comBuffer.Object.Unlock().ThrowOnError();
        }
    }

    private static unsafe void FillPlaceholder(nint ptr, int length)
    {
        // Mid-gray NV12 (Y=128, U=V=128 - neutral chroma): a flat gray frame
        // rather than exposing uninitialized memory while no phone is
        // connected yet.
        new Span<byte>((void*)ptr, length).Fill(128);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _readerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Reader task observed cancellation; nothing more to do.
        }

        _cts.Dispose();
    }
}
