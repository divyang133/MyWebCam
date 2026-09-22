using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using LocalWebcam.Network;
using LocalWebcam.Network.Control;
using LocalWebcam.Network.Video;
using LocalWebcam.Protocol.Control;
using LocalWebcam.Protocol.Video;
using LocalWebcam.Security.Pairing;
using LocalWebcam.Shared.Network;
using LocalWebcam.Shared.VirtualCamera;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using LocalWebcam.Windows.Discovery;
using LocalWebcam.Windows.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Desktop.Services;

/// <summary>
/// The desktop's side: browses for phones, connects, runs the
/// pairing/reconnect handshake as the initiator, configures and starts
/// streaming, receives reassembled frames via <see cref="IVideoFrameReceiver"/>,
/// and decodes them to NV12 pixels via <see cref="IVideoDecoder"/> (Phase 3).
/// </summary>
public sealed class DesktopConnectionService : IAsyncDisposable
{
    private readonly IDeviceDiscovery _discovery;
    private readonly ITrustedDeviceStore _trustStore;
    private readonly AdbUsbBridge _adbBridge;
    private readonly string _deviceId;
    private readonly string _displayName;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DesktopConnectionService> _logger;
    private readonly Dictionary<long, long> _receiveTimestamps = [];
    private readonly Dictionary<string, DiscoveredDevice> _knownDevices = [];

    private readonly IVirtualCamera _virtualCamera;
    private bool _virtualCameraReady;

    private IControlConnection? _connection;
    private IVideoFrameReceiver? _videoReceiver;
    private IVideoDecoder? _videoDecoder;
    private CancellationTokenSource? _streamingCts;

    // Frames used to arrive at the decoder in bursty, uneven spacing
    // (network jitter) and this was invisible because the decoder itself
    // held ~2-10 frames (software) or ~584ms (unconfigured hardware/DXVA)
    // internally before returning them. Once hardware decode was switched
    // to low-latency mode (see WindowsVideoDecoder.TrySetLowLatencyViaCodecApi)
    // that incidental smoothing dropped to ~0, and the raw arrival jitter
    // became directly visible as flickering/stuttering playback. This small
    // playout buffer restores steady pacing deliberately, at a much smaller
    // latency cost than the buffering it replaces.
    //
    // An earlier version of this gated dispatch to a PeriodicTimer fixed at
    // exactly 1/frameRate, dequeuing one frame per tick - confirmed on real
    // hardware to cause a *new* frame-drop problem: any tiny drift between
    // the sender's actual output rate and that assumed nominal rate (timer
    // jitter counts too) made the queue creep upward indefinitely, hitting
    // JitterBufferMaxDepthFrames and dropping frames that were never
    // actually late. Draining by age instead - dispatch everything that has
    // sat for at least the target delay, checked on a short fixed poll - is
    // self-correcting: it always matches whatever rate frames actually
    // arrive at, and only the true safety-net cap below drops anything.
    private const int JitterBufferTargetDepthFrames = 2;
    private const int JitterBufferMaxDepthFrames = 20;
    private const int JitterBufferPollIntervalMs = 5;

    // Reliability (Phase 5): automatic reconnection after an unexpected drop,
    // and a heartbeat used to detect drops TCP itself doesn't notice quickly
    // (e.g. the phone's Wi-Fi radio going to sleep) - see docs/PROTOCOL.md.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(16); // ~3 missed beats
    private static readonly TimeSpan QualityCheckInterval = TimeSpan.FromSeconds(5);
    private const double PacketLossDegradeThreshold = 0.05;
    private const double PacketLossRecoverThreshold = 0.01;

    private DiscoveredDevice? _currentDevice;
    private volatile bool _userInitiatedDisconnect;
    private bool _reachedPairedThisAttempt;
    private CancellationTokenSource? _reconnectCts;
    private DateTime _lastHeartbeatAckAtUtc;
    private bool _isLowQuality;

    public RemoteConnectionState State { get; private set; } = RemoteConnectionState.Idle;

    /// <summary>
    /// Time from a frame finishing UDP reassembly to it being decoded — a
    /// real, locally-measurable processing latency. Not full glass-to-glass
    /// (capture-to-render) latency: that needs clock-offset estimation
    /// between phone and desktop, which doesn't exist until the Phase 5
    /// heartbeat mechanism is built. Not mislabeling this is deliberate.
    /// </summary>
    public double LastDecodeLatencyMs { get; private set; }

