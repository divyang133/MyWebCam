using LocalWebcam.Protocol.Control;
using LocalWebcam.Shared.Video;

namespace LocalWebcam.Protocol.Tests.Control;

public class ControlMessageSerializerTests
{
    [Fact]
    public void HelloMessage_RoundTrips()
    {
        var original = new HelloMessage(ProtocolVersion: 1, DeviceId: "phone-123", DisplayName: "Pixel 9");

        var bytes = ControlMessageSerializer.Serialize(original);
        var result = ControlMessageSerializer.Deserialize<HelloMessage>(bytes);

        Assert.Equal(original, result);
    }

    [Fact]
    public void PairRequestMessage_WithBinaryPublicKey_RoundTrips()
    {
        var original = new PairRequestMessage(PublicKey: [1, 2, 3, 4, 5, 255, 0, 128]);

        var bytes = ControlMessageSerializer.Serialize(original);
        var result = ControlMessageSerializer.Deserialize<PairRequestMessage>(bytes);

        Assert.Equal(original.PublicKey, result.PublicKey);
    }

    [Fact]
    public void ConfigureStreamMessage_WithNestedRecord_RoundTrips()
    {
        var original = new ConfigureStreamMessage(VideoStreamConfig.Default1080p30, VideoPort: 5000);

        var bytes = ControlMessageSerializer.Serialize(original);
        var result = ControlMessageSerializer.Deserialize<ConfigureStreamMessage>(bytes);

        Assert.Equal(original.Config, result.Config);
    }

    [Fact]
    public void Deserialize_OfMalformedJson_Throws()
    {
        var garbage = "not valid json at all"u8.ToArray();

        Assert.ThrowsAny<Exception>(() => ControlMessageSerializer.Deserialize<PairRequestMessage>(garbage));
    }
}
