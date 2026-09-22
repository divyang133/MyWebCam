using Android.Content;
using Android.Net.Nsd;
using LocalWebcam.Network;
using LocalWebcam.Network.Discovery;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Android.Discovery;

/// <summary>
/// Advertises this phone via Android's native <see cref="NsdManager"/>
/// (Android's mDNS/DNS-SD wrapper) so <c>LocalWebcam.Windows</c>'s Zeroconf
/// browser can find it. <see cref="NsdManager"/> manages the underlying
/// multicast socket and permissions itself — no app-level multicast lock is
/// needed for registration.
/// </summary>
public sealed class NsdServiceAdvertiser(Context context, ILogger<NsdServiceAdvertiser> logger) : IServiceAdvertiser
{
    private NsdManager? _nsdManager;
    private RegistrationListener? _listener;

    public Task StartAsync(string deviceId, string displayName, int controlPort, CancellationToken cancellationToken = default)
    {
        if (_nsdManager is not null)
        {
            return Task.CompletedTask;
        }

        _nsdManager = (NsdManager)context.GetSystemService(Context.NsdService)!;

        var serviceInfo = new NsdServiceInfo
        {
            ServiceName = displayName,
            ServiceType = $"{DiscoveryConstants.ServiceName}.",
            Port = controlPort,
        };
        serviceInfo.SetAttribute("id", deviceId);

        _listener = new RegistrationListener(logger);
        _nsdManager.RegisterService(serviceInfo, NsdProtocol.DnsSd, _listener);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_nsdManager is not null && _listener is not null)
        {
            try
            {
                _nsdManager.UnregisterService(_listener);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to unregister NSD service");
            }
        }

        _listener = null;
        _nsdManager = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class RegistrationListener(ILogger logger) : Java.Lang.Object, NsdManager.IRegistrationListener
    {
        public void OnRegistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) =>
            logger.LogError("NSD registration failed for {Name}: {Error}", serviceInfo?.ServiceName, errorCode);

        public void OnServiceRegistered(NsdServiceInfo? serviceInfo) =>
            logger.LogInformation("NSD service registered: {Name}", serviceInfo?.ServiceName);

        public void OnServiceUnregistered(NsdServiceInfo? serviceInfo) =>
            logger.LogInformation("NSD service unregistered: {Name}", serviceInfo?.ServiceName);

        public void OnUnregistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) =>
            logger.LogWarning("NSD unregistration failed for {Name}: {Error}", serviceInfo?.ServiceName, errorCode);
    }
}
