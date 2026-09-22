using System.Text.Json;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using LocalWebcam.Security.Pairing;

namespace LocalWebcam.Android.Security;

/// <summary>
/// Persists trusted devices as a single JSON document, encrypted with
/// AES-256-GCM using a non-exportable key generated inside the Android
/// Keystore (spec section 22) — never plaintext.
///
/// This uses the Keystore/Cipher APIs directly rather than
/// <c>androidx.security.crypto</c>'s <c>EncryptedSharedPreferences</c>/
/// <c>MasterKey</c>, which the platform marks obsolete (Google no longer
/// recommends that wrapper); the underlying Keystore APIs used here are not
/// deprecated.
/// </summary>
public sealed class AndroidTrustedDeviceStore : ITrustedDeviceStore
{
    private const string KeystoreProviderName = "AndroidKeyStore";
    private const string KeyAlias = "localwebcam_trust_master_key";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmIvLength = 12;
    private const int GcmTagLengthBits = 128;

    private readonly Context _context;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath;

    public AndroidTrustedDeviceStore(Context context)
    {
        _context = context;
        _filePath = Path.Combine(context.FilesDir!.AbsolutePath, "trusted-devices.enc");
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

        var stored = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
        if (stored.Length <= GcmIvLength)
        {
            return [];
        }

        var iv = stored[..GcmIvLength];
        var ciphertext = stored[GcmIvLength..];

        byte[] json;
        try
        {
            json = Decrypt(GetOrCreateKey(), iv, ciphertext);
        }
        catch (Exception)
        {
            // Corrupt file, or the Keystore key is gone (e.g. app data was
            // partially restored to a different device). Trust storage that
            // can't be decrypted is equivalent to none - pairing again is
            // the correct recovery, not a crash.
            return [];
        }

        return JsonSerializer.Deserialize<List<TrustedDevice>>(json) ?? [];
    }

    private async Task SaveAllUnlockedAsync(List<TrustedDevice> devices, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(devices);
        var (iv, ciphertext) = Encrypt(GetOrCreateKey(), json);

        var combined = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(combined, 0);
        ciphertext.CopyTo(combined, iv.Length);

        await File.WriteAllBytesAsync(_filePath, combined, cancellationToken).ConfigureAwait(false);
    }

    private IKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance(KeystoreProviderName)!;
        keyStore.Load(null, null);

        if (!keyStore.ContainsAlias(KeyAlias))
        {
            var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!, KeystoreProviderName)!;
            var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes([KeyProperties.BlockModeGcm!])
                .SetEncryptionPaddings([KeyProperties.EncryptionPaddingNone!])
                .SetKeySize(256)
                .Build();
            keyGenerator.Init(spec);
            keyGenerator.GenerateKey();
        }

        return keyStore.GetKey(KeyAlias, null)!;
    }

    private static (byte[] Iv, byte[] Ciphertext) Encrypt(IKey key, byte[] plaintext)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, key);
        var ciphertext = cipher.DoFinal(plaintext)!;
        return (cipher.GetIV()!, ciphertext);
    }

    private static byte[] Decrypt(IKey key, byte[] iv, byte[] ciphertext)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(GcmTagLengthBits, iv));
        return cipher.DoFinal(ciphertext)!;
    }
}
