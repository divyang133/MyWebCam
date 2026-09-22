namespace LocalWebcam.Desktop.Services;

public enum RemoteConnectionState
{
    Idle,
    Connecting,
    AwaitingPairingCode,
    Paired,
    Streaming,

    /// <summary>
    /// A previously-streaming session dropped unexpectedly (e.g. Wi-Fi
    /// blip) and <see cref="DesktopConnectionService"/> is retrying with
    /// backoff, without the user having to reconnect manually.
    /// </summary>
    Reconnecting,

    Failed,
}
