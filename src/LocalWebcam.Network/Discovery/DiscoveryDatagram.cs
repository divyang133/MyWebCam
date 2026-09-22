using System.Text;

namespace LocalWebcam.Network.Discovery;

internal enum DiscoveryDatagramType : byte
{
    Discover = 1,
    Hello = 2,
}

/// <summary>
/// Wire format for the UDP-broadcast discovery fallback (spec section 6).
/// This is deliberately separate from the control-channel framing in
/// docs/PROTOCOL.md: discovery is connectionless, pre-authentication, and
/// happens before any protocol version has been negotiated.
///
/// Discover: [Magic(4)][Type(1)=1][Version(1)]
/// Hello:    [Magic(4)][Type(1)=2][Version(1)][DeviceIdLen(1)][DeviceId(UTF8)]
///           [NameLen(1)][Name(UTF8)][ControlPort(2, big-endian)]
/// </summary>
internal static class DiscoveryDatagram
{
    private static readonly byte[] Magic = "LWCD"u8.ToArray();
    private const byte Version = 1;

    public static byte[] CreateDiscover()
    {
        var buffer = new byte[Magic.Length + 2];
        Magic.CopyTo(buffer, 0);
        buffer[Magic.Length] = (byte)DiscoveryDatagramType.Discover;
        buffer[Magic.Length + 1] = Version;
        return buffer;
    }

    public static byte[] CreateHello(string deviceId, string displayName, int controlPort)
    {
        var deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);
        var nameBytes = Encoding.UTF8.GetBytes(displayName);

        if (deviceIdBytes.Length > byte.MaxValue || nameBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("Device id and display name must each be at most 255 UTF-8 bytes.");
        }

        using var stream = new MemoryStream();
        stream.Write(Magic);
        stream.WriteByte((byte)DiscoveryDatagramType.Hello);
        stream.WriteByte(Version);
        stream.WriteByte((byte)deviceIdBytes.Length);
        stream.Write(deviceIdBytes);
        stream.WriteByte((byte)nameBytes.Length);
        stream.Write(nameBytes);
        stream.WriteByte((byte)(controlPort >> 8));
        stream.WriteByte((byte)controlPort);
        return stream.ToArray();
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out DiscoveryDatagramType type, out string deviceId, out string displayName, out int controlPort)
    {
        type = default;
        deviceId = string.Empty;
        displayName = string.Empty;
        controlPort = 0;

        if (datagram.Length < Magic.Length + 2 || !datagram[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        type = (DiscoveryDatagramType)datagram[Magic.Length];
        var version = datagram[Magic.Length + 1];
        if (version != Version)
        {
            return false;
        }

        if (type == DiscoveryDatagramType.Discover)
        {
            return datagram.Length == Magic.Length + 2;
        }

        if (type != DiscoveryDatagramType.Hello)
        {
            return false;
        }

        var offset = Magic.Length + 2;
        if (offset >= datagram.Length)
        {
            return false;
        }

        var deviceIdLen = datagram[offset++];
        if (offset + deviceIdLen > datagram.Length)
        {
            return false;
        }

        deviceId = Encoding.UTF8.GetString(datagram.Slice(offset, deviceIdLen));
        offset += deviceIdLen;

        if (offset >= datagram.Length)
        {
            return false;
        }

        var nameLen = datagram[offset++];
        if (offset + nameLen + 2 > datagram.Length)
        {
            return false;
        }

        displayName = Encoding.UTF8.GetString(datagram.Slice(offset, nameLen));
        offset += nameLen;

        controlPort = (datagram[offset] << 8) | datagram[offset + 1];
        return true;
    }
}
