using LocalWebcam.Shared.Video;

namespace LocalWebcam.Video;

/// <summary>Describes one physical camera discovered on the device.</summary>
public sealed record CameraDescriptor(
    string Id,
    CameraFacing Facing,
    IReadOnlyList<VideoResolution> SupportedResolutions,
    bool HasFlash);
