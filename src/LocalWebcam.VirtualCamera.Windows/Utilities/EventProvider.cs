using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LocalWebcam.VirtualCamera.Windows.Utilities;

/// <summary>
/// This DLL is loaded natively, in-process, inside the Windows Frame Server
/// service (a different process/session than <c>LocalWebcam.Desktop</c>), so
/// there is no console or attached debugger to write to, and writing to a
/// file risks failing on a service account's permissions. ETW has neither
/// problem — it's the standard, Microsoft-recommended way to diagnose code
/// running inside Frame Server. View these events with
/// <see href="https://github.com/smourier/TraceSpy">WpfTraceSpy</see>
/// (Provider GUID below) or any other ETW consumer.
/// Adapted from smourier/VCamNetSample's EventProvider.cs.
/// </summary>
public sealed class EventProvider : IDisposable
{
    public static Guid ProviderId { get; } = new("2a9e7f3d-9b1a-4b7e-8c2d-6f1e5a3b7c90");

    public static EventProvider? Current => _current.Value;
    private static readonly Lazy<EventProvider?> _current = new(() => new EventProvider(ProviderId));

    private long _handle;

    public EventProvider(Guid id)
    {
        var hr = EventRegister(id, IntPtr.Zero, IntPtr.Zero, out _handle);
        if (hr != 0)
        {
            throw new Win32Exception(hr);
        }
    }

    public bool WriteMessageEvent(string? text, byte level = 0) => EventWriteString(_handle, level, 0, text) == 0;

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = EventUnregister(handle);
        }
    }

    public static void LogError(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null) => Log(TraceLevel.Error, message, methodName, filePath);

    public static void LogInfo(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null) => Log(TraceLevel.Info, message, methodName, filePath);

    private static void Log(TraceLevel level, string? message, string? methodName, string? filePath)
    {
        var current = Current;
        if (current == null)
        {
            return;
        }

        var name = filePath != null ? Path.GetFileNameWithoutExtension(filePath) : null;
        current.WriteMessageEvent($"[{Environment.CurrentManagedThreadId}]{name}::{methodName}:{message}", (byte)level);
    }

    [DllImport("advapi32")]
    private static extern int EventRegister([MarshalAs(UnmanagedType.LPStruct)] Guid ProviderId, IntPtr EnableCallback, IntPtr CallbackContext, out long RegHandle);

    [DllImport("advapi32")]
    private static extern int EventUnregister(long RegHandle);

    [DllImport("advapi32")]
    private static extern int EventWriteString(long RegHandle, byte Level, long Keyword, [MarshalAs(UnmanagedType.LPWStr)] string? String);
}
