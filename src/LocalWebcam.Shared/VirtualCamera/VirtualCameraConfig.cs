using System.Text.Json;
using LocalWebcam.Shared.Video;

namespace LocalWebcam.Shared.VirtualCamera;

/// <summary>
/// Persists the user's chosen streaming quality (resolution + frame rate) so
/// <see cref="VirtualCameraFrameChannel"/>'s advertised media type can match
/// it. This has to be a file, not just an in-memory value: the media source
/// that actually declares the OS-level media type
/// (<c>LocalWebcam.VirtualCamera.Windows.MediaStream</c>) runs inside the
/// Windows Frame Server, a separate process from <c>LocalWebcam.Desktop</c>
/// where the user picks the quality - there is no in-memory channel between
/// them, only this file and the named-pipe frame data itself (which doesn't
/// carry "this is the format we committed to" semantics, just per-frame
/// dimensions). Written by Desktop before each
/// <c>IVirtualCamera.InitializeAsync</c>/<c>ReconfigureAsync</c> call; read
/// by <c>MediaStream</c>'s constructor, which runs fresh on every Frame
/// Server activation, so it always sees whatever was saved most recently.
/// </summary>
public static class VirtualCameraConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalWebcam",
        "virtualcamera-config.json");

    public static void Save(VideoResolution resolution, int frameRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(new StoredConfig(resolution.Width, resolution.Height, frameRate));
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Falls back to 720p30 (the original, always-supported default) if nothing was ever saved or the file is unreadable.</summary>
    public static (VideoResolution Resolution, int FrameRate) Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(ConfigPath));
                if (stored is { Width: > 0, Height: > 0, FrameRate: > 0 })
                {
                    return (new VideoResolution(stored.Width, stored.Height), stored.FrameRate);
                }
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable config file - fall through to the default
            // rather than failing virtual camera setup over a preference file.
        }

        return (VideoResolution.Hd720, 30);
    }

    private sealed record StoredConfig(int Width, int Height, int FrameRate);
}
