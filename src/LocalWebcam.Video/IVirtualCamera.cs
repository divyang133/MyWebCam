using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>
/// The Windows OS-level virtual camera (spec section 16). Frames pushed here
/// must actually reach the OS's camera pipeline — other applications (OBS,
/// Teams, browsers) select this like any other physical webcam. There is no
/// pure-managed way to implement this: the underlying Media Foundation
/// Frame Server loads the real device source natively, out of this app's
/// process; see docs/WINDOWS_VIRTUAL_CAMERA.md for why and how.
/// </summary>
public interface IVirtualCamera : IAsyncDisposable
{
    /// <summary>
    /// Registers the virtual camera with Windows (it becomes visible in
    /// Settings → Cameras) but does not yet start producing frames.
    /// <paramref name="resolution"/>/<paramref name="frameRate"/> become the
    /// device's fixed advertised media type for its lifetime - the OS learns
    /// a camera's supported formats once, at creation, not per-connection.
    /// </summary>
    Task InitializeAsync(string friendlyName, VideoResolution resolution, int frameRate, CancellationToken cancellationToken = default);

    /// <summary>Starts the device source so consumers (OBS, Teams, ...) can open and stream from it.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the advertised resolution/frame rate. Since that's fixed for
    /// a device's lifetime, this removes and recreates the underlying OS
    /// device rather than reconfiguring it in place - a brief (sub-second)
    /// disappearance from Settings → Cameras/OBS's device list is the
    /// unavoidable cost of changing quality, so this should only be called
    /// while not actively streaming to a consumer.
    /// </summary>
    Task ReconfigureAsync(VideoResolution resolution, int frameRate, CancellationToken cancellationToken = default);

    /// <summary>Delivers one decoded frame to be shown as the current camera image.</summary>
    void PushFrame(DecodedVideoFrame frame);
}
