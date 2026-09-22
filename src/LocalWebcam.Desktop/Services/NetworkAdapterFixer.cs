using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Desktop.Services;

/// <summary>
/// Detects and fixes the real, reproducible conflict between Android USB
/// tethering and a VPN: the phone's RNDIS adapter accepts a DHCP offer that
/// includes both a default gateway and its own IP as the sole DNS server,
/// and Windows' automatic interface metric can rank that adapter *below*
/// (i.e. more preferred than) Wi-Fi - so general internet/DNS traffic
/// silently routes through the phone instead of a VPN-protected path. The
/// VPN tunnel itself stays connected; only its actual traffic breaks, which
/// is why this looks like a LocalWebcam connectivity bug rather than a
/// networking one. Confirmed and fixed manually on real hardware before
/// this was automated - see scripts/Fix-UsbTetheringVpnConflict.ps1 for the
/// original, interactively-run version of the same fix.
///
/// This only ever touches an adapter matching the same "RNDIS"/"Android"
/// description pattern <see cref="LocalWebcam.Desktop.ViewModels.DiscoveredDeviceEntry"/>
/// already uses to label USB-connected phones - never Wi-Fi, Ethernet, or
/// any VPN adapter - and only when that adapter actually has a gateway
/// configured (the specific, actionable signal that it's trying to act as
/// an internet path rather than just a LAN link to the phone).
/// </summary>
public static class NetworkAdapterFixer
{
    /// <summary>
    /// Finds a USB-tethering adapter that's currently configured with its
    /// own gateway (the state that causes the conflict) and hasn't already
    /// been fixed (metric not already deprioritized).
    /// </summary>
    public static string? FindAdapterNeedingFix()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var descriptor = $"{nic.Name} {nic.Description}";
            if (!descriptor.Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
                && !descriptor.Contains("Android", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address is not null && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway)
            {
                continue;
            }

            // Already-applied fixes set a very high metric; skip re-prompting
            // for elevation every launch once an adapter's been fixed once.
            if (GetCurrentMetric(nic.Name) is >= 9999)
            {
                continue;
            }

            return nic.Name;
        }

        return null;
    }

    private static int? GetCurrentMetric(string adapterName)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe")
            {
                ArgumentList = { "interface", "ip", "show", "config", $"name={adapterName}" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var line = output.Split('\n').FirstOrDefault(l => l.Contains("InterfaceMetric", StringComparison.OrdinalIgnoreCase));
            var digits = new string((line ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var metric) ? metric : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the fix: raises the adapter's metric so it's never preferred
    /// over Wi-Fi/VPN for anything except its own directly-connected subnet
    /// (unaffected either way - that's a same-subnet route with no
    /// competing alternative), and overrides its DNS to match whatever a
    /// real, already-working adapter is using (a plain "no DNS" override
    /// does not stick while DHCP stays enabled on the interface - confirmed
    /// interactively before automating this). Requires one elevated prompt;
    /// returns false (without throwing) if the user declines it.
    /// </summary>
    public static async Task<bool> TryFixAsync(string adapterName, ILogger logger, CancellationToken cancellationToken = default)
    {
        var fallbackDns = FindFallbackDnsServer(adapterName) ?? "1.1.1.1";

        var script =
            $"Set-NetIPInterface -InterfaceAlias '{adapterName}' -AutomaticMetric Disabled -InterfaceMetric 9999 -AddressFamily IPv4; " +
            $"netsh interface ip set dns name=\"{adapterName}\" source=static addr={fallbackDns} | Out-Null";

        logger.LogInformation("USB tethering adapter '{Adapter}' has its own gateway/DNS, which can silently break a VPN's traffic - requesting administrator rights to fix it (one-time, per adapter)", adapterName);

        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script },
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("Failed to launch the elevated network-fix process");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Network adapter fix exited with code {ExitCode}", process.ExitCode);
                return false;
            }

            logger.LogInformation("USB tethering adapter '{Adapter}' fixed - it will no longer compete with Wi-Fi/VPN for general traffic", adapterName);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            logger.LogInformation("The user declined the administrator prompt; the USB tethering/VPN conflict was left unfixed");
            return false;
        }
    }

    private static string? FindFallbackDnsServer(string excludeAdapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.Name == excludeAdapterName)
            {
                continue;
            }

            var dns = nic.GetIPProperties().DnsAddresses
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (dns is not null)
            {
                return dns.ToString();
            }
        }

        return null;
    }
}
