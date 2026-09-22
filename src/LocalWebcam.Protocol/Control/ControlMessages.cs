using LocalWebcam.Shared.Video;

namespace LocalWebcam.Protocol.Control;

// Payload records for each ControlMessageType. Serialized as JSON (spec
// section 8: fine for infrequent control messages, unlike video frames).

public sealed record HelloMessage(int ProtocolVersion, string DeviceId, string DisplayName);

public sealed record HelloAckMessage(int ProtocolVersion, string DeviceId, string DisplayName, bool IsTrusted);

public sealed record PairRequestMessage(byte[] PublicKey);

public sealed record PairChallengeMessage(byte[] PublicKey);

/// <summary>
/// Terminal message for the authentication phase, sent by the phone —
/// either after the user taps Allow/Reject (full pairing) or after
/// verifying a <see cref="TrustResponseMessage"/> (reconnect).
/// </summary>
public sealed record PairResultMessage(bool Success, string? Reason);

public sealed record TrustChallengeMessage(byte[] Nonce);

public sealed record TrustResponseMessage(byte[] Hmac);

/// <summary>
/// Sent by the desktop before <see cref="StartStreamMessage"/>.
/// <paramref name="VideoPort"/> is the UDP port the desktop's
/// <c>UdpVideoReceiver</c> is already bound to and listening on.
/// </summary>
public sealed record ConfigureStreamMessage(VideoStreamConfig Config, int VideoPort);

public sealed record ConfigureAckMessage(bool Accepted, string? Reason);

public sealed record StartStreamMessage;

public sealed record StopStreamMessage;

public sealed record SwitchCameraMessage;

public sealed record HeartbeatMessage(long TimestampUtcTicks);

public sealed record HeartbeatAckMessage(long TimestampUtcTicks);

public sealed record QualityHintMessage(VideoStreamConfig SuggestedConfig);

public sealed record ErrorMessage(string Code, string Message);

public sealed record DisconnectMessage(string? Reason);
