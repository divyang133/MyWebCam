using System.Net;
using System.Net.NetworkInformation;
using LocalWebcam.Shared.Network;

namespace LocalWebcam.Desktop.ViewModels;

/// <summary>
/// One reachable path to a discovered phone, for display in the devices
/// list. The same phone can show up as more than one entry - once per
/// address it's reachable at (e.g. Wi-Fi and USB tethering both up at
/// once) - so the user can see and pick between them rather than the app
/// silently keeping whichever one last happened to respond to a probe.
/// </summary>
public sealed record DiscoveredDeviceEntry(DiscoveredDevice Device, string TransportLabel)
{
    /// <summary>Stable key across repeated discovery events for the same reachable path (not just the same device).</summary>
    public string Key => GetKey(Device);

    public static string GetKey(DiscoveredDevice device) => $"{device.DeviceId}|{device.Address}";

    public static DiscoveredDeviceEntry Create(DiscoveredDevice device) => new(device, ClassifyTransport(device));

    /// <summary>
    /// Best-effort, display-only classification - matches the device's
    /// address against this PC's own network interfaces. Not exact (a
    /// genuine wired Ethernet LAN would also read as "Ethernet", and a
    /// non-Android USB network adapter wouldn't say "USB"), but correctly
    /// distinguishes Wi-Fi from Android's USB tethering (which Windows
    /// reports as a "Remote NDIS" adapter) in the realistic scenario this
    /// is for: a laptop's Wi-Fi versus a phone plugged in over USB.
    /// </summary>
    private static string ClassifyTransport(DiscoveredDevice device)
    {
        if (device.Source == DiscoverySource.Mdns)
        {
            return "Wi-Fi";
        }

        if (device.Source == DiscoverySource.UsbAdb)
        {
            return "USB";
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (IsInSameSubnet(device.Address, unicast.Address, unicast.IPv4Mask))
                    {
                        var descriptor = $"{nic.Name} {nic.Description}";
                        if (descriptor.Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
                            || descriptor.Contains("Android", StringComparison.OrdinalIgnoreCase))
                        {
                            return "USB";
                        }

                        return nic.NetworkInterfaceType switch
                        {
                            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                            _ => "Network",
                        };
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Best-effort classification only - fall through to the generic label.
        }

        return "Network";
    }

    private static bool IsInSameSubnet(IPAddress deviceAddress, IPAddress? localAddress, IPAddress? mask)
    {
        if (localAddress is null || mask is null
            || deviceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var deviceBytes = deviceAddress.GetAddressBytes();
        var localBytes = localAddress.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        for (var i = 0; i < maskBytes.Length; i++)
        {
            if ((deviceBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }
}
