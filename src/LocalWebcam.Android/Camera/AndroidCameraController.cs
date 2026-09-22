using Android.Content;
using Android.Hardware.Camera2;
using Android.OS;
using Android.Runtime;
using Android.Views;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Android.Camera;

/// <summary>
/// Camera2-backed <see cref="ICameraController"/>. Output targets passed to
/// <see cref="StartCaptureAsync"/> must be Android <see cref="Surface"/>
/// instances (typically a preview <c>TextureView</c>'s surface and the video
/// encoder's input surface) — Camera2 renders into all of them from a single
/// repeating capture request, so the encoder never sees a CPU-side copy.
/// </summary>
public sealed class AndroidCameraController(Context context, ILogger<AndroidCameraController> logger) : ICameraController
{
    private readonly List<CameraDescriptor> _availableCameras = [];

    private CameraManager? _manager;
    private HandlerThread? _backgroundThread;
    private Handler? _backgroundHandler;

    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private CaptureRequest.Builder? _requestBuilder;
    private CameraCharacteristics? _deviceCharacteristics;

    private IReadOnlyList<Surface>? _activeOutputTargets;
    private VideoStreamConfig? _activeConfig;

    public IReadOnlyList<CameraDescriptor> AvailableCameras => _availableCameras;

    public CameraDescriptor? CurrentCamera { get; private set; }

    public bool IsCapturing => _session is not null;

    public event EventHandler<CameraErrorEventArgs>? Error;

    public Task OpenAsync(CameraFacing preferredFacing, CancellationToken cancellationToken = default)
    {
        _manager ??= (CameraManager)context.GetSystemService(Context.CameraService)!;
        EnsureBackgroundThread();
        EnumerateCameras();

        var descriptor = _availableCameras.Find(c => c.Facing == preferredFacing) ?? _availableCameras.FirstOrDefault();
        if (descriptor is null)
        {
            throw new InvalidOperationException("No cameras are available on this device.");
        }

        return OpenDeviceAsync(descriptor, cancellationToken);
    }

    public async Task StartCaptureAsync(VideoStreamConfig config, IReadOnlyList<object> outputTargets, CancellationToken cancellationToken = default)
    {
        if (_device is not { } device)
        {
            throw new InvalidOperationException($"Call {nameof(OpenAsync)} before {nameof(StartCaptureAsync)}.");
        }

        var surfaces = outputTargets.Cast<Surface>().ToList();

        CameraOrientationState.BufferWidth = config.Resolution.Width;
        CameraOrientationState.BufferHeight = config.Resolution.Height;

        var session = await CreateCaptureSessionAsync(device, surfaces, cancellationToken).ConfigureAwait(false);

        var builder = device.CreateCaptureRequest(CameraTemplate.Record)
            ?? throw new InvalidOperationException("CreateCaptureRequest returned null.");
        foreach (var surface in surfaces)
        {
            builder.AddTarget(surface);
        }

        ApplyTargetFrameRate(builder, config.FrameRate);
        ApplyExposureCompensation(builder);

        session.SetRepeatingRequest(builder.Build(), null, _backgroundHandler);

        _session = session;
        _requestBuilder = builder;
        _activeOutputTargets = surfaces;
        _activeConfig = config;
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _session?.StopRepeating();
            _session?.Close();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error stopping capture session");
        }
        finally
        {
            _session = null;
            _requestBuilder = null;
        }

