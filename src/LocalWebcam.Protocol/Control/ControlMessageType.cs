namespace LocalWebcam.Protocol.Control;

/// <summary>Control-channel message types, per docs/PROTOCOL.md.</summary>
public enum ControlMessageType : byte
{
    Hello = 1,
    HelloAck = 2,

    // Full ECDH pairing (first-time trust establishment). PairResult is the
    // single terminal message for the whole authentication phase (sent by
    // the phone) regardless of whether it took the full-pairing or the
    // trust-reconnect path below.
    PairRequest = 3,
    PairChallenge = 4,
    PairResult = 5,

    // Lightweight reconnection for an already-trusted device: proves
    // possession of the stored trust key without repeating the
    // human-visible numeric-comparison step.
    TrustChallenge = 6,
    TrustResponse = 7,

    ConfigureStream = 8,
    ConfigureAck = 9,

    StartStream = 10,
    StopStream = 11,
    SwitchCamera = 12,

    Heartbeat = 13,
    HeartbeatAck = 14,

    QualityHint = 15,

    Error = 16,
    Disconnect = 17,
}
