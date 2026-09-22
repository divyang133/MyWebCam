using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace LocalWebcam.Windows.Video;

/// <summary>
/// H.264 decoder built on the Media Foundation H.264 Decoder MFT
/// (found via <c>MFTEnumEx</c> rather than a hardcoded CLSID, so hardware-
/// accelerated decoders are picked up automatically when present). Output
/// is NV12, matching the decoder's native output and spec section 17's
/// format choice — no extra conversion happens here beyond what's needed
/// for on-screen preview (<see cref="Nv12ToBgraConverter"/>), which is a
/// display-only concern, not part of the core decode pipeline.
/// </summary>
public sealed class WindowsVideoDecoder(ILogger<WindowsVideoDecoder> logger) : IVideoDecoder
{
    // MFT_ENUM_FLAG_SYNCMFT (0x1): restrict to synchronous MFTs. The built-in
    // Microsoft H.264 Video Decoder MFT is synchronous; true async-only
    // hardware MFTs would need the IMFAsyncCallback handshake, out of scope
    // for this phase. Being "synchronous" does NOT by itself mean it uses
    // DXVA hardware acceleration - confirmed on real hardware via GPU engine
    // counters showing zero video-decode engine activity: the decoder
    // silently defaults to pure software decode unless explicitly handed a
    // D3D11 device via MFT_MESSAGE_SET_D3D_MANAGER (see TryEnableHardwareDecode).
    private const uint MftEnumFlagSyncMft = 0x1;

    // MF_LOW_LATENCY, from mfapi.h - see the comment where this is used.
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22271408f63");

    // CODECAPI_AVLowLatencyMode, from codecapi.h - a DIFFERENT GUID from
    // MF_LOW_LATENCY above despite sharing the same first several bytes
    // (an easy, real mistake: an earlier version of this code accidentally
    // reused MfLowLatency's value here, which made ICodecAPI.SetValue
    // correctly reject it with E_NOTIMPL as an unrecognized parameter -
    // confirmed by testing on real hardware, not assumed). See
    // TrySetLowLatencyViaCodecApi's doc comment for why this needs setting
    // separately from MF_LOW_LATENCY at all.
    private static readonly Guid CodecApiAvLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private bool _mfStarted;
    private IMFTransform? _transform;
    private VideoResolution _configuredResolution;
    private ID3D11Device? _d3dDevice;
    private IMFDXGIDeviceManager? _deviceManager;

    // The decoder's actually-negotiated output frame size, which is what the
    // output buffers are really laid out as - not necessarily the resolution
    // we requested via SetInputType. FindOutputType only filters by subtype
    // (NV12), so nothing previously verified these matched; a mismatch here
    // silently misinterprets the Y/UV plane boundaries of every frame
    // (confirmed on real hardware as a corrupted band in the decoded image).
    private int _actualWidth;
    private int _actualHeight;

    public event EventHandler<DecodedVideoFrame>? FrameDecoded;

    public event EventHandler<CameraErrorEventArgs>? Error;

