namespace LocalWebcam.Protocol.Video;

/// <summary>A fully reassembled encoded video frame, as delivered to the receiver.</summary>
public sealed record ReassembledVideoFrame(uint FrameId, byte[] Data, ulong TimestampMicros, bool IsKeyFrame);
