namespace LocalWebcam.Mobile.Services;

public enum RemoteConnectionState
{
    Idle,
    Advertising,
    Connected,
    AwaitingPairingApproval,
    Paired,
    Streaming,
    Error,
}
