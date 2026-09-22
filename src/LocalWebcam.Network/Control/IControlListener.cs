namespace LocalWebcam.Network.Control;

/// <summary>Accepts incoming control-channel connections (the phone's role - the desktop connects to it).</summary>
public interface IControlListener : IAsyncDisposable
{
    /// <summary>The bound local port, once <see cref="Start"/> has run (useful when starting on port 0).</summary>
    int? Port { get; }

    event EventHandler<IControlConnection>? ConnectionAccepted;

    void Start(int port);

    Task StopAsync();
}
