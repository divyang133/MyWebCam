using System.IO.Pipes;
using LocalWebcam.Shared.VirtualCamera;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;
using Vortice.MediaFoundation;

namespace LocalWebcam.Windows.Video;

/// <summary>
/// Registers and drives the Windows 11 Frame Server virtual camera device
/// (spec section 16). This class only talks to the OS-level device handle
/// (<see cref="IMFVirtualCamera"/>, via Vortice.MediaFoundation, in-process);
/// the actual per-frame media source the Frame Server loads to serve those
/// frames to consumers (OBS, Teams, ...) lives in a separate, out-of-process
/// COM server — <c>LocalWebcam.VirtualCamera.Windows</c> — because the Frame
/// Server loads that source natively in its own process. This class is the
/// pipe *server* side of <see cref="VirtualCameraFrameChannel"/>: it feeds
/// whatever <see cref="PushFrame"/> was last called with to that out-of-
/// process media source whenever it is connected. See
/// docs/WINDOWS_VIRTUAL_CAMERA.md for the full picture.
/// </summary>
public sealed class WindowsVirtualCamera(ILogger<WindowsVirtualCamera> logger) : IVirtualCamera
{
    private IMFVirtualCamera? _camera;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoopTask;
    private string? _friendlyName;
    private bool _isStarted;

    // NamedPipeServerStream.WaitForConnectionAsync's cancellation support is
    // unreliable in practice: cancelling the token alone does not
    // consistently unblock a pending wait when no client ever connects (a
    // long-standing .NET behavior, not specific to this pipe) - the
    // reliable way to force it to abort is disposing the pipe instance
    // itself, which is why DisposeAsync needs to reach this rather than
    // relying on _acceptCts.Cancel() alone. Confirmed on real hardware:
    // clicking the tray icon's Exit did nothing at all, because
    // DisposeAsync was awaiting an accept loop stuck forever in
    // WaitForConnectionAsync with no client pending.
    private NamedPipeServerStream? _pendingPipe;

    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private int _latestWidth;
    private int _latestHeight;
    private long _latestTimestampUs;

    // Broadcasts to every connected client at once (swapped for a fresh
    // instance on each PushFrame, completing the old one - every waiter
    // awaiting that old Task wakes up together). A single SemaphoreSlim
    // wouldn't do this: releasing it wakes exactly one waiter, not all of
    // them, which silently starves every client but one once more than one
    // is connected - see the doc comment on AcceptLoopAsync for why more
    // than one client is a real, observed scenario, not a hypothetical.
    private volatile TaskCompletionSource<bool> _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _clientTasksLock = new();
    private readonly List<Task> _clientTasks = [];

