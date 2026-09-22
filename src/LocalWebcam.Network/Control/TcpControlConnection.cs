using System.Net;
using System.Net.Sockets;
using LocalWebcam.Protocol.Control;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Network.Control;

/// <summary>TCP-backed <see cref="IControlConnection"/>, framed with <see cref="ControlFrameCodec"/>.</summary>
public sealed class TcpControlConnection : IControlConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<TcpControlConnection> _logger;

    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;

    private TcpControlConnection(TcpClient client, ILogger<TcpControlConnection> logger)
    {
        _client = client;
        _stream = client.GetStream();
        _logger = logger;
    }

    public static async Task<TcpControlConnection> ConnectAsync(IPAddress address, int port, ILogger<TcpControlConnection> logger, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        return new TcpControlConnection(client, logger);
    }

    internal static TcpControlConnection FromAcceptedClient(TcpClient client, ILogger<TcpControlConnection> logger) =>
        new(client, logger);

    public bool IsConnected => _client.Connected;

    public IPEndPoint RemoteEndPoint => (IPEndPoint)_client.Client.RemoteEndPoint!;

    public event EventHandler<ControlMessageReceivedEventArgs>? MessageReceived;

    public event EventHandler? Disconnected;

    public void StartReceiving()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (type, payload) = await ControlFrameCodec.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
                MessageReceived?.Invoke(this, new ControlMessageReceivedEventArgs(type, payload));
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogInformation("Control connection closed: {Reason}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            // Malformed frame from the peer (spec section 22: validate,
            // don't crash) - treat as a disconnect rather than propagating.
            _logger.LogWarning(ex, "Malformed control frame received; closing connection");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control connection receive loop failed unexpectedly");
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SendAsync(ControlMessageType type, byte[] payload, CancellationToken cancellationToken = default)
    {
        var frame = ControlFrameCodec.Encode(type, payload);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CloseAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _stream.Close();
        _client.Close();

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Control connection receive loop ended with an exception during close");
            }
        }

        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
