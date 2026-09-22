namespace LocalWebcam.Shared.Video;

/// <summary>A pixel resolution, e.g. 1920x1080.</summary>
public readonly record struct VideoResolution(int Width, int Height)
{
    public static readonly VideoResolution Hd720 = new(1280, 720);
    public static readonly VideoResolution FullHd1080 = new(1920, 1080);

    public override string ToString() => $"{Width}x{Height}";
}
