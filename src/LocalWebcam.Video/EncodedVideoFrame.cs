namespace LocalWebcam.Video;

/// <summary>
/// One encoded H.264 access unit (Annex-B NAL data) produced by <see cref="IVideoEncoder"/>.
/// </summary>
/// <param name="Data">Annex-B encoded bytes. Owned by the caller; the encoder does not reuse this buffer.</param>
/// <param name="PresentationTimeUs">Encoder presentation timestamp, microseconds, monotonic per stream.</param>
/// <param name="IsKeyFrame">True for IDR/key frames.</param>
public sealed record EncodedVideoFrame(byte[] Data, long PresentationTimeUs, bool IsKeyFrame);
