using System.Net;
using LocalWebcam.Protocol.Control;

namespace LocalWebcam.Network.Control;

/// <summary>One TCP control-channel connection to a peer (either side).</summary>
public interface IControlConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>The peer's address — e.g. where the video channel's UDP packets should be sent/expected from.</summary>
    IPEndPoint RemoteEndPoint { get; }

    event EventHandler<ControlMessageReceivedEventArgs>? MessageReceived;

    event EventHandler? Disconnected;

    /// <summary>
    /// Starts the background receive loop that raises <see cref="MessageReceived"/>/
    /// <see cref="Disconnected"/>. Deliberately not automatic on construction:
    /// the caller must subscribe to those events first, or a message that
    /// arrives in the narrow window between the loop starting and the
    /// subscription happening would be silently dropped (observed on real
    /// hardware under load - see docs/PROTOCOL.md).
    /// </summary>
    void StartReceiving();

    Task SendAsync(ControlMessageType type, byte[] payload, CancellationToken cancellationToken = default);

    Task CloseAsync();
}

public static class ControlConnectionExtensions
{
    /// <summary>Serializes <paramref name="payload"/> to JSON and sends it as <paramref name="type"/>.</summary>
    public static Task SendAsync<T>(this IControlConnection connection, ControlMessageType type, T payload, CancellationToken cancellationToken = default) =>
        connection.SendAsync(type, ControlMessageSerializer.Serialize(payload), cancellationToken);
}
