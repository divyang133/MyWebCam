namespace LocalWebcam.Network.Video;

/// <summary>Fragments and sends one encoded video frame over UDP (phone side).</summary>
public interface IVideoFrameSender : IAsyncDisposable
{
    Task SendFrameAsync(byte[] data, ulong timestampMicros, bool isKeyFrame, CancellationToken cancellationToken = default);
}
