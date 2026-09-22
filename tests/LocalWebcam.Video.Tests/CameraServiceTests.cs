using LocalWebcam.Shared.Video;
using LocalWebcam.Video.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWebcam.Video.Tests;

public class CameraServiceTests
{
    private static (CameraService Service, FakeCameraController Camera, FakeVideoEncoder Encoder) CreateSut()
    {
        var camera = new FakeCameraController();
        var encoder = new FakeVideoEncoder();
        var service = new CameraService(camera, () => encoder, NullLogger<CameraService>.Instance);
        return (service, camera, encoder);
    }

    [Fact]
    public async Task StartAsync_OpensCameraAndStartsEncoder()
    {
        var (service, camera, encoder) = CreateSut();

        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        Assert.Equal(StreamingState.Streaming, service.State);
        Assert.Equal(1, camera.StartCaptureCallCount);
        Assert.Equal(1, encoder.StartCallCount);
        Assert.Equal(CameraFacing.Rear, camera.CurrentCamera!.Facing);
    }

    [Fact]
    public async Task StartAsync_WithPreviewTarget_PassesBothPreviewAndEncoderSurfaceAsOutputTargets()
    {
        var (service, camera, _) = CreateSut();
        var previewTarget = new object();

        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget);

        Assert.Equal(2, camera.LastOutputTargets!.Count);
        Assert.Contains(previewTarget, camera.LastOutputTargets);
        Assert.Contains(FakeVideoEncoder.InputSurfaceSentinel, camera.LastOutputTargets);
    }

    [Fact]
    public async Task StartAsync_WithoutPreviewTarget_PassesOnlyEncoderSurface()
    {
        var (service, camera, _) = CreateSut();

        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        Assert.Single(camera.LastOutputTargets!);
    }

    [Fact]
    public async Task StartAsync_WhenCameraOpenFails_TransitionsToErrorAndRethrows()
    {
        var (service, camera, _) = CreateSut();
        camera.ThrowOnOpen = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null));

        Assert.Equal(StreamingState.Error, service.State);
    }

    [Fact]
    public async Task StartAsync_WhileAlreadyStreaming_Throws()
    {
        var (service, _, _) = CreateSut();
        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null));
    }

    [Fact]
    public async Task StopAsync_StopsEncoderAndCameraAndReturnsToIdle()
    {
        var (service, camera, encoder) = CreateSut();
        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        await service.StopAsync();

        Assert.Equal(StreamingState.Idle, service.State);
        Assert.Equal(1, encoder.StopCallCount);
        Assert.False(camera.IsCapturing);
    }

    [Fact]
    public async Task StopAsync_WhenIdle_IsANoOp()
    {
        var (service, _, encoder) = CreateSut();

        await service.StopAsync();

        Assert.Equal(StreamingState.Idle, service.State);
        Assert.Equal(0, encoder.StopCallCount);
    }

    [Fact]
    public async Task FrameEncoded_FromEncoder_IsForwardedByService()
    {
        var (service, _, encoder) = CreateSut();
        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        EncodedVideoFrame? received = null;
        service.FrameEncoded += (_, frame) => received = frame;

        var expected = new EncodedVideoFrame([1, 2, 3], PresentationTimeUs: 1000, IsKeyFrame: true);
        encoder.RaiseFrame(expected);

        Assert.Same(expected, received);
    }

    [Fact]
    public async Task ComponentError_TransitionsStateToErrorAndRaisesEvent()
    {
        var (service, camera, _) = CreateSut();
        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        CameraErrorEventArgs? received = null;
        service.Error += (_, e) => received = e;

        camera.RaiseError("simulated disconnect");

        Assert.Equal(StreamingState.Error, service.State);
        Assert.Equal("simulated disconnect", received!.Message);
    }

    [Fact]
    public async Task SwitchCameraAsync_DelegatesToCameraController()
    {
        var (service, camera, _) = CreateSut();
        await service.StartAsync(CameraFacing.Rear, VideoStreamConfig.Default720p30, previewTarget: null);

        await service.SwitchCameraAsync();

        Assert.Equal(1, camera.SwitchCameraCallCount);
        Assert.Equal(CameraFacing.Front, camera.CurrentCamera!.Facing);
    }
}
