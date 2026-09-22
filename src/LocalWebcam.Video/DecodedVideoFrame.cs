namespace LocalWebcam.Video;

/// <summary>One decoded video frame, in NV12 (spec section 17 — matches Media Foundation's preferred decode output).</summary>
public sealed record DecodedVideoFrame(byte[] Nv12Data, int Width, int Height, long PresentationTimeUs);
