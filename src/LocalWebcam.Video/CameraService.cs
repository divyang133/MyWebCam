using LocalWebcam.Shared.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Video;

/// <summary>
/// Default, platform-agnostic <see cref="ICameraService"/>: owns the
/// combined lifecycle of an <see cref="ICameraController"/> and an
/// <see cref="IVideoEncoder"/>. A fresh encoder is created per
/// <see cref="StartAsync"/> (from <paramref name="encoderFactory"/>) since
/// hardware encoders are typically not reconfigurable after
/// <c>Stop</c>/<c>Release</c>; the camera controller is reused and handles
/// camera switching internally without disturbing the encoder.
/// </summary>
public sealed class CameraService(
    ICameraController cameraController,
    Func<IVideoEncoder> encoderFactory,
    ILogger<CameraService> logger) : ICameraService
{
    private IVideoEncoder? _encoder;
    private StreamingState _state = StreamingState.Idle;

    public StreamingState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public CameraDescriptor? CurrentCamera => cameraController.CurrentCamera;

    public event EventHandler<StreamingState>? StateChanged;

    public event EventHandler<EncodedVideoFrame>? FrameEncoded;

    public event EventHandler<CameraErrorEventArgs>? Error;

    public async Task StartAsync(CameraFacing facing, VideoStreamConfig config, object? previewTarget, CancellationToken cancellationToken = default)
    {
        if (State is StreamingState.Starting or StreamingState.Streaming)
        {
            throw new InvalidOperationException($"Cannot start streaming while in state {State}.");
        }

        State = StreamingState.Starting;

        try
        {
            var encoder = encoderFactory();
            encoder.FrameEncoded += OnEncoderFrameEncoded;
            encoder.Error += OnComponentError;
            _encoder = encoder;

            cameraController.Error += OnComponentError;

            await cameraController.OpenAsync(facing, cancellationToken).ConfigureAwait(false);

            // Reconcile with what the camera can actually sustain *before*
            // configuring the encoder: asking the encoder for a frame rate
            // the camera can't feed doesn't fail, it just makes the encoder
            // sample the shared surface faster than the camera updates it,
            // producing an uneven duplicate-frame cadence - confirmed on
            // real hardware as visible judder at a requested 1080p60 when
            // the camera could only actually do 1080p30. Encoding at the
            // camera's real rate instead gives genuinely smooth video
            // rather than a mismatched-but-technically-60fps stream.
            var achievableFrameRate = cameraController.GetAchievableFrameRate(config.FrameRate);
            if (achievableFrameRate < config.FrameRate)
            {
                logger.LogWarning(
                    "Camera cannot sustain {Requested} fps at this resolution; encoding at {Achievable} fps instead",
                    config.FrameRate, achievableFrameRate);
                config = config with { FrameRate = achievableFrameRate };
            }

            var encoderSurface = encoder.CreateInputSurface(config);
            var outputTargets = previewTarget is null
                ? new[] { encoderSurface }
                : new[] { previewTarget, encoderSurface };

            await cameraController.StartCaptureAsync(config, outputTargets, cancellationToken).ConfigureAwait(false);
            await encoder.StartAsync(cancellationToken).ConfigureAwait(false);

            State = StreamingState.Streaming;
            logger.LogInformation("Camera service started: {Facing} at {Resolution}@{FrameRate}", facing, config.Resolution, config.FrameRate);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start camera service");
            State = StreamingState.Error;
            Error?.Invoke(this, new CameraErrorEventArgs("Failed to start the camera/encoder pipeline.", ex));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State is StreamingState.Idle or StreamingState.Stopping)
        {
            return;
        }

        State = StreamingState.Stopping;

        try
        {
            if (_encoder is { } encoder)
            {
                await encoder.StopAsync(cancellationToken).ConfigureAwait(false);
                encoder.FrameEncoded -= OnEncoderFrameEncoded;
                encoder.Error -= OnComponentError;
                await encoder.DisposeAsync().ConfigureAwait(false);
                _encoder = null;
            }

            await cameraController.StopCaptureAsync(cancellationToken).ConfigureAwait(false);
            await cameraController.CloseAsync().ConfigureAwait(false);
            cameraController.Error -= OnComponentError;
        }
        finally
        {
            State = StreamingState.Idle;
        }
    }

    public Task SwitchCameraAsync(CancellationToken cancellationToken = default) =>
        cameraController.SwitchCameraAsync(cancellationToken);

    public Task SetTorchAsync(bool enabled, CancellationToken cancellationToken = default) =>
        cameraController.SetTorchAsync(enabled, cancellationToken);

    public Task SetBitrateAsync(int bitrateBps) => _encoder?.SetBitrateAsync(bitrateBps) ?? Task.CompletedTask;

    private void OnEncoderFrameEncoded(object? sender, EncodedVideoFrame frame) => FrameEncoded?.Invoke(this, frame);

    private void OnComponentError(object? sender, CameraErrorEventArgs e)
    {
        logger.LogWarning(e.Exception, "Camera pipeline error: {Message}", e.Message);
        State = StreamingState.Error;
        Error?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await cameraController.DisposeAsync().ConfigureAwait(false);
    }
}
