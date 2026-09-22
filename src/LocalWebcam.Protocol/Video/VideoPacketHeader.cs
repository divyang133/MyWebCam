namespace LocalWebcam.Protocol.Video;

/// <summary>One UDP video-packet header, decoded (docs/VIDEO_TRANSPORT.md).</summary>
public readonly record struct VideoPacketHeader(
    uint FrameId,
    ushort FragIndex,
    ushort FragCount,
    ulong TimestampMicros,
    uint SequenceNumber,
    bool IsKeyFrame,
    bool IsParity,
    uint TotalLength);
