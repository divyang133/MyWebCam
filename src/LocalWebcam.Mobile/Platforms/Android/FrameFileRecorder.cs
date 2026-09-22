using Android.Content;

namespace LocalWebcam.Mobile.Platforms.Android;

/// <summary>
/// Writes the raw Annex-B H.264 elementary stream to the app's external
/// files directory. This is Phase 1's local acceptance test harness (pull
/// the file with <c>adb</c> and play it with ffplay/VLC to verify hardware
/// encoding) — it goes away once Phase 2 sends frames over the network
/// instead of to disk.
/// </summary>
public sealed class FrameFileRecorder(Context context)
{
    private const string FileName = "capture.h264";

    private readonly Lock _lock = new();
    private FileStream? _stream;

    public string? FilePath { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            var directory = context.GetExternalFilesDir(null) ?? context.FilesDir
                ?? throw new InvalidOperationException("No writable app storage directory is available.");

            FilePath = Path.Combine(directory.AbsolutePath, FileName);
            _stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
    }

    public void Write(byte[] data)
    {
        lock (_lock)
        {
            _stream?.Write(data, 0, data.Length);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stream?.Flush();
            _stream?.Dispose();
            _stream = null;
        }
    }
}
