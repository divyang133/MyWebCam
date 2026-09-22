using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Windows.Discovery;

/// <summary>
/// Shells out to <c>adb.exe</c> (Android Platform Tools) for the USB
/// transport: enumerating USB-connected phones, and tunneling the control
/// and video TCP connections over the existing USB debugging link via
/// `adb forward`/`adb reverse` - the only way to reach a phone over USB
/// without creating the network adapter USB tethering does (see
/// <see cref="UsbAdbDeviceDiscovery"/>'s doc comment for why that matters).
/// Requires the phone to have Developer Options -> USB debugging enabled
/// and authorized for this PC, and `adb.exe` to be present somewhere this
/// class knows to look (<see cref="FindAdbPath"/>) - neither of which this
/// app can turn on itself.
/// </summary>
public sealed class AdbUsbBridge(ILogger<AdbUsbBridge> logger)
{
    private string? _adbPath;
    private bool _searched;

    /// <summary>
    /// Set by <c>DesktopConnectionService</c> while a USB session is active
    /// (connecting through to disconnecting) - <see cref="UsbAdbDeviceDiscovery"/>
    /// checks this and skips its poll entirely rather than spawning another
    /// adb.exe process. There's nothing left to discover once already
    /// connected to the one phone this app talks to at a time, and every
    /// skipped poll is one less process competing with the forward/reverse-
    /// tunneled video stream on the same USB link for no benefit.
    /// </summary>
    public bool SuspendPolling { get; set; }

    /// <summary>
    /// Locates <c>adb.exe</c>: the PATH first (an explicit, deliberate
    /// install), then the well-known locations Android Studio and the
    /// standalone Platform Tools zip each default to. Result is cached -
    /// the search touches the filesystem/PATH and the answer can't change
    /// mid-process in any way that matters here.
    /// </summary>
    public string? FindAdbPath()
    {
        if (_searched)
        {
            return _adbPath;
        }

        _searched = true;

        var candidates = new List<string> { "adb.exe", "adb" };

        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrEmpty(androidHome))
        {
            candidates.Add(Path.Combine(androidHome, "platform-tools", "adb.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe");
        candidates.Add(@"C:\Android\platform-tools\adb.exe");

        foreach (var candidate in candidates)
        {
            if (TryRun(candidate, "version", out _))
            {
                _adbPath = candidate;
                logger.LogInformation("Found adb at '{Path}'", candidate);
                return _adbPath;
            }
        }

        logger.LogWarning("adb.exe was not found on PATH or in any well-known Android SDK location - USB device detection is unavailable");
        return null;
    }

    /// <summary>Connected devices, as (serial, model) pairs - only those in the normal "device" state (authorized, not offline/unauthorized).</summary>
    public async Task<IReadOnlyList<(string Serial, string Model)>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var adbPath = FindAdbPath();
        if (adbPath is null)
        {
            return [];
        }

        var (exitCode, stdOut, _) = await RunAsync(adbPath, "devices -l", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return [];
        }

        var results = new List<(string Serial, string Model)>();
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device")
            {
                continue; // "unauthorized", "offline", etc. - not usable yet
            }

            var serial = parts[0];
            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..] ?? serial;
            results.Add((serial, model));
        }

        return results;
    }

    public Task<bool> ForwardAsync(string serial, int port, CancellationToken cancellationToken = default) =>
        RunForDeviceAsync(serial, $"forward tcp:{port} tcp:{port}", cancellationToken);

    public Task<bool> ReverseAsync(string serial, int port, CancellationToken cancellationToken = default) =>
        RunForDeviceAsync(serial, $"reverse tcp:{port} tcp:{port}", cancellationToken);

    /// <summary>Best-effort cleanup - safe to call even if the forward was never actually set up.</summary>
    public Task RemoveForwardAsync(string serial, int port, CancellationToken cancellationToken = default) =>
        RunForDeviceAsync(serial, $"forward --remove tcp:{port}", cancellationToken);

    /// <summary>Best-effort cleanup - safe to call even if the reverse was never actually set up.</summary>
    public Task RemoveReverseAsync(string serial, int port, CancellationToken cancellationToken = default) =>
        RunForDeviceAsync(serial, $"reverse --remove tcp:{port}", cancellationToken);

    private async Task<bool> RunForDeviceAsync(string serial, string args, CancellationToken cancellationToken)
    {
        var adbPath = FindAdbPath();
        if (adbPath is null)
        {
            return false;
        }

        var (exitCode, _, stdErr) = await RunAsync(adbPath, $"-s {serial} {args}", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            logger.LogWarning("adb {Args} for {Serial} failed: {Error}", args, serial, stdErr);
        }

        return exitCode == 0;
    }

    private static bool TryRun(string exe, string args, out string stdOut)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                stdOut = string.Empty;
                return false;
            }

            stdOut = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stdOut = string.Empty;
            return false;
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string adbPath, string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(adbPath)
        {
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "Failed to start adb.exe");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Failed to run 'adb {Args}'", args);
            return (-1, string.Empty, ex.Message);
        }
    }
}
