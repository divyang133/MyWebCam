namespace LocalWebcam.Security.Pairing;

/// <summary>
/// Persists trusted devices using the OS's secure storage (DPAPI on Windows,
/// Android Keystore-backed EncryptedSharedPreferences on Android) — never
/// plaintext (spec section 22).
/// </summary>
public interface ITrustedDeviceStore
{
    Task<TrustedDevice?> FindAsync(string deviceId, CancellationToken cancellationToken = default);

    Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken = default);

    Task RemoveAsync(string deviceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrustedDevice>> GetAllAsync(CancellationToken cancellationToken = default);
}