    public event EventHandler<RemoteConnectionState>? StateChanged;

    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public event EventHandler<string>? DeviceLost;

    /// <summary>Informational only — the desktop has no Allow/Reject action; the phone's user decides.</summary>
    public event EventHandler<string>? PairingCodeReady;

    public event EventHandler<ReassembledVideoFrame>? FrameReceived;

    public event EventHandler<DecodedVideoFrame>? FrameDecoded;

    public event EventHandler<string>? ErrorOccurred;

    public DesktopConnectionService(IDeviceDiscovery discovery, ITrustedDeviceStore trustStore, IVirtualCamera virtualCamera, AdbUsbBridge adbBridge, string deviceId, string displayName, ILoggerFactory loggerFactory)
    {
        _discovery = discovery;
        _trustStore = trustStore;
        _adbBridge = adbBridge;
        _virtualCamera = virtualCamera;
        _deviceId = deviceId;
        _displayName = displayName;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DesktopConnectionService>();

        _discovery.DeviceFound += (_, device) =>
        {
            _knownDevices[device.DeviceId] = device;
            DeviceFound?.Invoke(this, device);
        };
        _discovery.DeviceLost += (_, deviceId) => DeviceLost?.Invoke(this, deviceId);

        _ = InitializeVirtualCameraAsync();
        _ = FixUsbTetheringNetworkConflictIfNeededAsync();
    }

