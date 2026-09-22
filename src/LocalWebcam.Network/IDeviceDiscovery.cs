using LocalWebcam.Shared.Network;

namespace LocalWebcam.Network;

/// <summary>
/// Finds phones advertising themselves on the LAN (the desktop side of
/// discovery). Implementations may be mDNS-based or the UDP-broadcast
/// fallback (spec section 6); <see cref="CompositeDeviceDiscovery"/> merges
/// several sources and de-duplicates by <see cref="DiscoveredDevice.DeviceId"/>.
/// </summary>
public interface IDeviceDiscovery : IAsyncDisposable
{
    event EventHandler<DiscoveredDevice>? DeviceFound;

    /// <summary>Raised when a previously found device hasn't been seen recently and is presumed gone.</summary>
    event EventHandler<string>? DeviceLost;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
