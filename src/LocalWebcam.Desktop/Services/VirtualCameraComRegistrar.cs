using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Desktop.Services;

/// <summary>
/// Registers the virtual camera's out-of-process COM media source
/// (LocalWebcam.VirtualCamera.Windows.comhost.dll) via an elevated
/// <c>regsvr32</c>, triggered automatically the first time it's needed
/// rather than requiring the user to run a setup script themselves (spec
/// update: "everything should be configured when we run the client app").
///
/// This still shows a real UAC prompt - registering a COM server in HKLM
/// genuinely requires administrator rights (see docs/WINDOWS_VIRTUAL_CAMERA.md),
/// and silently elevating without that visible prompt isn't something any
/// Windows app can or should do. The point of this class is only to remove
/// the separate manual script step, not to hide the elevation itself from
/// the user - they still see and must approve the UAC dialog.
/// </summary>
public static class VirtualCameraComRegistrar
{
    /// <summary>REGDB_E_CLASSNOTREG - the exact, expected failure when the COM server hasn't been registered yet.</summary>
    public const int ClassNotRegisteredHResult = unchecked((int)0x80040154);

    /// <summary>
    /// ERROR_MOD_NOT_FOUND ("The specified module could not be found") - what
    /// Frame Server's COM activation fails with when the CLSID *is*
    /// registered but its InprocServer32 path points at a file that no
    /// longer exists there. This is the normal state after a portable
    /// (extract-anywhere) distribution's folder is moved, re-extracted
    /// somewhere else, or rebuilt at a different path than the one that last
    /// ran regsvr32 - not a corrupted install. Treated the same as
    /// <see cref="ClassNotRegisteredHResult"/>: re-registering (regsvr32
    /// always overwrites the existing InprocServer32 value) repairs it.
    /// </summary>
    public const int ModuleNotFoundHResult = unchecked((int)0x8007007E);

    /// <summary>
    /// Finds the comhost.dll next to this running executable (it's always
    /// copied there at build time via LocalWebcam.Desktop's project
    /// reference - see LocalWebcam.VirtualCamera.Windows.csproj) and
    /// launches an elevated <c>regsvr32 /s</c> on it. Returns false (without
    /// throwing) if the user declines the UAC prompt or the file can't be
    /// found - the caller is expected to report that as "virtual camera
    /// unavailable" rather than crash.
    /// </summary>
    public static async Task<bool> TryRegisterAsync(ILogger logger, CancellationToken cancellationToken = default)
    {
        // AppContext.BaseDirectory is NOT the right base here for a
        // single-file publish: .NET remaps it to a per-launch temp
        // extraction cache directory (IncludeAllContentForSelfExtract) for
        // resolving the app's *own* bundled dependencies, but that
        // extraction only copies files it already knew to bundle - it
        // silently drops comhost.dll's *own* .deps.json/.runtimeconfig.json
        // (added post-hoc by a custom MSBuild target, not part of the
        // bundle manifest), even though it happens to still copy the DLLs
        // themselves. Confirmed on real hardware: pointing regsvr32 at the
        // extraction cache's copy failed with exit code 5 (missing runtime
        // config to bootstrap the CLR - the exact same failure mode as the
        // original loose-deploy bug this class was written to work around),
        // while the original publish folder - which has the complete,
        // consistent set of files together - registers successfully.
        // Environment.ProcessPath reliably returns the physical .exe's own
        // location even for a single-file app, unlike AppContext.BaseDirectory.
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var dllPath = Path.Combine(processDirectory, "LocalWebcam.VirtualCamera.Windows.comhost.dll");
        logger.LogInformation("Resolved comhost path: {DllPath} (exists={Exists})", dllPath, File.Exists(dllPath));
        if (!File.Exists(dllPath))
        {
            logger.LogWarning("Could not find {DllPath} to register - was the app published without it?", dllPath);
            return false;
        }

        logger.LogInformation("Virtual camera COM server isn't registered yet; requesting administrator rights to register it (one-time, per install)");

        try
        {
            var startInfo = new ProcessStartInfo("regsvr32.exe")
            {
                ArgumentList = { "/s", dllPath },
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = processDirectory,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("Failed to launch the elevated regsvr32 process");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("regsvr32 exited with code {ExitCode}", process.ExitCode);
                return false;
            }

            logger.LogInformation("Virtual camera COM server registered successfully");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user declined the UAC prompt.
            logger.LogInformation("The user declined the administrator prompt; virtual camera registration was skipped");
            return false;
        }
    }
}
