using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LocalWebcam.Shared.Network;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Discovery;

/// <summary>
/// UDP-broadcast discovery fallback, browsing side (spec section 6): probes
/// periodically and tracks devices that reply, raising <see cref="DeviceLost"/>
/// for ones that stop responding.
/// </summary>
public sealed class UdpBroadcastDiscovery : IDeviceDiscovery
{
    private readonly int _discoveryPort;
    private readonly IPEndPoint? _fixedProbeTarget;
    private readonly TimeSpan _probeInterval;
    private readonly TimeSpan _deviceTimeout;
    private readonly ILogger<UdpBroadcastDiscovery> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private Task? _probeLoopTask;

    /// <param name="discoveryPort">Well-known port advertisers listen on.</param>
    /// <param name="probeTarget">
    /// A single fixed target to probe, overriding the normal behavior of
    /// probing every active network interface's own directed broadcast
    /// address. Tests pass <see cref="IPAddress.Loopback"/> here to exercise
    /// the protocol deterministically without relying on real broadcast
    /// delivery or the test machine's actual network interfaces.
    /// </param>
    public UdpBroadcastDiscovery(
        int discoveryPort,
        ILogger<UdpBroadcastDiscovery> logger,
        IPEndPoint? probeTarget = null,
        TimeSpan? probeInterval = null,
        TimeSpan? deviceTimeout = null)
    {
        _discoveryPort = discoveryPort;
        _logger = logger;
        _fixedProbeTarget = probeTarget;
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(3);
        _deviceTimeout = deviceTimeout ?? TimeSpan.FromSeconds(15);
    }

    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public event EventHandler<string>? DeviceLost;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return Task.CompletedTask;
        }

        _client = new UdpClient(0) { EnableBroadcast = true };
        _cts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _probeLoopTask = Task.Run(() => ProbeLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    private async Task ProbeLoopAsync(CancellationToken cancellationToken)
    {
        var probe = DiscoveryDatagram.CreateDiscover();

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var target in GetProbeTargets())
            {
                try
                {
                    await _client!.SendAsync(probe, target, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to send discovery probe to {Target}", target);
                }
            }

            PruneStaleDevices();

            try
            {
                await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One target per active network interface's own directed broadcast
    /// address (e.g. 192.168.42.255 for a USB-tethered Android phone's
    /// RNDIS interface, 192.168.1.255 for Wi-Fi), plus the global
    /// 255.255.255.255 as a catch-all. A single send to 255.255.255.255
    /// alone is not reliable on a machine with more than one active
    /// interface (e.g. Wi-Fi plus a phone's USB tethering adapter) - the OS
    /// picks one interface's route for it, silently missing the others,
    /// which is exactly the scenario USB support needs to work through.
    /// </summary>
    private IEnumerable<IPEndPoint> GetProbeTargets()
    {
        if (_fixedProbeTarget is { } fixedTarget)
        {
            yield return fixedTarget;
            yield break;
        }

        yield return new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        IPInterfaceProperties[] activeInterfaces;
        try
        {
            activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetIPProperties())
                .ToArray();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate network interfaces for discovery probing");
            yield break;
        }

        foreach (var properties in activeInterfaces)
        {
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var broadcast = GetDirectedBroadcastAddress(unicast.Address, unicast.IPv4Mask);
                if (broadcast is not null)
                {
                    yield return new IPEndPoint(broadcast, _discoveryPort);
                }
            }
        }
    }

    private static IPAddress? GetDirectedBroadcastAddress(IPAddress address, IPAddress? mask)
    {
        if (mask is null)
        {
            return null;
        }

        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != maskBytes.Length)
        {
            return null;
        }

        var broadcastBytes = new byte[addressBytes.Length];
        for (var i = 0; i < addressBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP discovery receive failed");
                continue;
            }

            if (!DiscoveryDatagram.TryParse(result.Buffer, out var type, out var deviceId, out var displayName, out var controlPort)
                || type != DiscoveryDatagramType.Hello)
            {
                continue;
            }

            _lastSeen[deviceId] = DateTimeOffset.UtcNow;
            DeviceFound?.Invoke(this, new DiscoveredDevice(deviceId, displayName, result.RemoteEndPoint.Address, controlPort, DiscoverySource.UdpBroadcastFallback));
        }
    }

    private void PruneStaleDevices()
    {
        var cutoff = DateTimeOffset.UtcNow - _deviceTimeout;
        foreach (var (deviceId, lastSeen) in _lastSeen)
        {
            if (lastSeen < cutoff && _lastSeen.TryRemove(deviceId, out _))
            {
                DeviceLost?.Invoke(this, deviceId);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _client?.Close();

        foreach (var task in new[] { _receiveLoopTask, _probeLoopTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery loop ended with an exception");
            }
        }

        _client?.Dispose();
        _client = null;
        _cts.Dispose();
        _cts = null;
        _lastSeen.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