        return Task.CompletedTask;
    }

    public async Task SwitchCameraAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCamera is not { } current || _activeOutputTargets is not { } targets || _activeConfig is not { } config)
        {
            throw new InvalidOperationException("Camera must be capturing before it can be switched.");
        }

        var otherFacing = current.Facing == CameraFacing.Rear ? CameraFacing.Front : CameraFacing.Rear;
        var next = _availableCameras.Find(c => c.Facing == otherFacing);
        if (next is null)
        {
            throw new InvalidOperationException($"No {otherFacing} camera is available to switch to.");
        }

        await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
        CloseDevice();

        await OpenDeviceAsync(next, cancellationToken).ConfigureAwait(false);
        await StartCaptureAsync(config, targets, cancellationToken).ConfigureAwait(false);
    }

    public Task SetTorchAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_requestBuilder is null || _session is null)
        {
            return Task.CompletedTask;
        }

        _requestBuilder.Set(CaptureRequest.FlashMode!, (int)(enabled ? global::Android.Hardware.Camera2.FlashMode.Torch : global::Android.Hardware.Camera2.FlashMode.Off));
        _session.SetRepeatingRequest(_requestBuilder.Build(), null, _backgroundHandler);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        CloseDevice();
        return Task.CompletedTask;
    }

    private void EnumerateCameras()
    {
        if (_manager is not { } manager)
        {
            return;
        }

        _availableCameras.Clear();

        foreach (var id in manager.GetCameraIdList() ?? [])
        {
            var characteristics = manager.GetCameraCharacteristics(id);
            var lensFacingValue = (Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.LensFacing);
            var facing = lensFacingValue?.IntValue() switch
            {
                (int)LensFacing.Front => CameraFacing.Front,
                (int)LensFacing.Back => CameraFacing.Rear,
                _ => (CameraFacing?)null,
            };

            if (facing is null)
            {
                continue;
            }

            var hasFlashValue = (Java.Lang.Boolean?)characteristics.Get(CameraCharacteristics.FlashInfoAvailable);

            // Exact per-camera supported sizes come from CameraCharacteristics'
            // StreamConfigurationMap; deferred until a phase that needs
            // resolutions beyond the fixed 720p/30 and 1080p/30 presets.
            _availableCameras.Add(new CameraDescriptor(
                id,
                facing.Value,
                [VideoResolution.Hd720, VideoResolution.FullHd1080],
                hasFlashValue?.BooleanValue() ?? false));
        }
    }

    private Task OpenDeviceAsync(CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (_manager is not { } manager)
        {
            throw new InvalidOperationException("Camera manager is not initialized.");
        }

        var tcs = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new DeviceStateCallback(tcs, logger, e => Error?.Invoke(this, e));

        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        manager.OpenCamera(descriptor.Id, callback, _backgroundHandler);

        _deviceCharacteristics = manager.GetCameraCharacteristics(descriptor.Id);

        return tcs.Task.ContinueWith(t =>
        {
            _device = t.Result;
            CurrentCamera = descriptor;
        }, cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// Without this, the camera silently uses its own default AE-chosen
    /// frame rate (commonly capped around 30fps) regardless of what the
    /// encoder is configured for - a 60fps request would otherwise produce
    /// a stream the encoder timestamps as 60fps but that's actually only
    /// ever fed 30 real frames a second. Picks the narrowest available
    /// range that can sustain the requested rate (avoids the camera
    /// choosing a lower rate within a wide variable range), falling back to
    /// the highest range the hardware supports if the exact rate isn't
    /// available, rather than failing the whole capture over it.
    /// </summary>
    private void ApplyTargetFrameRate(CaptureRequest.Builder builder, int frameRate)
    {
        var best = FindBestFpsRange(frameRate);
        if (best is null)
        {
            return;
        }

        if (RangeUpper(best) < frameRate)
        {
            logger.LogWarning("This camera's highest supported frame rate is {Actual} fps; {Requested} fps was requested", RangeUpper(best), frameRate);
        }

        builder.Set(CaptureRequest.ControlAeTargetFpsRange!, best);
    }

    /// <summary>
    /// Nudges auto-exposure brighter by a fixed +1 EV: reported (and
    /// confirmed by directly comparing this phone's own live preview
    /// against the desktop app's rendered frame, pixel for pixel, of the
    /// same scene at the same moment) as noticeably darker than the actual
    /// room - a controlled test of the desktop's rendering pipeline (a
    /// synthetic solid-color frame rendered back with exact pixel fidelity)
    /// ruled that side out entirely, pointing back to the camera's own
    /// auto-exposure decision here. This is the same +EV "brightness slider"
    /// most camera apps expose - a persistent offset the camera's continuous
    /// AE loop applies on top of whatever it would otherwise choose for any
    /// given scene, not a one-time fixed value, so it should generalize
    /// across lighting rather than only fixing this one specific room.
    /// Applied to the same capture request that targets both the preview
    /// and encoder surfaces, so it brightens what actually gets streamed to
    /// the desktop (and so the virtual camera Teams/Zoom/etc. see), not
    /// just this app's own on-screen preview.
    /// </summary>
    private void ApplyExposureCompensation(CaptureRequest.Builder builder)
    {
        const double TargetExposureCompensationEv = 1.0;

        if (_deviceCharacteristics?.Get(CameraCharacteristics.ControlAeCompensationRange) is not global::Android.Util.Range range
            || _deviceCharacteristics?.Get(CameraCharacteristics.ControlAeCompensationStep) is not global::Android.Util.Rational step
            || step.Numerator <= 0 || step.Denominator <= 0)
        {
            return;
        }

        var stepEv = step.Numerator / (double)step.Denominator;
        var minSteps = ((Java.Lang.Integer)range.Lower!).IntValue();
        var maxSteps = ((Java.Lang.Integer)range.Upper!).IntValue();
        var targetSteps = (int)Math.Round(TargetExposureCompensationEv / stepEv);
        var clampedSteps = Math.Clamp(targetSteps, minSteps, maxSteps);

        builder.Set(CaptureRequest.ControlAeExposureCompensation!, clampedSteps);
    }

    public int GetAchievableFrameRate(int requestedFrameRate)
    {
        var best = FindBestFpsRange(requestedFrameRate);
        return best is null ? requestedFrameRate : RangeUpper(best);
    }

    /// <summary>
    /// Picks the narrowest available AE target-FPS range that can sustain
    /// <paramref name="frameRate"/> (avoids the camera choosing a lower rate
    /// within a wide variable range), falling back to the highest range the
    /// hardware supports if the exact rate isn't available. Returns null if
    /// this camera doesn't report any ranges (rather than failing outright).
    /// </summary>
    private global::Android.Util.Range? FindBestFpsRange(int frameRate)
    {
        // CameraCharacteristics.Get() always returns the erased Java.Lang.Object,
        // even for an array-typed key - casting that directly to Range[] throws
        // InvalidCastException (confirmed on real hardware). The array must be
        // re-wrapped from its JNI handle as a JavaArray<T> instead. Range.Lower/
        // Upper are themselves Java.Lang.Object (Java generics are erased too)
        // and need unboxing via Java.Lang.Integer.IntValue().
        var rangesValue = _deviceCharacteristics?.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges);
        if (rangesValue is null)
        {
            return null;
        }

        using var rangesArray = JavaArray<global::Android.Util.Range>.FromJniHandle(rangesValue.Handle, JniHandleOwnership.DoNotTransfer)!;
        if (rangesArray.Count == 0)
        {
            return null;
        }

        var ranges = rangesArray.ToArray();

        return ranges
            .Where(r => RangeUpper(r) >= frameRate)
            .OrderBy(r => RangeUpper(r) - RangeLower(r))
            .ThenBy(RangeUpper)
            .FirstOrDefault()
            ?? ranges.OrderByDescending(RangeUpper).First();
    }

    private static int RangeUpper(global::Android.Util.Range r) => ((Java.Lang.Integer)r.Upper!).IntValue();
    private static int RangeLower(global::Android.Util.Range r) => ((Java.Lang.Integer)r.Lower!).IntValue();

    private Task<CameraCaptureSession> CreateCaptureSessionAsync(CameraDevice device, IReadOnlyList<Surface> surfaces, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CameraCaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new SessionStateCallback(tcs, logger);

        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

#pragma warning disable CS0618, CA1422 // CreateCaptureSession(IList<Surface>, ...) is deprecated in favor of SessionConfiguration (API 28+); revisit when raising targetSdk.
        device.CreateCaptureSession(surfaces.ToList(), callback, _backgroundHandler);
#pragma warning restore CS0618, CA1422

        return tcs.Task;
    }

    private void CloseDevice()
    {
        try
        {
            _device?.Close();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error closing camera device");
        }
        finally
        {
            _device = null;
            CurrentCamera = null;
        }
    }

    private void EnsureBackgroundThread()
    {
        if (_backgroundThread is not null)
        {
            return;
        }

        _backgroundThread = new HandlerThread("LocalWebcam.Camera2");
        _backgroundThread.Start();
        _backgroundHandler = new Handler(_backgroundThread.Looper!);
    }

    private sealed class DeviceStateCallback(TaskCompletionSource<CameraDevice> tcs, ILogger logger, Action<CameraErrorEventArgs> onError) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => tcs.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            logger.LogWarning("Camera disconnected: {CameraId}", camera.Id);
            var ex = new InvalidOperationException($"Camera {camera.Id} disconnected.");
            if (!tcs.TrySetException(ex))
            {
                onError(new CameraErrorEventArgs($"Camera {camera.Id} disconnected.", ex));
            }

            camera.Close();
        }

        public override void OnError(CameraDevice camera, [global::Android.Runtime.GeneratedEnum] CameraError error)
        {
            logger.LogError("Camera error {Error} on {CameraId}", error, camera.Id);
            var ex = new InvalidOperationException($"Camera {camera.Id} reported error: {error}.");
            if (!tcs.TrySetException(ex))
            {
                onError(new CameraErrorEventArgs($"Camera {camera.Id} reported error: {error}.", ex));
            }

            camera.Close();
        }
    }

    private sealed class SessionStateCallback(TaskCompletionSource<CameraCaptureSession> tcs, ILogger logger) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => tcs.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            logger.LogError("Camera capture session configuration failed");
            tcs.TrySetException(new InvalidOperationException("Camera capture session configuration failed."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync().ConfigureAwait(false);
        CloseDevice();

        _backgroundThread?.QuitSafely();
        _backgroundThread = null;
        _backgroundHandler = null;
    }
}
