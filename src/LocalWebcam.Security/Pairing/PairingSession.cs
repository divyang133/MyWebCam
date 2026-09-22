using System.Security.Cryptography;
using System.Text;

namespace LocalWebcam.Security.Pairing;

/// <summary>
/// One ECDH (P-256) key exchange between this device and a peer, used to
/// derive (1) a short numeric code both screens display for the user to
/// visually compare (spec section 7 — a MITM ends up with a different
/// shared secret per side, so the codes would mismatch), and (2) a
/// long-lived trust key stored for future reconnection without repeating
/// the human-visible pairing step.
///
/// A fresh <see cref="PairingSession"/> is used per pairing attempt
/// (ephemeral keys, forward secrecy); the derived trust key is what
/// persists, not this session.
/// </summary>
public sealed class PairingSession : IDisposable
{
    private const string VerificationCodeInfoPrefix = "localwebcam-pairing-code-v1";
    private const string TrustKeyInfoPrefix = "localwebcam-trust-key-v1";

    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private byte[]? _sharedSecret;

    /// <summary>This device's ephemeral public key (SubjectPublicKeyInfo), to send to the peer.</summary>
    public byte[] LocalPublicKey => _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

    public bool IsComplete => _sharedSecret is not null;

    /// <summary>Derives the shared secret from the peer's public key. Call once, after receiving it.</summary>
    public void CompleteKeyExchange(byte[] peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        _sharedSecret = _ecdh.DeriveKeyMaterial(peer.PublicKey);
    }

    /// <summary>
    /// A 6-digit code, identical on both sides for a genuine (non-MITM'd)
    /// exchange. <paramref name="localPublicKey"/>/<paramref name="peerPublicKey"/>
    /// are the same two keys exchanged for <see cref="CompleteKeyExchange"/>,
    /// in either order — they're canonically sorted internally so both
    /// participants compute the same result.
    /// </summary>
    public string ComputeVerificationCode(byte[] localPublicKey, byte[] peerPublicKey)
    {
        EnsureComplete();

        var (first, second) = OrderKeys(localPublicKey, peerPublicKey);
        var info = Encoding.UTF8.GetBytes(VerificationCodeInfoPrefix).Concat(first).Concat(second).ToArray();
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, _sharedSecret!, outputLength: 4, info: info);

        var code = BitConverter.ToUInt32(derived, 0) % 1_000_000;
        return code.ToString("D6");
    }

    /// <summary>
    /// Derives the long-lived symmetric trust key both sides store (each
    /// keyed by the <em>other</em> party's DeviceId in their own trust
    /// store). Deliberately takes no device-id parameter: the shared secret
    /// this is derived from is already specific to this one pairing exchange
    /// (baked in via the peer's public key in <see cref="CompleteKeyExchange"/>),
    /// and a fresh <see cref="PairingSession"/> is used per pairing attempt —
    /// so there is nothing further to bind, and binding one side's device id
    /// would make the two sides derive different keys.
    /// </summary>
    public byte[] DeriveTrustKey()
    {
        EnsureComplete();

        var info = Encoding.UTF8.GetBytes(TrustKeyInfoPrefix);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _sharedSecret!, outputLength: 32, info: info);
    }

    private void EnsureComplete()
    {
        if (_sharedSecret is null)
        {
            throw new InvalidOperationException($"Call {nameof(CompleteKeyExchange)} first.");
        }
    }

    private static (byte[] First, byte[] Second) OrderKeys(byte[] a, byte[] b) =>
        CompareBytes(a, b) <= 0 ? (a, b) : (b, a);

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var diff = a[i] - b[i];
            if (diff != 0)
            {
                return diff;
            }
        }

        return a.Length - b.Length;
    }

    public void Dispose() => _ecdh.Dispose();
}
