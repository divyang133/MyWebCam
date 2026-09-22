using System.Net;
using System.Net.Sockets;
using LocalWebcam.Network.Video;
using LocalWebcam.Protocol.Video;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWebcam.Network.Tests.Video;

public class UdpVideoTransportTests
{
    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task SenderAndReceiver_ReassembleAMultiFragmentFrameOverLoopback()
    {
        var port = GetFreeUdpPort();
        await using var receiver = new UdpVideoReceiver(port, NullLogger<UdpVideoReceiver>.Instance);
        await using var sender = new UdpVideoSender(new IPEndPoint(IPAddress.Loopback, port));

        var received = new TaskCompletionSource<ReassembledVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += (_, frame) => received.TrySetResult(frame);

        await receiver.StartAsync();

        // Bigger than one MaxFragmentPayloadSize (1400) so this exercises real fragmentation.
        var frameData = new byte[5000];
        Random.Shared.NextBytes(frameData);

        await sender.SendFrameAsync(frameData, timestampMicros: 123456, isKeyFrame: true);

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(frameData, result.Data);
        Assert.Equal(123456u, result.TimestampMicros);
        Assert.True(result.IsKeyFrame);
    }

    [Fact]
    public async Task SenderAndReceiver_DeliverMultipleFramesInOrder()
    {
        var port = GetFreeUdpPort();
        await using var receiver = new UdpVideoReceiver(port, NullLogger<UdpVideoReceiver>.Instance);
        await using var sender = new UdpVideoSender(new IPEndPoint(IPAddress.Loopback, port));

        var receivedFrames = new List<ReassembledVideoFrame>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += (_, frame) =>
        {
            receivedFrames.Add(frame);
            if (receivedFrames.Count == 3)
            {
                allReceived.TrySetResult();
            }
        };

        await receiver.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            await sender.SendFrameAsync([(byte)i], timestampMicros: (ulong)i, isKeyFrame: i == 0);
        }

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0u, 1u, 2u], receivedFrames.Select(f => f.FrameId));
        Assert.True(receiver.PacketLossRatio is >= 0 and <= 1);
    }

    [Fact]
    public async Task Receiver_DropsIncompleteFrame_WhenANewerFrameArrives()
    {
        var port = GetFreeUdpPort();
        await using var receiver = new UdpVideoReceiver(port, NullLogger<UdpVideoReceiver>.Instance);
        using var rawSender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);

        var receivedFrameIds = new List<uint>();
        var frame2Received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += (_, frame) =>
        {
            receivedFrameIds.Add(frame.FrameId);
            if (frame.FrameId == 2)
            {
                frame2Received.TrySetResult();
            }
        };

        await receiver.StartAsync();

        // Frame 1: only fragment 0 of 2 ever arrives - stays incomplete forever.
        var frame1Fragment0 = VideoPacketCodec.Encode(frameId: 1, fragIndex: 0, fragCount: 2, timestampMicros: 0, sequenceNumber: 0, isKeyFrame: false, isParity: false, totalLength: 2, [1]);
        await rawSender.SendAsync(frame1Fragment0, target);
        await Task.Delay(100); // ensure frame 1 is registered as pending before frame 2 arrives

        // Frame 2: complete, single fragment - its arrival should evict frame 1.
        var frame2Fragment0 = VideoPacketCodec.Encode(frameId: 2, fragIndex: 0, fragCount: 1, timestampMicros: 0, sequenceNumber: 1, isKeyFrame: true, isParity: false, totalLength: 1, [2]);
        await rawSender.SendAsync(frame2Fragment0, target);

        await frame2Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(1u, receivedFrameIds);
        Assert.Contains(2u, receivedFrameIds);
    }

    [Fact]
    public async Task Receiver_RecoversOneMissingFragment_ViaParity()
    {
        var port = GetFreeUdpPort();
        await using var receiver = new UdpVideoReceiver(port, NullLogger<UdpVideoReceiver>.Instance);
        using var rawSender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);

        var received = new TaskCompletionSource<ReassembledVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += (_, frame) => received.TrySetResult(frame);

        await receiver.StartAsync();

        // 4 fragments (UdpVideoSender's MinFragmentsForParity) with a shorter
        // last one, so dropping it (below) also exercises TotalLength-based
        // truncation of the reconstructed buffer, not just full-size fragments.
        const int fragmentSize = UdpVideoSender.MaxFragmentPayloadSize;
        var frameData = new byte[(fragmentSize * 3) + 200];
        Random.Shared.NextBytes(frameData);

        var fragments = new byte[4][];
        for (var i = 0; i < 4; i++)
        {
            var offset = i * fragmentSize;
            var length = Math.Min(fragmentSize, frameData.Length - offset);
            fragments[i] = frameData.AsSpan(offset, length).ToArray();
        }

        var parity = new byte[fragmentSize];
        foreach (var frag in fragments)
        {
            for (var i = 0; i < frag.Length; i++)
            {
                parity[i] ^= frag[i];
            }
        }

        const int droppedIndex = 3; // the last, shorter fragment - the harder recovery case
        for (ushort i = 0; i < fragments.Length; i++)
        {
            if (i == droppedIndex)
            {
                continue;
            }

            var packet = VideoPacketCodec.Encode(frameId: 1, fragIndex: i, fragCount: 4, timestampMicros: 55, sequenceNumber: i, isKeyFrame: true, isParity: false, totalLength: (uint)frameData.Length, fragments[i]);
            await rawSender.SendAsync(packet, target);
        }

        var parityPacket = VideoPacketCodec.Encode(frameId: 1, fragIndex: 0, fragCount: 4, timestampMicros: 55, sequenceNumber: 10, isKeyFrame: true, isParity: true, totalLength: (uint)frameData.Length, parity);
        await rawSender.SendAsync(parityPacket, target);

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(frameData, result.Data);
    }

    [Fact]
    public async Task Receiver_IgnoresMalformedDatagrams()
    {
        var port = GetFreeUdpPort();
        await using var receiver = new UdpVideoReceiver(port, NullLogger<UdpVideoReceiver>.Instance);
        using var rawSender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);

        var frameReceived = false;
        receiver.FrameReceived += (_, _) => frameReceived = true;

        await receiver.StartAsync();

        await rawSender.SendAsync(new byte[] { 1, 2, 3 }, target); // too short to be a valid header
        await Task.Delay(200);

        Assert.False(frameReceived);
    }
}