    /// <summary>
    /// Checks for and fixes the USB-tethering/VPN conflict (see
    /// <see cref="NetworkAdapterFixer"/>) on every launch, independent of
    /// virtual camera setup - this is a general Windows networking issue,
    /// not specific to streaming being active. Naturally a no-op (no
    /// elevation prompt) whenever USB tethering isn't currently active,
    /// since the check requires the adapter to actually be up with a
    /// gateway configured. Same "must not block the rest of the app"
    /// principle as <see cref="InitializeVirtualCameraAsync"/>.
    /// </summary>
    private async Task FixUsbTetheringNetworkConflictIfNeededAsync()
    {
        try
        {
            var adapter = NetworkAdapterFixer.FindAdapterNeedingFix();
            if (adapter is null)
            {
                return;
            }

            var fixApplied = await NetworkAdapterFixer.TryFixAsync(adapter, _logger).ConfigureAwait(false);
            if (!fixApplied)
            {
                // Same reasoning as VirtualCameraComRegistrar's failure message:
                // don't promise a retry will succeed, since Windows returns the
                // same result whether a real prompt was declined or elevation
                // was denied by policy before any UI appeared.
                ErrorOccurred?.Invoke(this, $"'{adapter}' (your phone's USB connection) may interfere with a VPN, which needs administrator approval to fix. If that's not available on this computer, USB tethering and any active VPN may not work reliably together here - Wi-Fi is unaffected.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USB tethering/VPN conflict check failed");
        }
    }

    /// <summary>
    /// Registers and starts the OS-level virtual camera device as soon as the
    /// app launches (independent of any phone being connected) so it shows up
    /// in Settings -> Cameras / OBS / Teams immediately; only frame delivery
    /// depends on a phone actually streaming. A failure here (e.g. Windows
    /// version below 11 22H2 - see docs/WINDOWS_VIRTUAL_CAMERA.md) is reported
    /// but must not prevent the rest of the app (discovery, pairing, preview)
    /// from working. Remembers the last-picked quality across app restarts
    /// (see VirtualCameraConfig) rather than always resetting to 720p30.
    /// </summary>
    private async Task InitializeVirtualCameraAsync()
    {
        var (resolution, frameRate) = VirtualCameraConfig.Load();
        SelectedQuality = new VideoStreamConfig(resolution, frameRate, InferBitrateBps(resolution, frameRate));

        try
        {
            await _virtualCamera.InitializeAsync("Local Webcam", resolution, frameRate).ConfigureAwait(false);
            await StartVirtualCameraWithAutoRegisterAsync(resolution, frameRate).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virtual camera device could not be started");
            ErrorOccurred?.Invoke(this, $"Virtual camera unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the already-initialized virtual camera device, and if that
    /// fails with the exact, expected "COM server not registered yet"
    /// error, registers it automatically (elevated - see
    /// VirtualCameraComRegistrar) and retries once, rather than requiring
    /// the user to run a separate setup script.
    /// </summary>
    private async Task StartVirtualCameraWithAutoRegisterAsync(VideoResolution resolution, int frameRate)
    {
        try
        {
            await _virtualCamera.StartAsync().ConfigureAwait(false);
            _virtualCameraReady = true;
            _logger.LogInformation("Virtual camera device is registered and running at {Quality}", SelectedQuality);
        }
        catch (Exception ex) when (ex.HResult == VirtualCameraComRegistrar.ClassNotRegisteredHResult
            || ex.HResult == VirtualCameraComRegistrar.ModuleNotFoundHResult)
        {
            var registered = await VirtualCameraComRegistrar.TryRegisterAsync(_logger).ConfigureAwait(false);
            if (!registered)
            {
                // Deliberately doesn't say "restart and approve the prompt" -
                // that's only true if a prompt was actually shown and
                // declined. When elevation itself is denied by policy before
                // any UI appears (a real, observed case on a locked-down
                // corporate machine - see docs/WINDOWS_VIRTUAL_CAMERA.md),
                // Windows returns the exact same ERROR_CANCELLED either way,
                // so this code can't tell those two cases apart - and
                // promising a retry will help isn't honest when it won't.
                ErrorOccurred?.Invoke(this, "The virtual camera couldn't finish its one-time setup, which needs administrator approval. If that's not available on this computer, the virtual camera can't be enabled here - but pairing, streaming, and this app's own preview still work normally without it.");
                return;
            }

            // The IMFVirtualCamera handle was created via MFCreateVirtualCamera
            // *before* the COM server was registered, and the Frame Server
            // appears to cache that failed activation against this specific
            // device instance: simply retrying StartAsync() on the same
            // handle fails again, now with a different HRESULT (0x80008085),
            // even though regsvr32 just succeeded. Removing and recreating
            // the device (ReconfigureAsync) forces a fresh activation attempt
            // against the now-registered CLSID; ReconfigureAsync only restarts
            // it if it was already started before, which it wasn't (Start()
            // just threw), so an explicit StartAsync() follows.
            await _virtualCamera.ReconfigureAsync(resolution, frameRate).ConfigureAwait(false);
            await _virtualCamera.StartAsync().ConfigureAwait(false);
            _virtualCameraReady = true;
            _logger.LogInformation("Virtual camera device is registered and running at {Quality} (after first-run setup)", SelectedQuality);
        }
    }

    /// <summary>The quality (resolution/frame rate/bitrate) both the virtual camera and the next connection will use. Change via <see cref="SetQualityAsync"/>.</summary>
    public VideoStreamConfig SelectedQuality { get; private set; } = VideoStreamConfig.Default720p30;

    private static int InferBitrateBps(VideoResolution resolution, int frameRate) =>
        VideoStreamConfig.Presets.FirstOrDefault(p => p.Config.Resolution == resolution && p.Config.FrameRate == frameRate).Config?.BitrateBps
            ?? VideoStreamConfig.Default720p30.BitrateBps;

    /// <summary>
    /// Changes the streaming quality: reconfigures the OS-level virtual
    /// camera (a brief device remove/recreate - see
    /// <see cref="IVirtualCamera.ReconfigureAsync"/>) and remembers the
    /// choice for next launch. Refuses while actively streaming, since the
    /// phone's encoder and the virtual camera's advertised format both need
    /// to change together and there's no clean way to do that mid-session -
    /// the UI should disable the quality picker while connected instead of
    /// relying on this to enforce it silently.
    /// </summary>
    public async Task SetQualityAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        if (config == SelectedQuality)
        {
            return;
        }

        if (State is RemoteConnectionState.Paired or RemoteConnectionState.Streaming or RemoteConnectionState.Reconnecting)
        {
            throw new InvalidOperationException("Disconnect before changing quality.");
        }

        await _virtualCamera.ReconfigureAsync(config.Resolution, config.FrameRate, cancellationToken).ConfigureAwait(false);
        SelectedQuality = config;
        _logger.LogInformation("Streaming quality changed to {Quality}", config);
    }

    public Task StartDiscoveryAsync(CancellationToken cancellationToken = default) => _discovery.StartAsync(cancellationToken);

    public double PacketLossRatio => _videoReceiver?.PacketLossRatio ?? 0;

    public async Task ConnectAsync(DiscoveredDevice device, CancellationToken cancellationToken = default)
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
        _userInitiatedDisconnect = false;
        _currentDevice = device;

        await RunSessionAsync(device, cancellationToken).ConfigureAwait(false);

        if (_reachedPairedThisAttempt && !_userInitiatedDisconnect)
        {
            // Reached at least Paired (a real, previously-working session)
            // and this wasn't a user-requested disconnect - the drop was
            // unexpected, so retry automatically instead of leaving the user
            // to notice and reconnect by hand.
            _ = ReconnectLoopAsync(device);
        }
    }

    /// <summary>One connection attempt: dial, then run the protocol/streaming/maintenance loop until it ends.</summary>
    private async Task RunSessionAsync(DiscoveredDevice device, CancellationToken cancellationToken)
    {
        _reachedPairedThisAttempt = false;
        SetState(State == RemoteConnectionState.Reconnecting ? RemoteConnectionState.Reconnecting : RemoteConnectionState.Connecting);

        try
        {
            if (UsbAdbDeviceDiscovery.TryGetAdbSerial(device.DeviceId, out var serial))
            {
                // Stop USB device polling for the duration of this session -
                // see AdbUsbBridge.SuspendPolling's doc comment. Set before
                // ForwardAsync (also an adb.exe invocation) so there's no
                // window where a poll could still land concurrently with it.
                _adbBridge.SuspendPolling = true;

                // Must exist before dialing: this is what makes
                // 127.0.0.1:ControlPort (device.Address/ControlPort for a
                // USB-sourced device) actually reach the phone's control
                // listener at all, tunneled over the existing USB
                // debugging connection instead of any IP network path.
                await _adbBridge.ForwardAsync(serial, device.ControlPort, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Dialing {Device} at {Address}:{Port} (source={Source})", device.DisplayName, device.Address, device.ControlPort, device.Source);
            var connection = await TcpControlConnection.ConnectAsync(
                device.Address, device.ControlPort, _loggerFactory.CreateLogger<TcpControlConnection>(), cancellationToken).ConfigureAwait(false);
            _connection = connection;

            var channel = Channel.CreateUnbounded<(ControlMessageType Type, byte[] Payload)>();
            connection.MessageReceived += (_, e) => channel.Writer.TryWrite((e.Type, e.Payload));
            connection.Disconnected += (_, _) => channel.Writer.TryComplete();
            connection.StartReceiving();

            await RunProtocolAsync(device, connection, channel.Reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection to {Device} failed", device.DisplayName);
            ErrorOccurred?.Invoke(this, ex.Message);

            // Otherwise a failed dial (e.g. the phone's control listener
            // wasn't actually reachable yet) would leave USB polling
            // suspended forever - CleanupUsbTunnelsAsync's own guards make
            // this safe to call even when nothing was actually set up.
            await CleanupUsbTunnelsAsync().ConfigureAwait(false);

            SetState(RemoteConnectionState.Failed);
        }
    }

    /// <summary>
    /// Retries <see cref="RunSessionAsync"/> with exponential backoff (capped
    /// at 30s) after a previously-streaming session drops unexpectedly.
    /// Resets the backoff after each successful reconnect, since a session
    /// that ran fine for a while and then dropped again is a fresh problem,
    /// not a continuation of the last one.
    /// </summary>
    private async Task ReconnectLoopAsync(DiscoveredDevice device)
    {
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        for (var attempt = 1; !token.IsCancellationRequested && !_userInitiatedDisconnect; attempt++)
        {
            SetState(RemoteConnectionState.Reconnecting);
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            _logger.LogInformation("Reconnecting to {Device} (attempt {Attempt}) in {Delay}", device.DisplayName, attempt, delay);

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (_userInitiatedDisconnect)
                {
                    return;
                }

                // Re-resolve the device by ID from live discovery before
                // retrying: its control port (and possibly address) can
                // change across a drop - e.g. the phone app process
                // restarting binds a fresh ephemeral TCP port - so reusing
                // the address/port captured at the very first connect
                // forever would retry against a now-dead endpoint
                // indefinitely instead of finding the device again.
                var currentDevice = _knownDevices.GetValueOrDefault(device.DeviceId, device);

                await RunSessionAsync(currentDevice, token).ConfigureAwait(false);

                if (_userInitiatedDisconnect)
                {
                    return;
                }

                if (_reachedPairedThisAttempt)
                {
                    attempt = 0; // reached a working session again - reset backoff for the *next* drop
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunProtocolAsync(DiscoveredDevice device, IControlConnection connection, ChannelReader<(ControlMessageType Type, byte[] Payload)> reader, CancellationToken cancellationToken)
    {
        await connection.SendAsync(ControlMessageType.Hello, new HelloMessage(1, _deviceId, _displayName), cancellationToken).ConfigureAwait(false);

        var (ackType, ackPayload) = await ReceiveAsync(reader).ConfigureAwait(false);
        if (ackType != ControlMessageType.HelloAck)
        {
            throw new IOException($"Expected HelloAck, got {ackType}.");
        }

        var ack = ControlMessageSerializer.Deserialize<HelloAckMessage>(ackPayload);
        var localTrust = await _trustStore.FindAsync(ack.DeviceId, cancellationToken).ConfigureAwait(false);

        var (authenticated, reason) = await AuthenticateAsync(connection, reader, ack, localTrust, cancellationToken).ConfigureAwait(false);
        if (!authenticated)
        {
            ErrorOccurred?.Invoke(this, reason ?? "Pairing failed.");
            SetState(RemoteConnectionState.Failed);
            return;
        }

        _reachedPairedThisAttempt = true;
        SetState(RemoteConnectionState.Paired);
        await StartStreamingAsync(device, connection, cancellationToken).ConfigureAwait(false);
        await RunMaintenanceLoopAsync(connection, reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs for the lifetime of a streaming session: sends periodic
    /// heartbeats and disconnects if too many go unacknowledged, adapts the
    /// requested bitrate to observed packet loss, and processes whatever
    /// inbound control messages arrive (chiefly <see cref="HeartbeatAckMessage"/>).
    /// Returns when the connection drops (the reader completes), a heartbeat
    /// times out, or <paramref name="cancellationToken"/> fires.
    /// </summary>
    private async Task RunMaintenanceLoopAsync(IControlConnection connection, ChannelReader<(ControlMessageType Type, byte[] Payload)> reader, CancellationToken cancellationToken)
    {
        _isLowQuality = false;
        _lastHeartbeatAckAtUtc = DateTime.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastHeartbeatSentAtUtc = DateTime.UtcNow;
        var lastQualityCheckAtUtc = DateTime.UtcNow;

        var readTask = ReadInboundAsync(reader, cancellationToken);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (readTask.IsCompleted)
                {
                    _logger.LogInformation("Maintenance loop ending: the inbound read task already completed (connection dropped from the read side)");
                    break;
                }

                var now = DateTime.UtcNow;

                if (now - _lastHeartbeatAckAtUtc > HeartbeatTimeout)
                {
                    _logger.LogWarning("No heartbeat response for {Timeout} - treating the connection as dropped", HeartbeatTimeout);
                    break;
                }

                if (now - lastHeartbeatSentAtUtc >= HeartbeatInterval)
                {
                    lastHeartbeatSentAtUtc = now;
                    try
                    {
                        await connection.SendAsync(ControlMessageType.Heartbeat, new HeartbeatMessage(now.Ticks), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(ex, "Failed to send a heartbeat - connection likely already dropped");
                        break;
                    }
                }

                if (now - lastQualityCheckAtUtc >= QualityCheckInterval)
                {
                    lastQualityCheckAtUtc = now;
                    await MaybeAdaptQualityAsync(connection, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // If the user asked to disconnect, DisconnectAsync() already
            // owns (or is about to own) tearing everything down and setting
            // Idle - setting _userInitiatedDisconnect happens there *before*
            // it disposes the connection, which is what unblocks this loop,
            // so there is no race between the two cleanup paths. Otherwise
            // this was an unexpected drop (heartbeat timeout, TCP reset,
            // ...) and this loop owns cleanup instead.
            if (!_userInitiatedDisconnect)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }

                await CleanupUsbTunnelsAsync().ConfigureAwait(false);

                if (_videoReceiver is not null)
                {
                    await _videoReceiver.DisposeAsync().ConfigureAwait(false);
                    _videoReceiver = null;
                }

                _videoDecoder?.Dispose();
                _videoDecoder = null;
                _receiveTimestamps.Clear();
                _streamingCts?.Cancel();
                _streamingCts?.Dispose();
                _streamingCts = null;

                SetState(RemoteConnectionState.Failed);
            }

            await readTask.ConfigureAwait(false);
        }
    }

    private async Task ReadInboundAsync(ChannelReader<(ControlMessageType Type, byte[] Payload)> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (type, payload) in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (type)
                {
                    case ControlMessageType.HeartbeatAck:
                        _lastHeartbeatAckAtUtc = DateTime.UtcNow;
                        break;

                    case ControlMessageType.Error:
                        var error = ControlMessageSerializer.Deserialize<ErrorMessage>(payload);
                        ErrorOccurred?.Invoke(this, error.Message);
                        break;

                    default:
                        _logger.LogInformation("Ignoring unexpected message {Type} during streaming", type);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Steps between exactly two bitrate presets based on observed UDP packet
    /// loss, with separate degrade/recover thresholds (hysteresis) so it
    /// doesn't flip back and forth right at one boundary value. Resolution is
    /// never touched - the virtual camera's media type is fixed for the
    /// connection's lifetime (see VirtualCameraFrameChannel).
    /// </summary>
    private async Task MaybeAdaptQualityAsync(IControlConnection connection, CancellationToken cancellationToken)
    {
        var loss = PacketLossRatio;

        // Measure this window only, not the connection's lifetime average -
        // see IVideoFrameReceiver.ResetLossStats's doc comment for why a
        // lifetime average was a real problem (a one-time startup burst
        // could keep bitrate stuck low for the rest of the session).
        _videoReceiver?.ResetLossStats();

        if (!_isLowQuality && loss > PacketLossDegradeThreshold)
        {
            _isLowQuality = true;
            var lowBitrate = SelectedQuality with { BitrateBps = SelectedQuality.BitrateBps * 3 / 8 };
            _logger.LogInformation("Packet loss {Loss:P1} exceeds threshold - requesting lower bitrate ({BitrateBps} bps)", loss, lowBitrate.BitrateBps);
            await connection.SendAsync(ControlMessageType.QualityHint, new QualityHintMessage(lowBitrate), cancellationToken).ConfigureAwait(false);
        }
        else if (_isLowQuality && loss < PacketLossRecoverThreshold)
        {
            _isLowQuality = false;
            _logger.LogInformation("Packet loss {Loss:P1} recovered - requesting normal bitrate", loss);
            await connection.SendAsync(ControlMessageType.QualityHint, new QualityHintMessage(SelectedQuality), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool Success, string? Reason)> AuthenticateAsync(
        IControlConnection connection,
        ChannelReader<(ControlMessageType Type, byte[] Payload)> reader,
        HelloAckMessage ack,
        TrustedDevice? localTrust,
        CancellationToken cancellationToken)
    {
        var (type, payload) = await ReceiveAsync(reader).ConfigureAwait(false);

        if (type == ControlMessageType.TrustChallenge)
        {
            var challenge = ControlMessageSerializer.Deserialize<TrustChallengeMessage>(payload);

            // No local record despite the phone believing we're trusted (an
            // asymmetric state - e.g. this device's trust store was cleared):
            // send a response that will deliberately fail verification
            // rather than hang the phone waiting for one.
            var hmacBytes = localTrust is not null
                ? new HMACSHA256(localTrust.TrustKey).ComputeHash(challenge.Nonce)
                : new byte[32];

            await connection.SendAsync(ControlMessageType.TrustResponse, new TrustResponseMessage(hmacBytes), cancellationToken).ConfigureAwait(false);

            var (resultType, resultPayload) = await ReceiveAsync(reader).ConfigureAwait(false);
            var result = ExpectPairResult(resultType, resultPayload);
            return (result.Success, result.Reason);
        }

        if (type == ControlMessageType.PairChallenge)
        {
            var challenge = ControlMessageSerializer.Deserialize<PairChallengeMessage>(payload);

            using var session = new PairingSession();
            session.CompleteKeyExchange(challenge.PublicKey);
            var code = session.ComputeVerificationCode(session.LocalPublicKey, challenge.PublicKey);

            SetState(RemoteConnectionState.AwaitingPairingCode);
            PairingCodeReady?.Invoke(this, code);

            await connection.SendAsync(ControlMessageType.PairRequest, new PairRequestMessage(session.LocalPublicKey), cancellationToken).ConfigureAwait(false);

            var (resultType, resultPayload) = await ReceiveAsync(reader).ConfigureAwait(false);
            var result = ExpectPairResult(resultType, resultPayload);

            if (result.Success)
            {
                await _trustStore.SaveAsync(new TrustedDevice(ack.DeviceId, ack.DisplayName, DateTimeOffset.UtcNow, session.DeriveTrustKey()), cancellationToken).ConfigureAwait(false);
            }

            return (result.Success, result.Reason);
        }

        throw new IOException($"Unexpected message {type} during authentication.");
    }

    private static PairResultMessage ExpectPairResult(ControlMessageType type, byte[] payload) =>
        type == ControlMessageType.PairResult
            ? ControlMessageSerializer.Deserialize<PairResultMessage>(payload)
            : throw new IOException($"Expected PairResult, got {type}.");

    private async Task StartStreamingAsync(DiscoveredDevice device, IControlConnection connection, CancellationToken cancellationToken)
    {
        var config = SelectedQuality;

        var decoder = new WindowsVideoDecoder(_loggerFactory.CreateLogger<WindowsVideoDecoder>());
        decoder.Configure(config.Resolution);
        decoder.Error += (_, e) => ErrorOccurred?.Invoke(this, e.Message);
        decoder.FrameDecoded += (_, frame) =>
        {
            if (_receiveTimestamps.Remove(frame.PresentationTimeUs, out var receivedAt))
            {
                LastDecodeLatencyMs = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
            }

            if (_virtualCameraReady)
            {
                _virtualCamera.PushFrame(frame);
            }

            FrameDecoded?.Invoke(this, frame);
        };
        _videoDecoder = decoder;

        var jitterBuffer = new Queue<(ReassembledVideoFrame Frame, long EnqueuedAtMs)>();
        var jitterLock = new object();

        // `adb forward`/`adb reverse` only carry TCP, not UDP - USB (ADB)
        // sessions need the TCP video transport instead of the normal
        // UDP one. Same loopback signal PhoneConnectionService uses on its
        // side to make the same choice (see its doc comment there).
        var isUsb = IPAddress.IsLoopback(device.Address);
        IVideoFrameReceiver receiver = isUsb
            ? new TcpVideoReceiver(0, _loggerFactory.CreateLogger<TcpVideoReceiver>())
            : new UdpVideoReceiver(0, _loggerFactory.CreateLogger<UdpVideoReceiver>());
        receiver.FrameReceived += (_, frame) =>
        {
            _receiveTimestamps[(long)frame.TimestampMicros] = Stopwatch.GetTimestamp();
            FrameReceived?.Invoke(this, frame);

            lock (jitterLock)
            {
                jitterBuffer.Enqueue((frame, Environment.TickCount64));

                // The decoder/network can't keep up - prefer the freshest
                // frames over growing the buffer (and its latency) unbounded.
                while (jitterBuffer.Count > JitterBufferMaxDepthFrames)
                {
                    jitterBuffer.Dequeue();
                }
            }
        };
        await receiver.StartAsync(cancellationToken).ConfigureAwait(false);
        _videoReceiver = receiver;

        // The phone will connect out to 127.0.0.1:<receiver.Port> (see
        // PhoneConnectionService.StartStreamingAsync) expecting that to
        // reach us - only true once `adb reverse` tunnels that phone-local
        // port back to this exact desktop port. Same port number both
        // sides, same reasoning as the control channel's `adb forward`.
        if (isUsb && UsbAdbDeviceDiscovery.TryGetAdbSerial(device.DeviceId, out var serial))
        {
            await _adbBridge.ReverseAsync(serial, receiver.Port!.Value, cancellationToken).ConfigureAwait(false);
        }

        var targetDelayMs = (long)(1000.0 / Math.Max(1, config.FrameRate) * JitterBufferTargetDepthFrames);
        _streamingCts = new CancellationTokenSource();
        _ = RunJitterDispatchLoopAsync(decoder, jitterBuffer, jitterLock, targetDelayMs, _streamingCts.Token);

        await connection.SendAsync(ControlMessageType.ConfigureStream, new ConfigureStreamMessage(config, receiver.Port!.Value), cancellationToken).ConfigureAwait(false);
        await connection.SendAsync(ControlMessageType.StartStream, new StartStreamMessage(), cancellationToken).ConfigureAwait(false);

        SetState(RemoteConnectionState.Streaming);
        _logger.LogInformation("Streaming requested; receiving on {Transport} port {Port}", isUsb ? "TCP (USB)" : "UDP", receiver.Port);
    }

    /// <summary>
    /// Dequeues into <paramref name="decoder"/> every frame that has sat in
    /// <paramref name="buffer"/> for at least <paramref name="targetDelayMs"/>,
    /// checked on a short fixed poll - see <see cref="JitterBufferTargetDepthFrames"/>'s
    /// doc comment for why age-based draining, not a fixed dispatch rate, is
    /// what avoids introducing new frame drops of its own.
    /// </summary>
    private static async Task RunJitterDispatchLoopAsync(IVideoDecoder decoder, Queue<(ReassembledVideoFrame Frame, long EnqueuedAtMs)> buffer, object bufferLock, long targetDelayMs, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(JitterBufferPollIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    ReassembledVideoFrame? frame = null;

                    lock (bufferLock)
                    {
                        if (buffer.Count > 0 && Environment.TickCount64 - buffer.Peek().EnqueuedAtMs >= targetDelayMs)
                        {
                            frame = buffer.Dequeue().Frame;
                        }
                    }

                    if (frame is null)
                    {
                        break;
                    }

                    decoder.SubmitEncodedFrame(frame.Data, (long)frame.TimestampMicros, frame.IsKeyFrame);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SwitchCameraAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.SendAsync(ControlMessageType.SwitchCamera, new SwitchCameraMessage(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        // Set before touching the connection: disposing it below is what
        // wakes RunMaintenanceLoopAsync's reader loop, and it must see this
        // flag already true so it skips its own (redundant) cleanup instead
        // of racing this method - see the comment in that loop's finally.
        _userInitiatedDisconnect = true;
        _reconnectCts?.Cancel();

        if (_connection is not null)
        {
            try
            {
                await _connection.SendAsync(ControlMessageType.StopStream, new StopStreamMessage()).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort - the connection may already be gone.
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        await CleanupUsbTunnelsAsync().ConfigureAwait(false);

        if (_videoReceiver is not null)
        {
            await _videoReceiver.DisposeAsync().ConfigureAwait(false);
            _videoReceiver = null;
        }

        _videoDecoder?.Dispose();
        _videoDecoder = null;
        _receiveTimestamps.Clear();
        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        _streamingCts = null;
        LastDecodeLatencyMs = 0;

        SetState(RemoteConnectionState.Idle);
    }

    /// <summary>
    /// Best-effort: removes the `adb forward`/`adb reverse` rules a USB
    /// session set up (see <see cref="RunSessionAsync"/>/<see cref="StartStreamingAsync"/>),
    /// so they don't pile up across repeated connect/disconnect cycles. Not
    /// removing them isn't a real problem either way - adb just leaves a
    /// harmless stale rule pointing at a port nothing's listening on
    /// anymore - which is why failures here are swallowed rather than
    /// surfaced. Must run before <see cref="_videoReceiver"/> is disposed:
    /// it needs the port that was actually reversed.
    /// </summary>
    private async Task CleanupUsbTunnelsAsync()
    {
        if (_currentDevice is not { } device || !UsbAdbDeviceDiscovery.TryGetAdbSerial(device.DeviceId, out var serial))
        {
            return;
        }

        // Resume USB device polling now that this session is ending - see
        // AdbUsbBridge.SuspendPolling's doc comment.
        _adbBridge.SuspendPolling = false;

        await _adbBridge.RemoveForwardAsync(serial, device.ControlPort).ConfigureAwait(false);

        if (_videoReceiver?.Port is { } port)
        {
            await _adbBridge.RemoveReverseAsync(serial, port).ConfigureAwait(false);
        }
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
        await DisconnectAsync().ConfigureAwait(false);
        await _discovery.DisposeAsync().ConfigureAwait(false);
        await _virtualCamera.DisposeAsync().ConfigureAwait(false);
    }
}
