using LocalWebcam.Security.Pairing;

namespace LocalWebcam.Network.Tests.Pairing;

public class PairingSessionTests
{
    [Fact]
    public void BothSides_ComputeIdenticalVerificationCode()
    {
        using var desktop = new PairingSession();
        using var phone = new PairingSession();

        desktop.CompleteKeyExchange(phone.LocalPublicKey);
        phone.CompleteKeyExchange(desktop.LocalPublicKey);

        var desktopCode = desktop.ComputeVerificationCode(desktop.LocalPublicKey, phone.LocalPublicKey);
        var phoneCode = phone.ComputeVerificationCode(phone.LocalPublicKey, desktop.LocalPublicKey);

        Assert.Equal(desktopCode, phoneCode);
        Assert.Equal(6, desktopCode.Length);
        Assert.True(int.TryParse(desktopCode, out _));
    }

    [Fact]
    public void BothSides_DeriveIdenticalTrustKey()
    {
        using var desktop = new PairingSession();
        using var phone = new PairingSession();

        desktop.CompleteKeyExchange(phone.LocalPublicKey);
        phone.CompleteKeyExchange(desktop.LocalPublicKey);

        var desktopKey = desktop.DeriveTrustKey();
        var phoneKey = phone.DeriveTrustKey();

        Assert.Equal(desktopKey, phoneKey);
        Assert.Equal(32, desktopKey.Length);
    }

    [Fact]
    public void DifferentPairingSessions_ProduceDifferentTrustKeys()
    {
        // Each pairing attempt uses a fresh session/shared secret, so two
        // independent pairings (even between the same two logical devices)
        // must not derive the same trust key.
        using var desktopA = new PairingSession();
        using var phoneA = new PairingSession();
        desktopA.CompleteKeyExchange(phoneA.LocalPublicKey);

        using var desktopB = new PairingSession();
        using var phoneB = new PairingSession();
        desktopB.CompleteKeyExchange(phoneB.LocalPublicKey);

        Assert.NotEqual(desktopA.DeriveTrustKey(), desktopB.DeriveTrustKey());
    }

    [Fact]
    public void MitmAttempt_ProducesMismatchedVerificationCodes()
    {
        // Simulates an active MITM: the attacker completes a *separate* key
        // exchange with each side, so each side's shared secret differs from
        // what the other side computes - the whole point of the visual
        // numeric-comparison step (spec section 7).
        using var desktop = new PairingSession();
        using var phone = new PairingSession();
        using var attackerToDesktop = new PairingSession();
        using var attackerToPhone = new PairingSession();

        desktop.CompleteKeyExchange(attackerToDesktop.LocalPublicKey);
        attackerToDesktop.CompleteKeyExchange(desktop.LocalPublicKey);

        phone.CompleteKeyExchange(attackerToPhone.LocalPublicKey);
        attackerToPhone.CompleteKeyExchange(phone.LocalPublicKey);

        var desktopCode = desktop.ComputeVerificationCode(desktop.LocalPublicKey, attackerToDesktop.LocalPublicKey);
        var phoneCode = phone.ComputeVerificationCode(phone.LocalPublicKey, attackerToPhone.LocalPublicKey);

        Assert.NotEqual(desktopCode, phoneCode);
    }

    [Fact]
    public void ComputeVerificationCode_BeforeKeyExchange_Throws()
    {
        using var session = new PairingSession();

        Assert.Throws<InvalidOperationException>(() =>
            session.ComputeVerificationCode(session.LocalPublicKey, session.LocalPublicKey));
    }

    [Fact]
    public void DeriveTrustKey_BeforeKeyExchange_Throws()
    {
        using var session = new PairingSession();

        Assert.Throws<InvalidOperationException>(() => session.DeriveTrustKey());
    }
}
