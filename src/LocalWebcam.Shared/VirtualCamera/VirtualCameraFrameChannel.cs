using System.Buffers.Binary;
using LocalWebcam.Shared.Video;

namespace LocalWebcam.Shared.VirtualCamera;

/// <summary>
/// Wire format for the local IPC channel (a named pipe) carrying decoded NV12
/// frames from <c>LocalWebcam.Desktop</c> into the out-of-process virtual
/// camera media source hosted by the Windows Frame Server. A named pipe is
/// required rather than any in-process hand-off because the Frame Server
/// loads that media source natively, in a different process (and often a
/// different session) from this app — see docs/WINDOWS_VIRTUAL_CAMERA.md.
///
/// Desktop is the pipe server (long-lived once streaming starts); the media
/// source is the client, connecting (and reconnecting) whenever the Frame
/// Server has loaded it. Only the single latest frame matters — there is no
/// queue, matching this project's general "drop stale data, don't buffer"
/// policy (see docs/VIDEO_TRANSPORT.md).
/// </summary>
public static class VirtualCameraFrameChannel
{
    public const string PipeName = "LocalWebcam.VirtualCameraFrames";

    // The virtual camera must advertise a single NV12 media type to Windows
    // before any phone is even connected, so resolution/frame rate can't be
    // negotiated per-connection - but the user can still choose one of
    // several presets (spec update: 720p30 or 1080p60) before starting the
    // camera. These start as the safe 720p30 default and are refreshed from
    // VirtualCameraConfig by whoever constructs the media type: Desktop
    // (LocalWebcam.Windows.Video.WindowsVirtualCamera) before each
    // Initialize/Reconfigure, and MediaStream's constructor on every fresh
    // Frame Server activation (a different process, so it can't just read
    // Desktop's in-memory value).
    public static VideoResolution FrameResolution { get; private set; } = VideoResolution.Hd720;

    public static int FrameRateNumerator { get; private set; } = 30;

    public const int FrameRateDenominator = 1;

    public static void ApplyConfig(VideoResolution resolution, int frameRate)
    {
        FrameResolution = resolution;
        FrameRateNumerator = frameRate;
    }

    /// <summary>Convenience for the two call sites described above: loads the persisted config and applies it in one step.</summary>
    public static void ReloadFromSavedConfig()
    {
        var (resolution, frameRate) = VirtualCameraConfig.Load();
        ApplyConfig(resolution, frameRate);
    }

    private const uint Magic = 0x4C575643; // "LWVC"
    private const int HeaderSize = 24;

    public static int Nv12FrameByteSize(VideoResolution resolution) => resolution.Width * resolution.Height * 3 / 2;

    /// <summary>Writes one length-prefixed frame message. Not thread-safe against concurrent writers on the same stream.</summary>
    public static void WriteFrame(Stream stream, int width, int height, long presentationTimeUs, ReadOnlySpan<byte> nv12Data)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], height);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..20], presentationTimeUs);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], nv12Data.Length);
        stream.Write(header);
        stream.Write(nv12Data);
    }

    /// <summary>
    /// Reads one frame message into <paramref name="reusableBuffer"/>, blocking until it
    /// arrives. Returns false if the stream ended (peer disconnected) before a full message
    /// was read — the caller should treat that as "reconnect", not an error.
    /// </summary>
    public static bool TryReadFrame(Stream stream, byte[] reusableBuffer, out int width, out int height, out long presentationTimeUs, out int length)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (!TryReadExact(stream, header))
        {
            width = height = length = 0;
            presentationTimeUs = 0;
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        if (magic != Magic)
        {
            throw new InvalidDataException("Virtual camera frame channel is out of sync (bad magic).");
        }

        width = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        height = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        presentationTimeUs = BinaryPrimitives.ReadInt64LittleEndian(header[12..20]);
        length = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);

        if (length < 0 || length > reusableBuffer.Length)
        {
            throw new InvalidDataException($"Virtual camera frame channel reported an implausible frame size ({length} bytes).");
        }

        return TryReadExact(stream, reusableBuffer.AsSpan(0, length));
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
