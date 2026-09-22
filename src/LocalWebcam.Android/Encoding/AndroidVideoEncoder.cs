using Android.Media;
using Android.OS;
using Android.Views;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Android.Encoding;

/// <summary>
/// Hardware H.264 encoder backed by <see cref="MediaCodec"/> in Surface-input
/// mode: the camera renders frames directly into the codec's input surface,
/// so no YUV/RGB conversion or CPU-side frame copy ever happens in managed
/// code (spec section 10). Encoded Annex-B access units are drained on a
/// dedicated background thread and raised via <see cref="FrameEncoded"/>.
/// </summary>
public sealed class AndroidVideoEncoder(ILogger<AndroidVideoEncoder> logger) : IVideoEncoder
{
    private const string MimeTypeAvc = "video/avc";

    // MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME - not exposed as a typed
    // constant in the .NET binding; the raw Java key is stable API surface.
    private const string ParameterKeyRequestSyncFrame = "request-sync";

    // MediaCodec.PARAMETER_KEY_VIDEO_BITRATE - same story: stable Java key,
    // not surfaced as a typed constant in the .NET binding. Supported by the
    // platform's hardware AVC encoder for live bitrate changes without a
    // stop/reconfigure/start cycle.
    private const string ParameterKeyVideoBitrate = "video-bitrate";

    private const long DequeueTimeoutUs = 10_000;

    private MediaCodec? _codec;
    private Surface? _inputSurface;
    private CancellationTokenSource? _drainLoopCts;
    private Task? _drainLoopTask;

    public bool IsEncoding { get; private set; }

    public event EventHandler<EncodedVideoFrame>? FrameEncoded;

    public event EventHandler<CameraErrorEventArgs>? Error;

    public object CreateInputSurface(VideoStreamConfig config)
    {
        if (_codec is not null)
        {
            throw new InvalidOperationException("Encoder is already configured. Dispose this instance before reconfiguring.");
        }

        var format = MediaFormat.CreateVideoFormat(MimeTypeAvc, config.Resolution.Width, config.Resolution.Height)
            ?? throw new InvalidOperationException("Failed to create MediaFormat.");

        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, config.BitrateBps);
        format.SetInteger(MediaFormat.KeyFrameRate, config.FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);

        var codec = MediaCodec.CreateEncoderByType(MimeTypeAvc)
            ?? throw new InvalidOperationException($"No hardware/software encoder available for {MimeTypeAvc}.");

        // Latency tuning, gated on what this specific hardware encoder
        // actually reports supporting: MediaCodec.configure() throws
        // IllegalArgumentException outright for an unsupported combination
        // rather than ignoring it (confirmed on a OnePlus 7 Pro's Qualcomm
        // encoder - setting KeyLowLatency/KeyBitrateMode=Cbr unconditionally
        // crashed StartAsync every time). Query capabilities first so we
        // only ask for what's actually there, rather than guessing which
        // key was the problem. KeyLatency/KeyPriority are older, broadly-
        // supported hints Android's own docs describe as ignored if
        // unsupported, so those stay unconditional.
        var capabilities = codec.CodecInfo?.GetCapabilitiesForType(MimeTypeAvc);

