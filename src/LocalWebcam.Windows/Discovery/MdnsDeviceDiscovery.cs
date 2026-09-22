using System.Collections.Concurrent;
using System.Net;
using LocalWebcam.Network;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Shared.Network;
using Microsoft.Extensions.Logging;
using Zeroconf;

namespace LocalWebcam.Windows.Discovery;

/// <summary>
/// Browses for phones advertising <c>_localwebcam._tcp</c> via mDNS/DNS-SD
/// using the Zeroconf library (pure managed, no native Bonjour dependency).
/// Android advertises through <c>NsdManager</c> instead; this class only
/// browses, per the platform split documented in docs/ARCHITECTURE.md.
/// </summary>
public sealed class MdnsDeviceDiscovery(ILogger<MdnsDeviceDiscovery> logger) : IDeviceDiscovery
{
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _pruneLoopTask;

    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public event EventHandler<string>? DeviceLost;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_subscription is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _subscription = ZeroconfResolver
            .ResolveContinuous(DiscoveryConstants.MdnsServiceType, scanTime: TimeSpan.FromSeconds(2))
            .Subscribe(OnHostFound, ex => logger.LogWarning(ex, "mDNS discovery error"));
        _pruneLoopTask = Task.Run(() => PruneLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    private void OnHostFound(IZeroconfHost host)
    {
        if (!host.Services.TryGetValue(DiscoveryConstants.MdnsServiceType, out var service))
        {
            return;
        }

        if (!IPAddress.TryParse(host.IPAddress, out var address))
        {
            logger.LogWarning("mDNS host {DisplayName} had no parseable IP address", host.DisplayName);
            return;
        }

        // The phone publishes its stable DeviceId as a TXT record; fall back
        // to the mDNS instance id if it's ever missing (shouldn't happen for
        // our own advertiser, but this keeps discovery lenient).
        var deviceId = ExtractTxtValue(service, "id") ?? host.Id;

        _lastSeen[deviceId] = DateTimeOffset.UtcNow;
        DeviceFound?.Invoke(this, new DiscoveredDevice(deviceId, host.DisplayName, address, service.Port, DiscoverySource.Mdns));
    }

    private static string? ExtractTxtValue(IService service, string key)
    {
        foreach (var record in service.Properties)
        {
            if (record.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private async Task PruneLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - DeviceTimeout;
            foreach (var (deviceId, lastSeen) in _lastSeen)
            {
                if (lastSeen < cutoff && _lastSeen.TryRemove(deviceId, out _))
                {
                    DeviceLost?.Invoke(this, deviceId);
                }
            }
        }
    }

    public async Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_pruneLoopTask is not null)
            {
                try
                {
                    await _pruneLoopTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "mDNS prune loop ended with an exception");
                }
            }

            _cts.Dispose();
            _cts = null;
        }

        _lastSeen.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
