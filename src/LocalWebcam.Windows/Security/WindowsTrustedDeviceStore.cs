using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using LocalWebcam.Security.Pairing;

namespace LocalWebcam.Windows.Security;

/// <summary>
/// Persists trusted devices as a single JSON document encrypted with DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>)
/// under <c>%LOCALAPPDATA%\LocalWebcam</c>. DPAPI ties the ciphertext to the
/// Windows user account, so it's meaningless if copied elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrustedDeviceStore : ITrustedDeviceStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WindowsTrustedDeviceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalWebcam",
            "trusted-devices.dat");
    }

    public async Task<TrustedDevice?> FindAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(d => d.DeviceId == deviceId);
    }

    public async Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllUnlockedAsync(cancellationToken).ConfigureAwait(false);
            all.RemoveAll(d => d.DeviceId == device.DeviceId);
            all.Add(device);
            await SaveAllUnlockedAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (all.RemoveAll(d => d.DeviceId == deviceId) > 0)
            {
                await SaveAllUnlockedAsync(all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TrustedDevice>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAllUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<TrustedDevice>> LoadAllUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);

        byte[] json;
        try
        {
            json = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Corrupt, or written by a different Windows user profile. Trust
            // storage that can't be read is equivalent to no trust storage,
            // not a fatal error - pairing will simply be required again.
            return [];
        }

        return JsonSerializer.Deserialize<List<TrustedDevice>>(json) ?? [];
    }

    private async Task SaveAllUnlockedAsync(List<TrustedDevice> devices, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(devices);
        var encrypted = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_filePath, encrypted, cancellationToken).ConfigureAwait(false);
    }
}
