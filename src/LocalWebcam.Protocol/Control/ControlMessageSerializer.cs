using System.Text.Json;

namespace LocalWebcam.Protocol.Control;

/// <summary>JSON (de)serialization for control message payloads (spec section 8: fine for infrequent messages, unlike per-frame video).</summary>
public static class ControlMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<T>(payload, Options)
            ?? throw new InvalidDataException($"Control message payload deserialized to null for {typeof(T).Name}.");
}
