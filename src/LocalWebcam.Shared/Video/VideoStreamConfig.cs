namespace LocalWebcam.Shared.Video;

/// <summary>
/// Requested capture/encode parameters. Negotiated between phone and desktop
/// in later phases (control channel); for Phase 1 this is set locally.
/// </summary>
public sealed record VideoStreamConfig(VideoResolution Resolution, int FrameRate, int BitrateBps)
{
    public static VideoStreamConfig Default720p30 { get; } = new(VideoResolution.Hd720, FrameRate: 30, BitrateBps: 4_000_000);

    public static VideoStreamConfig Default1080p30 { get; } = new(VideoResolution.FullHd1080, FrameRate: 30, BitrateBps: 6_000_000);

    /// <summary>
    /// 1080p60 needs meaningfully more bitrate than 1080p30 for the same
    /// perceived quality (twice the temporal information to encode).
    /// 12 Mbps matches YouTube's live-encoder guidance for 1080p60 H.264 -
    /// the top of the commonly-cited 6-12 Mbps practical range for this
    /// resolution/frame rate - which is appropriate here since this is a
    /// local/LAN stream with no platform-imposed bandwidth ceiling (unlike
    /// e.g. Twitch's 6 Mbps cap).
    /// </summary>
    public static VideoStreamConfig Default1080p60 { get; } = new(VideoResolution.FullHd1080, FrameRate: 60, BitrateBps: 12_000_000);

    /// <summary>The selectable quality presets, in ascending order - drives the desktop UI's quality picker.</summary>
    public static IReadOnlyList<(string Name, VideoStreamConfig Config)> Presets { get; } =
    [
        ("720p / 30 fps", Default720p30),
        ("1080p / 30 fps", Default1080p30),
        ("1080p / 60 fps", Default1080p60),
    ];

    public override string ToString() => $"{Resolution}@{FrameRate}";
}
