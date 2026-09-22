using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>
/// Hardware/software H.264 decoder (Windows Media Foundation). Frame
/// submission is synchronous by design — a decoder MFT's
/// <c>ProcessInput</c>/<c>ProcessOutput</c> pair is a tight, CPU-bound
/// operation with no actual asynchronous I/O to await.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    event EventHandler<DecodedVideoFrame>? FrameDecoded;

    event EventHandler<CameraErrorEventArgs>? Error;

    /// <summary>Must be called once, before the first <see cref="SubmitEncodedFrame"/>.</summary>
    void Configure(VideoResolution resolution);

    /// <summary>Feeds one Annex-B encoded access unit; raises <see cref="FrameDecoded"/> zero or more times as output becomes available.</summary>
    void SubmitEncodedFrame(byte[] data, long presentationTimeUs, bool isKeyFrame);
}
