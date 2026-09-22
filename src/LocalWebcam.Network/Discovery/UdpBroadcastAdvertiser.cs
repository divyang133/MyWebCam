using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Discovery;

/// <summary>
/// UDP-broadcast discovery fallback, advertiser side (spec section 6): binds
/// to a well-known port, and replies with a Hello datagram — directly to
/// whatever endpoint a Discover request came from — whenever one arrives.
/// Used when mDNS multicast is filtered by the network but ordinary
/// unicast/broadcast UDP still gets through.
/// </summary>
public sealed class UdpBroadcastAdvertiser(int discoveryPort, ILogger<UdpBroadcastAdvertiser> logger) : IServiceAdvertiser
{
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;

    public Task StartAsync(string deviceId, string displayName, int controlPort, CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return Task.CompletedTask;
        }

        _client = new UdpClient(discoveryPort) { EnableBroadcast = true };
        _cts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(deviceId, displayName, controlPort, _cts.Token));

        logger.LogInformation("UDP discovery advertiser listening on port {Port} as {DeviceId}", discoveryPort, deviceId);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(string deviceId, string displayName, int controlPort, CancellationToken cancellationToken)
    {
        var helloDatagram = DiscoveryDatagram.CreateHello(deviceId, displayName, controlPort);

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
                logger.LogWarning(ex, "UDP discovery advertiser receive failed");
                continue;
            }

            if (!DiscoveryDatagram.TryParse(result.Buffer, out var type, out _, out _, out _) || type != DiscoveryDatagramType.Discover)
            {
                continue;
            }

            try
            {
                await _client.SendAsync(helloDatagram, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to reply to discovery probe from {Endpoint}", result.RemoteEndPoint);
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

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Advertiser receive loop ended with an exception");
            }
        }

        _client?.Dispose();
        _client = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
