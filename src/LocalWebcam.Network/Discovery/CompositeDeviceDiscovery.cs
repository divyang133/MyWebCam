using LocalWebcam.Shared.Network;

namespace LocalWebcam.Network.Discovery;

/// <summary>
/// Fans out to several discovery sources (mDNS, UDP-broadcast fallback) and
/// forwards their events. Callers key devices by <see cref="DiscoveredDevice.DeviceId"/>
/// (e.g. an upsert into a dictionary/observable collection) since the same
/// device may be reported by more than one source.
/// </summary>
public sealed class CompositeDeviceDiscovery(IReadOnlyList<IDeviceDiscovery> sources) : IDeviceDiscovery
{
    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public event EventHandler<string>? DeviceLost;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var source in sources)
        {
            source.DeviceFound += OnDeviceFound;
            source.DeviceLost += OnDeviceLost;
        }

        await Task.WhenAll(sources.Select(s => s.StartAsync(cancellationToken))).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(sources.Select(s => s.StopAsync())).ConfigureAwait(false);

        foreach (var source in sources)
        {
            source.DeviceFound -= OnDeviceFound;
            source.DeviceLost -= OnDeviceLost;
        }
    }

    private void OnDeviceFound(object? sender, DiscoveredDevice device) => DeviceFound?.Invoke(this, device);

    private void OnDeviceLost(object? sender, string deviceId) => DeviceLost?.Invoke(this, deviceId);

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
