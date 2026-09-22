using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>
/// Platform camera control (Camera2 on Android). Capture output targets are
/// native platform objects (e.g. an Android <c>Surface</c>) — this interface
/// cannot fully abstract them away without hiding a real platform
/// requirement, per the project's platform-isolation principle. Only
/// platform-specific implementations and their composition root should
/// construct those targets.
/// </summary>
public interface ICameraController : IAsyncDisposable
{
    /// <summary>Cameras discovered on this device. Populated after the first <see cref="OpenAsync"/> call.</summary>
    IReadOnlyList<CameraDescriptor> AvailableCameras { get; }

    CameraDescriptor? CurrentCamera { get; }

    bool IsCapturing { get; }

    /// <summary>Raised for non-fatal and fatal camera errors (permission denied, device disconnected, etc.).</summary>
    event EventHandler<CameraErrorEventArgs>? Error;

    /// <summary>Enumerates cameras and opens the requested one. Does not start capture.</summary>
    Task OpenAsync(CameraFacing preferredFacing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a repeating capture request targeting <paramref name="outputTargets"/>
    /// (e.g. a preview <c>Surface</c> and the video encoder's input <c>Surface</c>).
    /// </summary>
    Task StartCaptureAsync(VideoStreamConfig config, IReadOnlyList<object> outputTargets, CancellationToken cancellationToken = default);

    Task StopCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the current camera and opens the other-facing camera, restarting capture with the same targets/config.</summary>
    Task SwitchCameraAsync(CancellationToken cancellationToken = default);

    Task SetTorchAsync(bool enabled, CancellationToken cancellationToken = default);

    Task CloseAsync();

    /// <summary>
    /// The highest frame rate the currently-open camera can actually sustain
    /// at or above <paramref name="requestedFrameRate"/>'s intent (i.e. the
    /// upper bound of its best matching AE target-FPS range) - not
    /// necessarily <paramref name="requestedFrameRate"/> itself. Callers
    /// should use this to reconcile the encoder's frame rate with what the
    /// camera can really deliver *before* configuring the encoder: asking
    /// the encoder for a higher rate than the camera can feed doesn't fail
    /// outright, it just produces an uneven duplicate-frame cadence (visible
    /// as judder) since the encoder samples the shared surface faster than
    /// the camera actually updates it. Returns <paramref name="requestedFrameRate"/>
    /// unchanged if this camera's capabilities can't be determined - i.e.
    /// "assume it's fine" rather than silently downgrading with no evidence
    /// either way.
    /// </summary>
    int GetAchievableFrameRate(int requestedFrameRate);
}
