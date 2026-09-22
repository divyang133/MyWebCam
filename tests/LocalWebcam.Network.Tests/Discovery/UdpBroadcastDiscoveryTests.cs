using System.Net;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Shared.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWebcam.Network.Tests.Discovery;

public class UdpBroadcastDiscoveryTests
{
    private static int GetFreeUdpPort()
    {
        using var socket = new System.Net.Sockets.UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Discovery_FindsAdvertiserOverLoopback()
    {
        var port = GetFreeUdpPort();
        await using var advertiser = new UdpBroadcastAdvertiser(port, NullLogger<UdpBroadcastAdvertiser>.Instance);
        await using var discovery = new UdpBroadcastDiscovery(
            port,
            NullLogger<UdpBroadcastDiscovery>.Instance,
            probeTarget: new IPEndPoint(IPAddress.Loopback, port),
            probeInterval: TimeSpan.FromMilliseconds(100));

        var found = new TaskCompletionSource<DiscoveredDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.DeviceFound += (_, device) => found.TrySetResult(device);

        await advertiser.StartAsync("phone-123", "Pixel 9", controlPort: 5000);
        await discovery.StartAsync();

        var result = await found.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("phone-123", result.DeviceId);
        Assert.Equal("Pixel 9", result.DisplayName);
        Assert.Equal(5000, result.ControlPort);
        Assert.Equal(DiscoverySource.UdpBroadcastFallback, result.Source);
        Assert.Equal(IPAddress.Loopback, result.Address);
    }

    [Fact]
    public async Task Discovery_WithNoAdvertiser_NeverRaisesDeviceFound()
    {
        var port = GetFreeUdpPort();
        await using var discovery = new UdpBroadcastDiscovery(
            port,
            NullLogger<UdpBroadcastDiscovery>.Instance,
            probeTarget: new IPEndPoint(IPAddress.Loopback, port),
            probeInterval: TimeSpan.FromMilliseconds(50));

        var foundCount = 0;
        discovery.DeviceFound += (_, _) => Interlocked.Increment(ref foundCount);

        await discovery.StartAsync();
        await Task.Delay(300);
        await discovery.StopAsync();

        Assert.Equal(0, foundCount);
    }

    [Fact]
    public async Task Discovery_DeviceLost_RaisedAfterAdvertiserStops()
    {
        var port = GetFreeUdpPort();
        var advertiser = new UdpBroadcastAdvertiser(port, NullLogger<UdpBroadcastAdvertiser>.Instance);
        await using var discovery = new UdpBroadcastDiscovery(
            port,
            NullLogger<UdpBroadcastDiscovery>.Instance,
            probeTarget: new IPEndPoint(IPAddress.Loopback, port),
            probeInterval: TimeSpan.FromMilliseconds(50),
            deviceTimeout: TimeSpan.FromMilliseconds(150));

        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.DeviceFound += (_, _) => found.TrySetResult(true);
        discovery.DeviceLost += (_, id) => lost.TrySetResult(id);

        await advertiser.StartAsync("phone-abc", "Test Phone", 5000);
        await discovery.StartAsync();
        await found.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await advertiser.DisposeAsync();

        var lostId = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("phone-abc", lostId);
    }
}
