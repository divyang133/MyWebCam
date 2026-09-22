namespace LocalWebcam.Network.Discovery;

public static class DiscoveryConstants
{
    /// <summary>DNS-SD service name, unqualified (no protocol/domain suffix).</summary>
    public const string ServiceName = "_localwebcam._tcp";

    /// <summary>Fully-qualified mDNS service type as Zeroconf expects it.</summary>
    public const string MdnsServiceType = ServiceName + ".local.";

    /// <summary>
    /// Well-known port for the UDP-broadcast discovery fallback. Chosen in
    /// the dynamic/private range (49152-65535) to avoid colliding with
    /// registered services.
    /// </summary>
    public const int UdpBroadcastPort = 57621;

    /// <summary>
    /// Fixed port for the phone's control-channel <c>TcpListener</c>
    /// (<c>PhoneConnectionService</c>), replacing what used to be an
    /// OS-assigned dynamic port (<c>Start(port: 0)</c>). Wi-Fi discovery
    /// (mDNS/UDP-broadcast) still learns the port from the phone's own
    /// advertisement either way, so this doesn't change anything for it -
    /// it exists so USB (ADB) connections, which have no advertisement to
    /// read a dynamic port from, can set up `adb forward tcp:ControlPort
    /// tcp:ControlPort` against a port number known in advance.
    /// </summary>
    public const int ControlPort = 57900;
}