    public Task InitializeAsync(string friendlyName, VideoResolution resolution, int frameRate, CancellationToken cancellationToken = default)
    {
        if (!MediaFactory.MFIsVirtualCameraTypeSupported(VirtualCameraType.SoftwareCameraSource))
        {
            throw new NotSupportedException(
                "This Windows version does not support Media Foundation virtual cameras " +
                "(MFCreateVirtualCamera requires Windows 11 22H2 or later). See docs/WINDOWS_VIRTUAL_CAMERA.md.");
        }

        _friendlyName = friendlyName;

        // Must happen before MFCreateVirtualCamera: the Frame Server can
        // activate the out-of-process media source (which reads this same
        // config) essentially immediately once the device exists - see
        // VirtualCameraConfig's doc comment for why this is a file and not
        // an in-memory value.
        VirtualCameraConfig.Save(resolution, frameRate);
        VirtualCameraFrameChannel.ApplyConfig(resolution, frameRate);

        CreateCamera(friendlyName);

        _acceptCts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));

        return Task.CompletedTask;
    }

    private void CreateCamera(string friendlyName)
    {
        // Session lifetime + CurrentUser access: the device disappears when this
        // process exits and needs no admin rights beyond the one-time COM
        // registration (see docs/WINDOWS_VIRTUAL_CAMERA.md) — appropriate for an
        // app-driven virtual camera that should not persist as a phantom device
        // after the app is closed.
        _camera = MediaFactory.MFCreateVirtualCamera(
            VirtualCameraType.SoftwareCameraSource,
            VirtualCameraLifetime.Session,
            VirtualCameraAccess.CurrentUser,
            friendlyName,
            VirtualCameraIds.ActivatorClsidRegistryFormat,
            []);

        logger.LogInformation("Virtual camera '{Name}' registered (sourceId={SourceId})", friendlyName, VirtualCameraIds.ActivatorClsidRegistryFormat);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var camera = _camera ?? throw new InvalidOperationException($"Call {nameof(InitializeAsync)} first.");
        camera.Start(null!).CheckError();
        _isStarted = true;
        logger.LogInformation("Virtual camera started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _camera?.Stop().CheckError();
        _isStarted = false;
        logger.LogInformation("Virtual camera stopped");
        return Task.CompletedTask;
    }

    public Task ReconfigureAsync(VideoResolution resolution, int frameRate, CancellationToken cancellationToken = default)
    {
        if (_friendlyName is null)
        {
            throw new InvalidOperationException($"Call {nameof(InitializeAsync)} first.");
        }

        var wasStarted = _isStarted;

        // Resolution/frame rate are fixed for a device's lifetime (Windows
        // learns a camera's supported formats once, at creation), so
        // changing them means removing and recreating the OS device rather
        // than reconfiguring it in place. The pipe server (AcceptLoopAsync)
        // is untouched - it's independent of which specific IMFVirtualCamera
        // instance is currently registered.
        if (_camera is { } camera)
        {
            // Both Stop() and Remove() are best-effort here: the Frame Server
            // can tear down an idle virtual camera's underlying session on
            // its own (observed on real hardware as MF_E_SHUTDOWN from Stop()
            // after the device sat registered but unused for a while), and
            // that's fine - we're about to Dispose() and recreate the device
            // either way, so a stale handle failing to stop/remove cleanly
            // shouldn't block reconfiguring.
            if (wasStarted)
            {
                try
                {
                    camera.Stop().CheckError();
                }
                catch (SharpGen.Runtime.SharpGenException ex)
                {
                    logger.LogWarning(ex, "Error stopping the virtual camera before reconfiguring");
                }
            }

            try
            {
                camera.Remove().CheckError();
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                logger.LogWarning(ex, "Error removing the virtual camera before reconfiguring");
            }

            camera.Dispose();
            _camera = null;
        }

        VirtualCameraConfig.Save(resolution, frameRate);
        VirtualCameraFrameChannel.ApplyConfig(resolution, frameRate);
        CreateCamera(_friendlyName);

        if (wasStarted)
        {
            _camera!.Start(null!).CheckError();
            _isStarted = true;
        }

        logger.LogInformation("Virtual camera reconfigured to {Resolution}@{FrameRate}", resolution, frameRate);
        return Task.CompletedTask;
    }

    public void PushFrame(DecodedVideoFrame frame)
    {
        lock (_frameLock)
        {
            _latestFrame = frame.Nv12Data;
            _latestWidth = frame.Width;
            _latestHeight = frame.Height;
            _latestTimestampUs = frame.PresentationTimeUs;
        }

        var previous = Interlocked.Exchange(ref _frameSignal, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult(true);
    }

    /// <summary>
    /// Accepts connections from the out-of-process media source forever, and
    /// serves each one concurrently rather than one-at-a-time. This isn't
    /// hypothetical: Windows was observed (during real testing) creating a
    /// *separate* media-source activation per consumer - the desktop app's
    /// own preview and a second app (Windows Camera) opening the same device
    /// each got their own <c>SharedFrameSource</c> instance needing its own
    /// pipe connection. Serving only one at a time silently starved every
    /// consumer after the first (they'd sit on the placeholder gray frame
    /// forever, waiting for a pipe slot that never freed up).
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    VirtualCameraFrameChannel.PipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not create the virtual camera frame pipe; retrying");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _pendingPipe = pipe;

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Cancelling the token alone doesn't reliably unblock a
                // pending WaitForConnectionAsync (see _pendingPipe's doc
                // comment) - DisposeAsync forces it by disposing the pipe
                // directly, which surfaces here as ObjectDisposedException
                // or IOException rather than OperationCanceledException.
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            finally
            {
                _pendingPipe = null;
            }

            logger.LogInformation("Virtual camera media source connected to the frame pipe");
            var clientTask = ServeClientAsync(pipe, cancellationToken);

            lock (_clientTasksLock)
            {
                _clientTasks.RemoveAll(t => t.IsCompleted);
                _clientTasks.Add(clientTask);
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await WriteLoopAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            logger.LogInformation(ex, "Virtual camera media source disconnected from the frame pipe");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            await _frameSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            byte[] data;
            int width, height;
            long timestampUs;
            lock (_frameLock)
            {
                if (_latestFrame is null)
                {
                    continue;
                }

                data = _latestFrame;
                width = _latestWidth;
                height = _latestHeight;
                timestampUs = _latestTimestampUs;
            }

            VirtualCameraFrameChannel.WriteFrame(pipe, width, height, timestampUs, data);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _acceptCts?.Cancel();

        // _acceptCts.Cancel() alone does not reliably unblock a pending
        // WaitForConnectionAsync - see _pendingPipe's doc comment. Without
        // this, DisposeAsync (and anything awaiting it, e.g. the app's
        // tray-icon Exit handler) could hang forever whenever no client is
        // currently connected, which is the common case.
        _pendingPipe?.Dispose();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }

        Task[] clientTasks;
        lock (_clientTasksLock)
        {
            clientTasks = [.. _clientTasks];
        }

        try
        {
            await Task.WhenAll(clientTasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: individual client tasks already log their own
            // disconnect/cancellation reasons in ServeClientAsync.
        }

        _acceptCts?.Dispose();

        if (_camera is { } camera)
        {
            try
            {
                camera.Remove().CheckError();
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                logger.LogWarning(ex, "Error removing the virtual camera during dispose");
            }

            camera.Dispose();
            _camera = null;
        }
    }
}
