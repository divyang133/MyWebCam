using LocalWebcam.Protocol.Video;

namespace LocalWebcam.Network.Video;

/// <summary>Receives and reassembles UDP video packets (desktop side).</summary>
public interface IVideoFrameReceiver : IAsyncDisposable
{
    /// <summary>The bound local UDP port, once <see cref="StartAsync"/> has run (useful when starting on port 0).</summary>
    int? Port { get; }

    event EventHandler<ReassembledVideoFrame>? FrameReceived;

    /// <summary>Fraction of packets (by sequence-number gaps) not received, since the last <see cref="ResetLossStats"/> (or connection start). 0 until any packets arrive.</summary>
    double PacketLossRatio { get; }

    /// <summary>Starts a fresh loss-measurement window - see the implementation's doc comment for why this matters.</summary>
    void ResetLossStats();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
