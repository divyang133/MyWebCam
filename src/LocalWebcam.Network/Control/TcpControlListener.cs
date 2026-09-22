using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Control;

/// <summary>TCP-backed <see cref="IControlListener"/>: the phone's side, accepting the desktop's connection.</summary>
public sealed class TcpControlListener(ILoggerFactory loggerFactory) : IControlListener
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public int? Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    public event EventHandler<IControlConnection>? ConnectionAccepted;

    public void Start(int port)
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<TcpControlListener>();

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var connection = TcpControlConnection.FromAcceptedClient(client, loggerFactory.CreateLogger<TcpControlConnection>());
            logger.LogInformation("Accepted control connection from {Endpoint}", client.Client.RemoteEndPoint);
            ConnectionAccepted?.Invoke(this, connection);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // Accept-loop cancellation/socket-teardown exceptions are expected here.
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
