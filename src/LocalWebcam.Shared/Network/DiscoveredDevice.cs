using System.Net;

namespace LocalWebcam.Shared.Network;

/// <summary>
/// A phone found on the LAN, before pairing. <paramref name="DeviceId"/> is a
/// stable identifier (independent of IP address) used to correlate this
/// device across discovery methods and against the trusted-device store.
/// </summary>
public sealed record DiscoveredDevice(
    string DeviceId,
    string DisplayName,
    IPAddress Address,
    int ControlPort,
    DiscoverySource Source);

public enum DiscoverySource
{
    Mdns,
    UdpBroadcastFallback,

    /// <summary>
    /// Found via USB (ADB), not any IP network path - <see cref="DiscoveredDevice.Address"/>
    /// is always the loopback address here, reached through an
    /// `adb forward` tunnel rather than a real route.
    /// </summary>
    UsbAdb,
}
