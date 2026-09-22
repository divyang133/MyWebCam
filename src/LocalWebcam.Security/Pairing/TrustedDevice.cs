namespace LocalWebcam.Security.Pairing;

/// <summary>A previously paired device. <see cref="TrustKey"/> is never logged (spec section 24).</summary>
public sealed record TrustedDevice(string DeviceId, string DisplayName, DateTimeOffset PairedAtUtc, byte[] TrustKey);
