using System.Collections.Concurrent;
using System.Net;
using LocalWebcam.Network;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Shared.Network;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Windows.Discovery;

/// <summary>
/// Finds phones over USB via ADB instead of any IP network path - for
/// environments where USB tethering (an RNDIS network adapter) is blocked
/// by policy (a real, reported case: corporate IT blocking "Ethernet"-type
/// adapters, which is exactly how Windows classifies USB tethering).
/// Polls `adb devices -l`; each authorized, connected device becomes a
/// <see cref="DiscoveredDevice"/> reachable at the loopback address on
/// <see cref="DiscoveryConstants.ControlPort"/> - reachable there only once
/// <see cref="AdbUsbBridge.ForwardAsync"/> has set up the actual
/// `adb forward` tunnel for it (done by the connecting code, not here;
/// this class only detects and lists candidates).
///
/// Unlike mDNS/UDP-broadcast, there is no real advertisement to read a
/// display name or control port from - the model name `adb devices -l`
/// reports is used for display, and the control port is always the fixed
/// <see cref="DiscoveryConstants.ControlPort"/> (see that constant's doc
/// comment for why the phone's listener uses a fixed port at all).
/// </summary>
public sealed class UsbAdbDeviceDiscovery(AdbUsbBridge adbBridge, ILogger<UsbAdbDeviceDiscovery> logger) : IDeviceDiscovery
{
    /// <summary>Prefix marking a <see cref="DiscoveredDevice.DeviceId"/> as a synthetic USB-discovery ID wrapping an adb serial - not the phone's own persisted identity (see <see cref="TryGetAdbSerial"/>).</summary>
    private const string DeviceIdPrefix = "usb:";

    // Each poll spawns a real adb.exe process (unlike mDNS/UDP-broadcast
    // discovery, which is just a socket send) - a real, measurable cost
    // reported as noticeable system load ("the machine gets hung") when
    // this ran continuously for the app's whole lifetime. 5s instead of
    // 2s halves that baseline cost; pausing entirely while connected (see
    // AdbUsbBridge.SuspendPolling) matters far more, since that's also
    // when this would otherwise be spawning adb.exe processes on the same
    // USB link actively carrying the forward/reverse-tunneled video
    // stream, competing with it for no benefit - there's nothing left to
    // discover once already connected to the one phone this app talks to.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, string> _knownSerials = new(); // deviceId -> serial

    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;

    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public event EventHandler<string>? DeviceLost;

    /// <summary>The adb serial behind a USB-discovered device's synthetic <see cref="DiscoveredDevice.DeviceId"/>, or null if <paramref name="deviceId"/> wasn't discovered this way.</summary>
    public static bool TryGetAdbSerial(string deviceId, out string serial)
    {
        if (deviceId.StartsWith(DeviceIdPrefix, StringComparison.Ordinal))
        {
            serial = deviceId[DeviceIdPrefix.Length..];
            return true;
        }

        serial = string.Empty;
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _pollLoopTask = Task.Run(() => PollLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "USB device poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (adbBridge.SuspendPolling)
        {
            return;
        }

        var devices = await adbBridge.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var seenDeviceIds = new HashSet<string>();

        foreach (var (serial, model) in devices)
        {
            var deviceId = DeviceIdPrefix + serial;
            seenDeviceIds.Add(deviceId);
            _knownSerials[deviceId] = serial;

            DeviceFound?.Invoke(this, new DiscoveredDevice(deviceId, $"{model} (USB)", IPAddress.Loopback, DiscoveryConstants.ControlPort, DiscoverySource.UsbAdb));
        }

        foreach (var deviceId in _knownSerials.Keys.Where(id => !seenDeviceIds.Contains(id)).ToList())
        {
            if (_knownSerials.TryRemove(deviceId, out _))
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

        if (_pollLoopTask is not null)
        {
            try
            {
                await _pollLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "USB discovery poll loop ended with an exception");
            }
        }

        _cts.Dispose();
        _cts = null;
        _knownSerials.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
