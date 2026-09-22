using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using LocalWebcam.Network;
using LocalWebcam.Network.Control;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Network.Video;
using LocalWebcam.Protocol.Control;
using LocalWebcam.Security.Pairing;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Mobile.Services;

/// <summary>
/// The phone's side of Phase 2: advertises this device, accepts one desktop
/// connection at a time, runs the pairing/reconnect handshake, and — once
/// authenticated — starts <see cref="ICameraService"/> and streams encoded
/// frames to the desktop's <c>UdpVideoReceiver</c> over UDP.
/// </summary>
public sealed class PhoneConnectionService : IAsyncDisposable
{
    private readonly IReadOnlyList<IServiceAdvertiser> _advertisers;
    private readonly IControlListener _listener;
    private readonly ITrustedDeviceStore _trustStore;
    private readonly ICameraService _cameraService;
    private readonly ILogger<PhoneConnectionService> _logger;
    private readonly string _deviceId;
    private readonly string _displayName;

    private IVideoFrameSender? _videoSender;
    private IControlConnection? _currentConnection;

    // Front, not Rear: this app's whole point is to be a webcam - it faces
    // the user, like every laptop webcam does. Tracked here (rather than
    // re-defaulting to Rear on every fresh StartAsync call) so a camera
    // switch survives a reconnect instead of silently reverting - confirmed
    // on real hardware as the actual cause of a "wrong/rear camera" report
    // after several reconnects during testing.
    private CameraFacing _currentFacing = CameraFacing.Front;

    public RemoteConnectionState State { get; private set; } = RemoteConnectionState.Idle;

    // Set by MainViewModel.AttachPreviewTarget whenever the on-screen
    // TextureView's Surface becomes available/destroyed. Needed here (not
    // just passed straight into ICameraService.StartAsync from the
    // ViewModel's own "Start Streaming" button) because a desktop-initiated
    // connection starts the camera from *this* service instead - streaming
    // to the desktop worked fine either way (only the encoder surface
    // matters for that), but the local on-screen preview silently stayed
    // black for exactly this path, since Camera2's output surfaces are
    // fixed at capture-session creation and can't be added after the fact.
    public object? PreviewTarget { get; set; }

    public event EventHandler<RemoteConnectionState>? StateChanged;

    public event EventHandler<PairingApprovalRequest>? PairingApprovalRequested;

    public PhoneConnectionService(
        IEnumerable<IServiceAdvertiser> advertisers,
        IControlListener listener,
        ITrustedDeviceStore trustStore,
        ICameraService cameraService,
        string deviceId,
        string displayName,
        ILoggerFactory loggerFactory)
    {
        _advertisers = advertisers.ToList();
        _listener = listener;
        _trustStore = trustStore;
        _cameraService = cameraService;
        _deviceId = deviceId;
        _displayName = displayName;
        _logger = loggerFactory.CreateLogger<PhoneConnectionService>();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener.ConnectionAccepted += OnConnectionAccepted;

        // A fixed port, not the dynamic one this used before: Wi-Fi
        // discovery (mDNS/UDP-broadcast) still tells the desktop the actual
        // port from this advertisement either way, so this doesn't change
        // anything for it - but the USB (ADB) transport has no
        // advertisement to read a dynamic port from at all, and needs a
        // port number known in advance to set up `adb forward` against.
        // See DiscoveryConstants.ControlPort's doc comment.
        _listener.Start(DiscoveryConstants.ControlPort);

        foreach (var advertiser in _advertisers)
        {
            await advertiser.StartAsync(_deviceId, _displayName, _listener.Port!.Value, cancellationToken).ConfigureAwait(false);
        }

        SetState(RemoteConnectionState.Advertising);
        _logger.LogInformation("Advertising as {DeviceId} ({DisplayName}) on control port {Port}", _deviceId, _displayName, _listener.Port);
    }

    private void OnConnectionAccepted(object? sender, IControlConnection connection) => _ = HandleConnectionAsync(connection);

