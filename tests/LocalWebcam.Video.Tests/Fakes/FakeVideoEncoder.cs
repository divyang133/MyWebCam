using LocalWebcam.Shared.Video;
using LocalWebcam.Video;

namespace LocalWebcam.Video.Tests.Fakes;

internal sealed class FakeVideoEncoder : IVideoEncoder
{
    public static readonly object InputSurfaceSentinel = new();

    public bool ThrowOnStart { get; set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public bool IsEncoding { get; private set; }

    public event EventHandler<EncodedVideoFrame>? FrameEncoded;

    public event EventHandler<CameraErrorEventArgs>? Error;

    public void RaiseError(string message) => Error?.Invoke(this, new CameraErrorEventArgs(message));

    public void RaiseFrame(EncodedVideoFrame frame) => FrameEncoded?.Invoke(this, frame);

    public object CreateInputSurface(VideoStreamConfig config) => InputSurfaceSentinel;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("Simulated encoder start failure.");
        }

        StartCallCount++;
        IsEncoding = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCallCount++;
        IsEncoding = false;
        return Task.CompletedTask;
    }

    public Task RequestKeyFrameAsync() => Task.CompletedTask;

    public int? LastBitrateBps { get; private set; }

    public Task SetBitrateAsync(int bitrateBps)
    {
        LastBitrateBps = bitrateBps;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