        if (capabilities?.IsFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURELowLatency) == true)
        {
            format.SetInteger(MediaFormat.KeyLowLatency, 1);
        }

        format.SetInteger(MediaFormat.KeyLatency, 0);
        format.SetInteger(MediaFormat.KeyPriority, 0);

        // Without this, this hardware encoder emits B-frames by default,
        // which forces the decoder to hold multiple frames to reorder them
        // back into presentation order before it can output any of them -
        // confirmed on real hardware as a steady "decoder held 2+ frame(s)
        // before this output" on nearly every frame, adding a full extra
        // frame interval or more of pure decode-side latency on top of
        // network/encode time. Zero B-frames means decode order always
        // matches presentation order.
        format.SetInteger(MediaFormat.KeyMaxBFrames, 0);

        // Constant bitrate instead of the default variable-bitrate mode:
        // steadier per-frame size means steadier per-frame network/decode
        // time, which matters more for a live low-latency stream than the
        // slightly better quality-per-bit VBR can offer at the same average
        // rate - but plenty of hardware AVC encoders (especially with
        // Surface input) only support VBR/CQ, so this is capability-gated
        // too.
        if (capabilities?.EncoderCapabilities?.IsBitrateModeSupported(BitrateMode.Cbr) == true)
        {
            format.SetInteger(MediaFormat.KeyBitrateMode, (int)BitrateMode.Cbr);
        }

        // Without this, the encoder picks its own default profile - commonly
        // Baseline, chosen for maximum decoder compatibility rather than
        // quality. High profile's extra coding tools (8x8 transforms, more
        // reference frames, etc.) give meaningfully better quality at the
        // same bitrate, which is what "quality isn't good" at 1080p60
        // reports were actually pointing at - the negotiated resolution/
        // frame rate were already correct. Picking the highest-Level entry
        // the device actually reports for a given profile (rather than
        // hardcoding a level) avoids requesting a level this specific
        // encoder can't sustain at 1080p60.
        var profileLevel = ChooseAvcProfileLevel(capabilities);
        if (profileLevel is { } pl)
        {
            format.SetInteger(MediaFormat.KeyProfile, pl.Profile);
            format.SetInteger(MediaFormat.KeyLevel, pl.Level);
        }

        try
        {
            try
            {
                codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            }
            catch (Java.Lang.IllegalArgumentException ex)
            {
                // Belt-and-braces: even capability-gated keys surprised us
                // once on real hardware (see the comments above), so if
                // configure() still rejects the format, retry with none of
                // the latency/bitrate-mode tuning rather than failing to
                // stream at all - a slightly higher-latency stream beats no
                // stream.
                logger.LogWarning(ex, "Encoder rejected the low-latency format; retrying without the latency/bitrate-mode tuning");
                format.RemoveKey(MediaFormat.KeyLowLatency);
                format.RemoveKey(MediaFormat.KeyLatency);
                format.RemoveKey(MediaFormat.KeyPriority);
                format.RemoveKey(MediaFormat.KeyBitrateMode);
                format.RemoveKey(MediaFormat.KeyMaxBFrames);
                format.RemoveKey(MediaFormat.KeyProfile);
                format.RemoveKey(MediaFormat.KeyLevel);
                codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            }

            var surface = codec.CreateInputSurface()
                ?? throw new InvalidOperationException("MediaCodec did not return an input surface.");

            _codec = codec;
            _inputSurface = surface;
            return surface;
        }
        catch
        {
            codec.Release();
            throw;
        }
    }

    /// <summary>
    /// Picks the highest-Level entry this specific encoder reports for the
    /// best available profile (High, falling back to Main), rather than
    /// hardcoding a level that might exceed what the hardware can actually
    /// sustain. Returns null if the device doesn't report ProfileLevels at
    /// all, leaving the encoder's own default in place.
    /// </summary>
    private static (int Profile, int Level)? ChooseAvcProfileLevel(MediaCodecInfo.CodecCapabilities? capabilities)
    {
        var profileLevels = capabilities?.ProfileLevels;
        if (profileLevels is null)
        {
            return null;
        }

        int[] preferredProfiles =
        [
            (int)MediaCodecProfileType.Avcprofilehigh,
            (int)MediaCodecProfileType.Avcprofilemain,
        ];

        foreach (var profile in preferredProfiles)
        {
            var best = profileLevels
                .Where(pl => (int)pl.Profile == profile)
                .OrderByDescending(pl => (int)pl.Level)
                .FirstOrDefault();

            if (best is not null)
            {
                return ((int)best.Profile, (int)best.Level);
            }
        }

        return null;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_codec is not { } codec)
        {
            throw new InvalidOperationException($"Call {nameof(CreateInputSurface)} before {nameof(StartAsync)}.");
        }

        if (IsEncoding)
        {
            return Task.CompletedTask;
        }

        codec.Start();
        IsEncoding = true;

        _drainLoopCts = new CancellationTokenSource();
        _drainLoopTask = Task.Run(() => DrainLoop(codec, _drainLoopCts.Token));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEncoding)
        {
            return;
        }

        IsEncoding = false;

        if (_drainLoopCts is not null)
        {
            await _drainLoopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_drainLoopTask is not null)
        {
            try
            {
                await _drainLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Encoder drain loop ended with an exception");
            }
        }

        try
        {
            _codec?.Stop();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MediaCodec.Stop threw");
        }
    }

    public Task RequestKeyFrameAsync()
    {
        if (_codec is not { } codec || !IsEncoding)
        {
            return Task.CompletedTask;
        }

        using var bundle = new Bundle();
        bundle.PutInt(ParameterKeyRequestSyncFrame, 0);
        codec.SetParameters(bundle);
        return Task.CompletedTask;
    }

    public Task SetBitrateAsync(int bitrateBps)
    {
        if (_codec is not { } codec || !IsEncoding)
        {
            return Task.CompletedTask;
        }

        using var bundle = new Bundle();
        bundle.PutInt(ParameterKeyVideoBitrate, bitrateBps);
        codec.SetParameters(bundle);
        logger.LogInformation("Encoder bitrate changed to {BitrateBps} bps", bitrateBps);
        return Task.CompletedTask;
    }

    private void DrainLoop(MediaCodec codec, CancellationToken cancellationToken)
    {
        using var bufferInfo = new MediaCodec.BufferInfo();

        while (!cancellationToken.IsCancellationRequested)
        {
            int outputIndex;
            try
            {
                outputIndex = codec.DequeueOutputBuffer(bufferInfo, DequeueTimeoutUs);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DequeueOutputBuffer failed");
                Error?.Invoke(this, new CameraErrorEventArgs("Hardware encoder failed while draining output.", ex));
                return;
            }

            if (outputIndex == (int)MediaCodecInfoState.TryAgainLater)
            {
                continue;
            }

            if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                logger.LogInformation("Encoder output format changed: {Format}", codec.OutputFormat);
                continue;
            }

            if (outputIndex == (int)MediaCodecInfoState.OutputBuffersChanged || outputIndex < 0)
            {
                continue;
            }

            try
            {
                var outputBuffer = codec.GetOutputBuffer(outputIndex);
                if (outputBuffer is not null && bufferInfo.Size > 0)
                {
                    var data = new byte[bufferInfo.Size];
                    outputBuffer.Position(bufferInfo.Offset);
                    outputBuffer.Limit(bufferInfo.Offset + bufferInfo.Size);
                    outputBuffer.Get(data);

                    var isKeyFrame = (bufferInfo.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                    var isConfigOnly = (bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0;

                    if (!isConfigOnly)
                    {
                        FrameEncoded?.Invoke(this, new EncodedVideoFrame(data, bufferInfo.PresentationTimeUs, isKeyFrame));
                    }
                    else
                    {
                        // SPS/PPS-only buffer; later phases prepend this to the
                        // first frame sent to a newly (re)connected receiver.
                        FrameEncoded?.Invoke(this, new EncodedVideoFrame(data, bufferInfo.PresentationTimeUs, IsKeyFrame: true));
                    }
                }
            }
            finally
            {
                codec.ReleaseOutputBuffer(outputIndex, render: false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _drainLoopCts?.Dispose();
        _drainLoopCts = null;
        _drainLoopTask = null;

        try
        {
            _codec?.Release();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MediaCodec.Release threw");
        }

        _codec?.Dispose();
        _codec = null;

        _inputSurface?.Release();
        _inputSurface?.Dispose();
        _inputSurface = null;
    }
}
