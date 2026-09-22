using LocalWebcam.Network.Discovery;

namespace LocalWebcam.Network.Tests.Discovery;

public class DiscoveryDatagramTests
{
    [Fact]
    public void Discover_RoundTrips()
    {
        var bytes = DiscoveryDatagram.CreateDiscover();

        var ok = DiscoveryDatagram.TryParse(bytes, out var type, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(DiscoveryDatagramType.Discover, type);
    }

    [Fact]
    public void Hello_RoundTrips()
    {
        var bytes = DiscoveryDatagram.CreateHello("device-42", "Pixel 9", 5000);

        var ok = DiscoveryDatagram.TryParse(bytes, out var type, out var deviceId, out var name, out var port);

        Assert.True(ok);
        Assert.Equal(DiscoveryDatagramType.Hello, type);
        Assert.Equal("device-42", deviceId);
        Assert.Equal("Pixel 9", name);
        Assert.Equal(5000, port);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 1, 1 })]
    public void TryParse_RejectsMalformedOrUnrecognizedInput(byte[] garbage)
    {
        var ok = DiscoveryDatagram.TryParse(garbage, out _, out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_RejectsTruncatedHello()
    {
        var full = DiscoveryDatagram.CreateHello("device-42", "Pixel 9", 5000);
        var truncated = full.AsSpan(0, full.Length - 3).ToArray();

        var ok = DiscoveryDatagram.TryParse(truncated, out _, out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_RejectsWrongVersion()
    {
        var bytes = DiscoveryDatagram.CreateDiscover();
        bytes[^1] = 99; // corrupt the version byte

        var ok = DiscoveryDatagram.TryParse(bytes, out _, out _, out _, out _);

        Assert.False(ok);
    }
}
