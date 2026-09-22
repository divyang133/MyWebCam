using LocalWebcam.Protocol.Control;

namespace LocalWebcam.Network.Control;

public sealed class ControlMessageReceivedEventArgs(ControlMessageType type, byte[] payload) : EventArgs
{
    public ControlMessageType Type { get; } = type;

    public byte[] Payload { get; } = payload;
}
