namespace LocalWebcam.Network;

/// <summary>
/// Makes this device discoverable on the LAN (the phone side of discovery).
/// </summary>
public interface IServiceAdvertiser : IAsyncDisposable
{
    Task StartAsync(string deviceId, string displayName, int controlPort, CancellationToken cancellationToken = default);

    Task StopAsync();
}
