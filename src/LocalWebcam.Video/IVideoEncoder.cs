using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>
/// Hardware H.264 encoder (Android <c>MediaCodec</c>). Frames are supplied by
/// the camera rendering directly into the encoder's input surface — no
/// YUV/RGB conversion or CPU-side frame copy, per spec section 10.
/// </summary>
public interface IVideoEncoder : IAsyncDisposable
{
    bool IsEncoding { get; }

    /// <summary>Raised for each encoded access unit, on a background thread.</summary>
    event EventHandler<EncodedVideoFrame>? FrameEncoded;

    event EventHandler<CameraErrorEventArgs>? Error;

    /// <summary>
    /// Configures the encoder for <paramref name="config"/> and returns the
    /// native input surface (an Android <c>Surface</c>) the camera should
    /// render into. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    object CreateInputSurface(VideoStreamConfig config);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the encoder to emit a key frame out of band (e.g. after packet loss, in later phases).</summary>
    Task RequestKeyFrameAsync();

    /// <summary>
    /// Adjusts the target bitrate on a running encoder without a full
    /// stop/reconfigure/start cycle (spec section 12's adaptive bitrate).
    /// Resolution and frame rate are unaffected — the virtual camera's media
    /// type is fixed for the connection's lifetime (see
    /// <c>VirtualCameraFrameChannel</c>), so only bitrate is ever renegotiated.
    /// </summary>
    Task SetBitrateAsync(int bitrateBps);
}
