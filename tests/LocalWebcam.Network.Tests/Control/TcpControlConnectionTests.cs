using System.Net;
using LocalWebcam.Network.Control;
using LocalWebcam.Protocol.Control;
using LocalWebcam.Security.Pairing;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWebcam.Network.Tests.Control;

public class TcpControlConnectionTests
{
    [Fact]
    public async Task ClientAndListener_ExchangeHelloOverRealLoopbackSocket()
    {
        await using var listener = new TcpControlListener(NullLoggerFactory.Instance);
        var acceptedTcs = new TaskCompletionSource<IControlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionAccepted += (_, connection) => acceptedTcs.TrySetResult(connection);
        listener.Start(port: 0);

        await using var client = await TcpControlConnection.ConnectAsync(
            IPAddress.Loopback, listener.Port!.Value, NullLogger<TcpControlConnection>.Instance);

        await using var server = await acceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var serverReceivedHello = new TaskCompletionSource<HelloMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, e) =>
        {
            if (e.Type == ControlMessageType.Hello)
            {
                serverReceivedHello.TrySetResult(ControlMessageSerializer.Deserialize<HelloMessage>(e.Payload));
            }
        };
        server.StartReceiving();
        client.StartReceiving();

        var hello = new HelloMessage(ProtocolVersion: 1, DeviceId: "phone-123", DisplayName: "Pixel 9");
        await client.SendAsync(ControlMessageType.Hello, hello);

        var received = await serverReceivedHello.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(hello, received);
    }

    [Fact]
    public async Task Disconnected_FiresWhenPeerCloses()
    {
        await using var listener = new TcpControlListener(NullLoggerFactory.Instance);
        var acceptedTcs = new TaskCompletionSource<IControlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionAccepted += (_, connection) => acceptedTcs.TrySetResult(connection);
        listener.Start(port: 0);

        var client = await TcpControlConnection.ConnectAsync(
            IPAddress.Loopback, listener.Port!.Value, NullLogger<TcpControlConnection>.Instance);
        await using var server = await acceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var serverDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Disconnected += (_, _) => serverDisconnected.TrySetResult();
        server.StartReceiving();
        client.StartReceiving();

        await client.CloseAsync();

        await serverDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FullPairingHandshake_OverRealSocket_BothSidesDeriveMatchingTrustKey()
    {
        await using var listener = new TcpControlListener(NullLoggerFactory.Instance);
        var acceptedTcs = new TaskCompletionSource<IControlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionAccepted += (_, connection) => acceptedTcs.TrySetResult(connection);
        listener.Start(port: 0);

        await using var desktop = await TcpControlConnection.ConnectAsync(
            IPAddress.Loopback, listener.Port!.Value, NullLogger<TcpControlConnection>.Instance);
        await using var phone = await acceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var desktopSession = new PairingSession();
        using var phoneSession = new PairingSession();

        var phoneReceivedRequest = new TaskCompletionSource<PairRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        phone.MessageReceived += (_, e) =>
        {
            if (e.Type == ControlMessageType.PairRequest)
            {
                phoneReceivedRequest.TrySetResult(ControlMessageSerializer.Deserialize<PairRequestMessage>(e.Payload));
            }
        };

        var desktopReceivedChallenge = new TaskCompletionSource<PairChallengeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.MessageReceived += (_, e) =>
        {
            if (e.Type == ControlMessageType.PairChallenge)
            {
                desktopReceivedChallenge.TrySetResult(ControlMessageSerializer.Deserialize<PairChallengeMessage>(e.Payload));
            }
        };
        phone.StartReceiving();
        desktop.StartReceiving();

        // Desktop initiates with its ephemeral public key.
        await desktop.SendAsync(ControlMessageType.PairRequest, new PairRequestMessage(desktopSession.LocalPublicKey));
        var request = await phoneReceivedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Phone completes its side of the exchange and replies with its own public key.
        phoneSession.CompleteKeyExchange(request.PublicKey);
        await phone.SendAsync(ControlMessageType.PairChallenge, new PairChallengeMessage(phoneSession.LocalPublicKey));
        var challenge = await desktopReceivedChallenge.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Desktop completes its side.
        desktopSession.CompleteKeyExchange(challenge.PublicKey);

        var desktopCode = desktopSession.ComputeVerificationCode(desktopSession.LocalPublicKey, phoneSession.LocalPublicKey);
        var phoneCode = phoneSession.ComputeVerificationCode(phoneSession.LocalPublicKey, desktopSession.LocalPublicKey);
        Assert.Equal(desktopCode, phoneCode);

        var desktopTrustKey = desktopSession.DeriveTrustKey();
        var phoneTrustKey = phoneSession.DeriveTrustKey();
        Assert.Equal(desktopTrustKey, phoneTrustKey);
    }
}
