using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>
/// Application-service facade over <see cref="ICameraController"/> and
/// <see cref="IVideoEncoder"/>: owns their combined lifecycle so UI code
/// depends on one simple, platform-agnostic surface (spec section 25).
/// </summary>
public interface ICameraService : IAsyncDisposable
{
    StreamingState State { get; }

    CameraDescriptor? CurrentCamera { get; }

    event EventHandler<StreamingState>? StateChanged;

    event EventHandler<EncodedVideoFrame>? FrameEncoded;

    event EventHandler<CameraErrorEventArgs>? Error;

    /// <summary>
    /// Opens the requested camera and starts encoding. <paramref name="previewTarget"/>
    /// is an optional native preview surface (e.g. from a <c>TextureView</c>);
    /// pass <see langword="null"/> to encode without a live preview.
    /// </summary>
    Task StartAsync(CameraFacing facing, VideoStreamConfig config, object? previewTarget, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SwitchCameraAsync(CancellationToken cancellationToken = default);

    Task SetTorchAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>No-op while not streaming. See <see cref="IVideoEncoder.SetBitrateAsync"/>.</summary>
    Task SetBitrateAsync(int bitrateBps);
}