    public void Configure(VideoResolution resolution)
    {
        if (!_mfStarted)
        {
            MediaFactory.MFStartup(false).CheckError();
            _mfStarted = true;
        }

        using var activateCollection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            MftEnumFlagSyncMft,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 });

        var activate = activateCollection.FirstOrDefault()
            ?? throw new InvalidOperationException("No H.264 decoder is available on this system (Media Foundation MFTEnumEx returned none).");

        var transform = activate.ActivateObject<IMFTransform>();

        TryEnableHardwareDecode(transform);

        // MF_LOW_LATENCY (not exposed as a typed key in Vortice.MediaFoundation;
        // the raw GUID from mfapi.h is stable API surface): tells the decoder
        // to emit each frame as soon as it can rather than buffering several
        // frames deep for smoother average throughput. Without this, the
        // built-in H.264 decoder MFT was observed adding several hundred ms
        // of pure buffering latency even though our stream has no B-frames to
        // reorder - this is the single biggest glass-to-glass latency win
        // available on the decode side.
        try
        {
            transform.Attributes.Set(MfLowLatency, 1u);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            logger.LogWarning(ex, "This decoder does not support MF_LOW_LATENCY; continuing without it");
        }

        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, PackSize(resolution.Width, resolution.Height));
        transform.SetInputType(0, inputType, 0);

        var outputType = FindOutputType(transform, VideoFormatGuids.NV12)
            ?? throw new InvalidOperationException("The H.264 decoder did not offer an NV12 output type.");
        transform.SetOutputType(0, outputType, 0);
        UpdateActualFrameSize(outputType, resolution);

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _transform = transform;
        _configuredResolution = resolution;
        logger.LogInformation("H.264 decoder configured for {Resolution}", resolution);
    }

    /// <summary>
    /// Reads back the frame size the decoder actually negotiated for its
    /// output type and uses that (not the resolution we requested) to
    /// interpret decoded buffers - see the field doc comments on
    /// <see cref="_actualWidth"/>/<see cref="_actualHeight"/> for why this
    /// matters. Called both on initial configuration and after a
    /// TransformStreamChange, since the negotiated size can change there too.
    /// </summary>
    private void UpdateActualFrameSize(IMFMediaType outputType, VideoResolution requested)
    {
        var frameSize = outputType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        _actualWidth = (int)(frameSize >> 32);
        _actualHeight = (int)(frameSize & 0xFFFFFFFF);

        if (_actualWidth != requested.Width || _actualHeight != requested.Height)
        {
            logger.LogWarning(
                "The H.264 decoder negotiated an output frame size of {ActualWidth}x{ActualHeight}, which differs from the requested {Requested} - using the negotiated size to interpret decoded buffers",
                _actualWidth, _actualHeight, requested);
        }
    }

    /// <summary>
    /// Creates a D3D11 device and hands it to the decoder MFT via
    /// MFT_MESSAGE_SET_D3D_MANAGER, so it decodes on the GPU's dedicated
    /// video-decode hardware instead of the CPU - without this, the
    /// built-in decoder MFT decodes in pure software even though it's
    /// DXVA-capable in principle (confirmed on real hardware: ~280% of one
    /// CPU core for a single 720p30 stream, and zero GPU video-decode
    /// engine activity, before this). The rest of the pipeline
    /// (EmitDecodedFrame's ConvertToContiguousBuffer/Lock call) needs no
    /// changes for this: Media Foundation's DXGI-surface-backed buffer
    /// implementation already does the GPU-to-CPU readback internally on
    /// Lock (confirmed against Microsoft's own docs) - it's simply slightly
    /// less efficient than IMF2DBuffer::Lock2D would be for a read-only
    /// case, not a correctness concern.
    ///
    /// Best-effort: falls back to software decode (leaving the transform
    /// untouched) if D3D11 device creation or the MFT message fails for
    /// any reason - a real possibility on a GPU-less VM, a driver without
    /// video decode support, or an unusual remote-desktop session.
    /// </summary>
    private void TryEnableHardwareDecode(IMFTransform transform)
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out ID3D11Device device,
                out ID3D11DeviceContext context);
            result.CheckError();
            context.Dispose();

            // Media Foundation requires the device to be marked
            // multithread-protected before it's shared with a decoder MFT
            // via a device manager (documented MF/D3D11 interop
            // requirement - the MFT and this process's own D3D11 usage,
            // if any, can otherwise race on the same device).
            using (var multithread = device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            var deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            deviceManager.ResetDevice(device).CheckError();

            transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)deviceManager.NativePointer.ToInt64());

            _d3dDevice = device;
            _deviceManager = deviceManager;
            logger.LogInformation("H.264 decoder using hardware-accelerated (D3D11/DXVA) decode");

            TrySetLowLatencyViaCodecApi(transform);
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            _d3dDevice?.Dispose();
            _d3dDevice = null;
            _deviceManager = null;
            logger.LogWarning(ex, "Could not enable hardware-accelerated video decode; continuing with software decode");
        }
    }

    /// <summary>
    /// Sets CODECAPI_AVLowLatencyMode via ICodecAPI - a separate GUID from
    /// MF_LOW_LATENCY (already set on the transform's IMFAttributes earlier
    /// in Configure) despite the two sharing the same first several bytes,
    /// and set through a completely different interface. Many hardware/DXVA
    /// decoder implementations only actually check the low-latency hint
    /// through ICodecAPI, not the generic IMFAttributes store MF_LOW_LATENCY
    /// uses. Confirmed on real hardware: hardware decode carried ~584ms of
    /// decode+render latency despite MF_LOW_LATENCY being set, with the
    /// decoder logging "held 2 frame(s)" on every single output - versus
    /// the near-zero latency the same attribute achieved for software
    /// decode. This call dropped it to ~7ms, with no further recurring
    /// "held" messages. Also confirmed on real hardware: the documented
    /// boolean semantics are misleading for this specific decoder - a
    /// VT_BOOL VARIANT was rejected with E_INVALIDARG even though
    /// IsSupported/IsModifiable both reported S_OK; a VT_UI4 VARIANT with
    /// value 1 is what actually worked. Best-effort throughout: not every
    /// decoder implements ICodecAPI at all, so QueryInterface (or SetValue)
    /// failing here just means this specific decoder doesn't support the
    /// extra hint, not a real error - MF_LOW_LATENCY alone still applies.
    /// </summary>
    private void TrySetLowLatencyViaCodecApi(IMFTransform transform)
    {
        try
        {
            var iid = typeof(ICodecApi).GUID;
            var hr = Marshal.QueryInterface(transform.NativePointer, in iid, out var codecApiPtr);
            if (hr != 0)
            {
                logger.LogInformation("This decoder does not implement ICodecAPI; relying on MF_LOW_LATENCY alone");
                return;
            }

            var variantPtr = Marshal.AllocHGlobal(16); // sizeof(VARIANT) on both x86 and x64
            try
            {
                var codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(codecApiPtr);

                var isSupportedResult = codecApi.IsSupported(CodecApiAvLowLatencyMode);
                var isModifiableResult = codecApi.IsModifiable(CodecApiAvLowLatencyMode);
                logger.LogInformation("ICodecAPI diagnostics: IsSupported=0x{IsSupported:X8}, IsModifiable=0x{IsModifiable:X8}", isSupportedResult, isModifiableResult);

                // A VT_BOOL VARIANT (via GetNativeVariantForObject(true, ...))
                // was rejected with E_INVALIDARG despite IsSupported/
                // IsModifiable both reporting S_OK - trying VT_UI4 instead,
                // since some CODECAPI properties documented in boolean terms
                // are actually typed as UINT32 under the hood.
                Marshal.GetNativeVariantForObject((uint)1, variantPtr);

                var setResult = codecApi.SetValue(CodecApiAvLowLatencyMode, variantPtr);
                if (setResult != 0)
                {
                    logger.LogInformation("ICodecAPI.SetValue(CODECAPI_AVLowLatencyMode) returned 0x{Result:X8}", setResult);
                }
                else
                {
                    logger.LogInformation("CODECAPI_AVLowLatencyMode set via ICodecAPI");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(variantPtr);
                Marshal.Release(codecApiPtr);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            logger.LogInformation(ex, "Could not set CODECAPI_AVLowLatencyMode; relying on MF_LOW_LATENCY alone");
        }
    }

    private static IMFMediaType? FindOutputType(IMFTransform transform, Guid subtype)
    {
        for (var i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                return null;
            }

            if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) == subtype)
            {
                return candidate;
            }
        }
    }

    private int _submittedSinceLastOutput;

    public void SubmitEncodedFrame(byte[] data, long presentationTimeUs, bool isKeyFrame)
    {
        if (_transform is not { } transform)
        {
            throw new InvalidOperationException($"Call {nameof(Configure)} before {nameof(SubmitEncodedFrame)}.");
        }

        // ProcessInput's contract: the transform takes its own reference if
        // it needs to hold onto the sample, so the caller must release its
        // own reference once the call returns - another real leak, same
        // pattern as the output-side one in DrainOutput (just smaller per
        // frame, since this is the compressed H.264 sample, not the
        // decoded NV12 one).
        using var sample = CreateInputSample(data, presentationTimeUs);

        try
        {
            transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            logger.LogWarning(ex, "ProcessInput rejected a frame (isKeyFrame={IsKeyFrame})", isKeyFrame);
            Error?.Invoke(this, new CameraErrorEventArgs("The decoder rejected an encoded frame.", ex));
            return;
        }

        _submittedSinceLastOutput++;
        var producedOutput = DrainOutput(transform);
        if (producedOutput && _submittedSinceLastOutput > 1)
        {
            // How many frames the decoder held internally before this one
            // finally came out - a direct measure of its internal pipeline
            // depth/latency, independent of anything upstream.
            logger.LogInformation("Decoder held {Count} frame(s) before this output", _submittedSinceLastOutput);
        }

        if (producedOutput)
        {
            _submittedSinceLastOutput = 0;
        }
    }

    private static IMFSample CreateInputSample(byte[] data, long presentationTimeUs)
    {
        // AddBuffer below gives the sample its own COM reference to this
        // buffer - our local reference is separate and needs its own
        // disposal, or its underlying native memory only gets freed
        // whenever the GC eventually finalizes this RCW instead of
        // immediately when the sample itself is disposed.
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = data.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = presentationTimeUs * 10; // microseconds -> 100ns units
        return sample;
    }

    private bool DrainOutput(IMFTransform transform)
    {
        var producedOutput = false;
        while (true)
        {
            var outputInfo = transform.GetOutputStreamInfo(0);
            var providesOwnSamples = ((OutputStreamInfoFlags)outputInfo.Flags).HasFlag(OutputStreamInfoFlags.OutputStreamProvidesSamples);

            var outputBuffer = new OutputDataBuffer { StreamID = 0 };

            if (!providesOwnSamples)
            {
                var ownedSample = MediaFactory.MFCreateSample();
                using (var ownedBuffer = MediaFactory.MFCreateMemoryBuffer(outputInfo.Size))
                {
                    ownedSample.AddBuffer(ownedBuffer);
                }

                outputBuffer.Sample = ownedSample;
            }

            // IMFTransform::ProcessOutput's contract makes the caller
            // responsible for releasing the resulting sample regardless of
            // which side allocated it (providesOwnSamples only changes
            // *who* allocates it, not who releases it) - and the transform
            // can *replace* outputBuffer.Sample with its own instance
            // during the call, so the sample actually needing disposal is
            // whatever ends up there *after* ProcessOutput returns, not
            // necessarily the one assigned above. Never disposing this was
            // a real, severe native-memory leak: every decoded frame's
            // IMFSample/IMFMediaBuffer (~1.3 MB of unmanaged memory at
            // 720p) was retained forever, confirmed on real hardware as
            // >900 MB of growth within ~20 seconds of streaming (roughly
            // 30 fps x 1.3 MB/frame).
            try
            {
                Result result;
                try
                {
                    result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);
                }
                catch (SharpGen.Runtime.SharpGenException ex)
                {
                    logger.LogWarning(ex, "ProcessOutput threw unexpectedly");
                    Error?.Invoke(this, new CameraErrorEventArgs("The decoder failed while producing output.", ex));
                    return producedOutput;
                }

                if (result == ResultCode.TransformNeedMoreInput)
                {
                    return producedOutput;
                }

                if (result == ResultCode.TransformStreamChange)
                {
                    var newOutputType = FindOutputType(transform, VideoFormatGuids.NV12);
                    if (newOutputType is not null)
                    {
                        transform.SetOutputType(0, newOutputType, 0);
                        UpdateActualFrameSize(newOutputType, _configuredResolution);
                    }

                    continue;
                }

                result.CheckError();

                EmitDecodedFrame(outputBuffer.Sample);
                producedOutput = true;
            }
            finally
            {
                outputBuffer.Sample?.Dispose();
            }
        }
    }

    private void EmitDecodedFrame(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var currentLength);

        var displayWidth = _configuredResolution.Width;
        var displayHeight = _configuredResolution.Height;

        byte[] data;
        try
        {
            if (_actualWidth == displayWidth && _actualHeight == displayHeight)
            {
                data = new byte[currentLength];
                Marshal.Copy(ptr, data, 0, currentLength);
            }
            else
            {
                // The decoder's coded frame size doesn't match the requested
                // display size - commonly a macroblock-alignment pad (e.g.
                // 1080 padded to 1088, confirmed on real hardware via the
                // warning in UpdateActualFrameSize). Crop down to exactly
                // the display size here so every downstream consumer (the
                // preview converter, the virtual camera's fixed-size frame
                // buffer) can keep assuming a tightly packed width*height
                // NV12 buffer with no coded-size surprises.
                data = CropNv12(ptr, _actualWidth, _actualHeight, displayWidth, displayHeight);
            }
        }
        finally
        {
            buffer.Unlock();
        }

        FrameDecoded?.Invoke(this, new DecodedVideoFrame(data, displayWidth, displayHeight, sample.SampleTime / 10));
    }

    private static byte[] CropNv12(nint sourcePtr, int codedWidth, int codedHeight, int displayWidth, int displayHeight)
    {
        var result = new byte[displayWidth * displayHeight * 3 / 2];

        for (var row = 0; row < displayHeight; row++)
        {
            Marshal.Copy(sourcePtr + row * codedWidth, result, row * displayWidth, displayWidth);
        }

        // The UV plane starts after the full CODED-height Y plane, not the
        // (shorter) display height - using displayHeight here would read
        // into the tail of the padded Y plane instead of the real chroma
        // data.
        var uvSourceOffset = codedWidth * codedHeight;
        var uvDestOffset = displayWidth * displayHeight;
        var uvRows = displayHeight / 2;
        for (var row = 0; row < uvRows; row++)
        {
            Marshal.Copy(sourcePtr + uvSourceOffset + row * codedWidth, result, uvDestOffset + row * displayWidth, displayWidth);
        }

        return result;
    }

    private static ulong PackSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;

    public void Dispose()
    {
        if (_transform is not null)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                logger.LogWarning(ex, "Error notifying decoder of end-of-stream during dispose");
            }

            _transform.Dispose();
            _transform = null;
        }

        _deviceManager?.Dispose();
        _deviceManager = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;

        if (_mfStarted)
        {
            try
            {
                MediaFactory.MFShutdown();
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                logger.LogWarning(ex, "MFShutdown threw during dispose");
            }

            _mfStarted = false;
        }
    }
}
