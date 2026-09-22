using LocalWebcam.Shared.Video;
using LocalWebcam.Video;

namespace LocalWebcam.Video.Tests.Fakes;

internal sealed class FakeCameraController : ICameraController
{
    private static readonly CameraDescriptor Rear = new("0", CameraFacing.Rear, [VideoResolution.Hd720], HasFlash: true);
    private static readonly CameraDescriptor Front = new("1", CameraFacing.Front, [VideoResolution.Hd720], HasFlash: false);

    public bool ThrowOnOpen { get; set; }

    public int StartCaptureCallCount { get; private set; }

    public int SwitchCameraCallCount { get; private set; }

    public IReadOnlyList<object>? LastOutputTargets { get; private set; }

    public IReadOnlyList<CameraDescriptor> AvailableCameras { get; } = [Rear, Front];

    public CameraDescriptor? CurrentCamera { get; private set; }

    public bool IsCapturing { get; private set; }

    public event EventHandler<CameraErrorEventArgs>? Error;

    public void RaiseError(string message) => Error?.Invoke(this, new CameraErrorEventArgs(message));

    public Task OpenAsync(CameraFacing preferredFacing, CancellationToken cancellationToken = default)
    {
        if (ThrowOnOpen)
        {
            throw new InvalidOperationException("Simulated open failure.");
        }

        CurrentCamera = AvailableCameras.First(c => c.Facing == preferredFacing);
        return Task.CompletedTask;
    }

    public Task StartCaptureAsync(VideoStreamConfig config, IReadOnlyList<object> outputTargets, CancellationToken cancellationToken = default)
    {
        StartCaptureCallCount++;
        LastOutputTargets = outputTargets;
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public Task SwitchCameraAsync(CancellationToken cancellationToken = default)
    {
        SwitchCameraCallCount++;
        CurrentCamera = AvailableCameras.First(c => c.Facing != CurrentCamera!.Facing);
        return Task.CompletedTask;
    }

    public Task SetTorchAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CloseAsync()
    {
        CurrentCamera = null;
        return Task.CompletedTask;
    }

    public int GetAchievableFrameRate(int requestedFrameRate) => requestedFrameRate;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