    private async Task HandleConnectionAsync(IControlConnection connection)
    {
        var channel = Channel.CreateUnbounded<(ControlMessageType Type, byte[] Payload)>();

        void OnMessage(object? s, ControlMessageReceivedEventArgs e) => channel.Writer.TryWrite((e.Type, e.Payload));
        void OnDisconnected(object? s, EventArgs e) => channel.Writer.TryComplete();

        connection.MessageReceived += OnMessage;
        connection.Disconnected += OnDisconnected;
        connection.StartReceiving();
        _currentConnection = connection;
        SetState(RemoteConnectionState.Connected);

        try
        {
            await RunProtocolAsync(connection, channel.Reader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection from {Endpoint} ended", connection.RemoteEndPoint);
            SetState(RemoteConnectionState.Error);
        }
        finally
        {
            connection.MessageReceived -= OnMessage;
            connection.Disconnected -= OnDisconnected;
            await StopStreamingAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            _currentConnection = null;
            SetState(RemoteConnectionState.Advertising);
        }
    }

    /// <summary>
    /// Full stop for when the app itself is closing (see
    /// <see cref="LocalWebcam.Mobile.Platforms.Android.StreamingForegroundService.OnTaskRemoved"/>):
    /// stops the camera directly - awaited here rather than left to
    /// <see cref="HandleConnectionAsync"/>'s own cleanup, since that runs on
    /// a separate fire-and-forget task and Android gives OnTaskRemoved only
    /// a short window before the process can be killed - closes the current
    /// control connection (as if the desktop had dropped it, so the desktop
    /// sees a real disconnect instead of silently freezing on the last
    /// frame), and stops advertising/listening so the desktop's own
    /// auto-reconnect can't silently re-establish a session and restart the
    /// camera a few seconds later, which defeats the point of closing the
    /// app. Unlike <see cref="DisposeAsync"/>, every underlying component
    /// here resets itself back to a startable state on stop, so a later
    /// <see cref="StartAsync"/> (the app being reopened in the same
    /// process) works again.
    /// </summary>
    public async Task StopActiveSessionAsync()
    {
        await StopStreamingAsync().ConfigureAwait(false);

        if (_currentConnection is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _listener.ConnectionAccepted -= OnConnectionAccepted;
        await _listener.StopAsync().ConfigureAwait(false);

        foreach (var advertiser in _advertisers)
        {
            await advertiser.StopAsync().ConfigureAwait(false);
        }

        SetState(RemoteConnectionState.Idle);
    }

    private async Task RunProtocolAsync(IControlConnection connection, ChannelReader<(ControlMessageType Type, byte[] Payload)> reader)
    {
        var (helloType, helloPayload) = await ReceiveAsync(reader).ConfigureAwait(false);
        if (helloType != ControlMessageType.Hello)
        {
            throw new IOException($"Expected Hello, got {helloType}.");
        }

        var hello = ControlMessageSerializer.Deserialize<HelloMessage>(helloPayload);
        var existingTrust = await _trustStore.FindAsync(hello.DeviceId).ConfigureAwait(false);

        await connection.SendAsync(ControlMessageType.HelloAck, new HelloAckMessage(1, _deviceId, _displayName, existingTrust is not null)).ConfigureAwait(false);

        var authenticated = existingTrust is not null
            ? await TryReconnectWithTrustAsync(connection, reader, existingTrust).ConfigureAwait(false)
            : await PairAsync(connection, reader, hello).ConfigureAwait(false);

        if (!authenticated)
        {
            return;
        }

        SetState(RemoteConnectionState.Paired);
        await RunStreamingAsync(connection, reader).ConfigureAwait(false);
    }

    private async Task<bool> TryReconnectWithTrustAsync(IControlConnection connection, ChannelReader<(ControlMessageType, byte[])> reader, TrustedDevice trust)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await connection.SendAsync(ControlMessageType.TrustChallenge, new TrustChallengeMessage(nonce)).ConfigureAwait(false);

        var (type, payload) = await ReceiveAsync(reader).ConfigureAwait(false);
        if (type != ControlMessageType.TrustResponse)
        {
            await connection.SendAsync(ControlMessageType.PairResult, new PairResultMessage(false, $"Expected TrustResponse, got {type}.")).ConfigureAwait(false);
            return false;
        }

        var response = ControlMessageSerializer.Deserialize<TrustResponseMessage>(payload);
        using var hmac = new HMACSHA256(trust.TrustKey);
        var expected = hmac.ComputeHash(nonce);
        var verified = CryptographicOperations.FixedTimeEquals(expected, response.Hmac);

        await connection.SendAsync(ControlMessageType.PairResult, new PairResultMessage(verified, verified ? null : "Trust verification failed.")).ConfigureAwait(false);
        _logger.LogInformation("Reconnect for {DeviceId} verified={Verified}", trust.DeviceId, verified);
        return verified;
    }

    private async Task<bool> PairAsync(IControlConnection connection, ChannelReader<(ControlMessageType, byte[])> reader, HelloMessage hello)
    {
        using var session = new PairingSession();
        await connection.SendAsync(ControlMessageType.PairChallenge, new PairChallengeMessage(session.LocalPublicKey)).ConfigureAwait(false);

        var (type, payload) = await ReceiveAsync(reader).ConfigureAwait(false);
        if (type != ControlMessageType.PairRequest)
        {
            await connection.SendAsync(ControlMessageType.PairResult, new PairResultMessage(false, $"Expected PairRequest, got {type}.")).ConfigureAwait(false);
            return false;
        }

        var request = ControlMessageSerializer.Deserialize<PairRequestMessage>(payload);
        session.CompleteKeyExchange(request.PublicKey);
        var code = session.ComputeVerificationCode(session.LocalPublicKey, request.PublicKey);

        SetState(RemoteConnectionState.AwaitingPairingApproval);
        var approval = new PairingApprovalRequest(hello.DisplayName, code);
        PairingApprovalRequested?.Invoke(this, approval);

        var accepted = await approval.Decision.Task.ConfigureAwait(false);

        if (accepted)
        {
            await _trustStore.SaveAsync(new TrustedDevice(hello.DeviceId, hello.DisplayName, DateTimeOffset.UtcNow, session.DeriveTrustKey())).ConfigureAwait(false);
        }

        await connection.SendAsync(ControlMessageType.PairResult, new PairResultMessage(accepted, accepted ? null : "Rejected by user.")).ConfigureAwait(false);
        _logger.LogInformation("Pairing with {DeviceId} accepted={Accepted}", hello.DeviceId, accepted);
        return accepted;
    }

    private async Task RunStreamingAsync(IControlConnection connection, ChannelReader<(ControlMessageType Type, byte[] Payload)> reader)
    {
        await foreach (var (type, payload) in reader.ReadAllAsync())
        {
            switch (type)
            {
                case ControlMessageType.ConfigureStream:
                    var configure = ControlMessageSerializer.Deserialize<ConfigureStreamMessage>(payload);
                    await StartStreamingAsync(connection, configure).ConfigureAwait(false);
                    break;

                case ControlMessageType.StopStream:
                    await StopStreamingAsync().ConfigureAwait(false);
                    SetState(RemoteConnectionState.Paired);
                    break;

                case ControlMessageType.SwitchCamera:
                    _currentFacing = _currentFacing == CameraFacing.Front ? CameraFacing.Rear : CameraFacing.Front;
                    await _cameraService.SwitchCameraAsync().ConfigureAwait(false);
                    break;

                case ControlMessageType.Heartbeat:
                    var heartbeat = ControlMessageSerializer.Deserialize<HeartbeatMessage>(payload);
                    await connection.SendAsync(ControlMessageType.HeartbeatAck, new HeartbeatAckMessage(heartbeat.TimestampUtcTicks)).ConfigureAwait(false);
                    break;

                case ControlMessageType.QualityHint:
                    var hint = ControlMessageSerializer.Deserialize<QualityHintMessage>(payload);
                    await _cameraService.SetBitrateAsync(hint.SuggestedConfig.BitrateBps).ConfigureAwait(false);
                    _logger.LogInformation("Adjusted encoder bitrate to {BitrateBps} bps per desktop's quality hint", hint.SuggestedConfig.BitrateBps);
                    break;

                default:
                    _logger.LogInformation("Ignoring unexpected message {Type} in streaming phase", type);
                    break;
            }
        }
    }

    private async Task StartStreamingAsync(IControlConnection connection, ConfigureStreamMessage configure)
    {
        await connection.SendAsync(ControlMessageType.ConfigureAck, new ConfigureAckMessage(true, null)).ConfigureAwait(false);

        var target = new IPEndPoint(connection.RemoteEndPoint.Address, configure.VideoPort);

        // A control connection whose remote address is loopback can only be
        // the desktop reaching us through an `adb forward` tunnel (see
        // DiscoveryConstants.ControlPort's doc comment) - a genuine Wi-Fi
        // peer's address is never our own loopback. USB (ADB) only tunnels
        // TCP (`adb forward`/`adb reverse` don't carry UDP), so the video
        // channel needs to switch transport too, not just the control one.
        IVideoFrameSender sender = IPAddress.IsLoopback(connection.RemoteEndPoint.Address)
            ? new TcpVideoSender(target)
            : new UdpVideoSender(target);
        _videoSender = sender;

        _cameraService.FrameEncoded += OnFrameEncoded;

        await _cameraService.StartAsync(_currentFacing, configure.Config, PreviewTarget).ConfigureAwait(false);
        SetState(RemoteConnectionState.Streaming);
        _logger.LogInformation("Streaming to {Target} at {Config}", target, configure.Config);
    }

    private async void OnFrameEncoded(object? sender, EncodedVideoFrame frame)
    {
        if (_videoSender is not { } videoSender)
        {
            return;
        }

        try
        {
            await videoSender.SendFrameAsync(frame.Data, (ulong)frame.PresentationTimeUs, frame.IsKeyFrame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send an encoded frame");
        }
    }

    private async Task StopStreamingAsync()
    {
        if (_videoSender is null)
        {
            return;
        }

        _cameraService.FrameEncoded -= OnFrameEncoded;
        await _cameraService.StopAsync().ConfigureAwait(false);
        await _videoSender.DisposeAsync().ConfigureAwait(false);
        _videoSender = null;
    }

    private static async Task<(ControlMessageType Type, byte[] Payload)> ReceiveAsync(ChannelReader<(ControlMessageType Type, byte[] Payload)> reader)
    {
        if (!await reader.WaitToReadAsync().ConfigureAwait(false) || !reader.TryRead(out var item))
        {
            throw new IOException("Connection closed before the expected message arrived.");
        }

        return item;
    }

    private void SetState(RemoteConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.ConnectionAccepted -= OnConnectionAccepted;
        await _listener.DisposeAsync().ConfigureAwait(false);

        foreach (var advertiser in _advertisers)
        {
            await advertiser.DisposeAsync().ConfigureAwait(false);
        }

        await StopStreamingAsync().ConfigureAwait(false);
    }
}
