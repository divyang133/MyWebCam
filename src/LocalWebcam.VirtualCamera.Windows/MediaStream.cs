using System.Threading;
using DirectN;
using LocalWebcam.Shared.VirtualCamera;
using LocalWebcam.VirtualCamera.Windows.Utilities;

namespace LocalWebcam.VirtualCamera.Windows;

/// <summary>
/// The single video stream our virtual camera exposes. Offers exactly one
/// media type — NV12 at <see cref="VirtualCameraFrameChannel.FrameResolution"/>
/// — unlike the reference sample (which offers both RGB32 and NV12 and
/// therefore needs a color-space-converting frame generator): our frames
/// already arrive as NV12 straight out of <c>WindowsVideoDecoder</c>, so
/// there is nothing to convert, and offering only the format we can produce
/// natively avoids that whole GPU/CPU conversion path entirely.
///
/// <see cref="RequestSample"/> is the pull callback the Frame Server invokes
/// whenever a consumer (OBS, Teams, ...) wants the next frame; it must return
/// quickly, so it never itself waits on I/O — <see cref="SharedFrameSource"/>
/// keeps the single latest frame ready on a background thread, and this just
/// copies whatever's currently there. Adapted from
/// smourier/VCamNetSample's MediaStream.cs.
/// </summary>
public sealed class MediaStream : MFAttributesBase, IMFMediaStream2, IKsControl
{
    // Only sizes the Frame-Server-provided allocator we initialize in
    // Start() - RequestSample no longer sources samples from it (see its
    // doc comment): using AllocateSample from this pool was found,
    // reproducibly, to hang after exactly this many calls (confirmed by
    // varying this constant and watching the hang point move with it),
    // apparently because whatever's downstream wasn't releasing pooled
    // samples fast enough to free up slots. Kept around in case Frame
    // Server itself still expects a sized allocator to exist.
    public const int NUM_ALLOCATOR_SAMPLES = 10;

    private readonly object _lock = new();
    private readonly MediaSource _source;
    private IComObject<IMFMediaEventQueue>? _queue;
    private IComObject<IMFStreamDescriptor>? _descriptor;
    private IComObject<IMFVideoSampleAllocatorEx>? _allocator;
    private _MF_STREAM_STATE _state;
    private SharedFrameSource? _frameSource;

    public MediaStream(MediaSource source, uint index)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;

            // This constructor runs fresh on every Frame Server activation,
            // in this out-of-process COM server - it can't see whatever
            // quality Desktop's user most recently picked except by reading
            // it back from disk. See VirtualCameraConfig's doc comment. Must
            // happen before constructing _frameSource below, which sizes its
            // buffer from VirtualCameraFrameChannel.FrameResolution - hence
            // this is an explicit statement here rather than a field
            // initializer (field initializers would run too early, before
            // this reload).
            VirtualCameraFrameChannel.ReloadFromSavedConfig();
            _frameSource = new SharedFrameSource();

            SetGUID(MFConstants.MF_DEVICESTREAM_STREAM_CATEGORY, KSMedia.PINNAME_VIDEO_CAPTURE).ThrowOnError();
            SetUINT32(MFConstants.MF_DEVICESTREAM_STREAM_ID, index).ThrowOnError();
            SetUINT32(MFConstants.MF_DEVICESTREAM_FRAMESERVER_SHARED, 1).ThrowOnError();
            SetUINT32(MFConstants.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, (uint)_MFFrameSourceTypes.MFFrameSourceTypes_Color).ThrowOnError();

            Functions.MFCreateEventQueue(out var queue).ThrowOnError();
            _queue = new ComObject<IMFMediaEventQueue>(queue);

            var resolution = VirtualCameraFrameChannel.FrameResolution;

            Functions.MFCreateMediaType(out var nv12Type).ThrowOnError();
            nv12Type.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            nv12Type.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            // IMFAttributesExtensions.SetSize only binds to IComObject<IMFAttributes>/IMFAttributes
            // receivers, not the raw IMFMediaType we get here - pack the UINT64 attribute
            // ourselves (high 32 bits = width, low 32 bits = height, per the native MF contract).
            nv12Type.SetUINT64(MFConstants.MF_MT_FRAME_SIZE, ((ulong)(uint)resolution.Width << 32) | (uint)resolution.Height).ThrowOnError();
            nv12Type.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, (uint)resolution.Width).ThrowOnError();
            nv12Type.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
            nv12Type.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
            nv12Type.SetRatio(MFConstants.MF_MT_FRAME_RATE, (uint)VirtualCameraFrameChannel.FrameRateNumerator, (uint)VirtualCameraFrameChannel.FrameRateDenominator);
            var bitrate = resolution.Width * 3 * resolution.Height * 8 * VirtualCameraFrameChannel.FrameRateNumerator / 2;
            nv12Type.SetUINT32(MFConstants.MF_MT_AVG_BITRATE, (uint)bitrate).ThrowOnError();
            nv12Type.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            IMFMediaType[] mediaTypes = [nv12Type];
            Functions.MFCreateStreamDescriptor(index, mediaTypes.Length, mediaTypes, out var descriptor).ThrowOnError();
            descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
            handler.SetCurrentMediaType(mediaTypes[0]).ThrowOnError();
            _descriptor = new ComObject<IMFStreamDescriptor>(descriptor);
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT Start(IMFMediaType? type)
    {
        var queue = _queue;
        var allocator = _allocator;
        if (queue == null || allocator == null)
        {
            return HRESULTS.MF_E_SHUTDOWN;
        }

        if (type != null)
        {
            allocator.Object.InitializeSampleAllocator(NUM_ALLOCATOR_SAMPLES, type).ThrowOnError();
        }

        queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStarted, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
        _state = _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING;
        EventProvider.LogInfo("Started");
        return HRESULTS.S_OK;
    }

    public HRESULT Stop()
    {
        var queue = _queue;
        var allocator = _allocator;
        if (queue == null || allocator == null)
        {
            return HRESULTS.MF_E_SHUTDOWN;
        }

        allocator.Object.UninitializeSampleAllocator();
        queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStopped, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
        _state = _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED;
        return HRESULTS.S_OK;
    }

    public static MFSampleAllocatorUsage GetAllocatorUsage() => MFSampleAllocatorUsage.MFSampleAllocatorUsage_UsesProvidedAllocator;

    public HRESULT SetAllocator(object allocator)
    {
        if (allocator == null)
        {
            return HRESULTS.E_POINTER;
        }

        if (allocator is not IMFVideoSampleAllocatorEx aex)
        {
            return HRESULTS.E_NOINTERFACE;
        }

        _allocator = new ComObject<IMFVideoSampleAllocatorEx>(aex);
        return HRESULTS.S_OK;
    }

    /// <summary>
    /// No-op by design (see <see cref="MediaSource.SetD3DManager"/>): we never
    /// attach a DirectX device manager to the allocator, so
    /// <c>AllocateSample</c> always hands back a plain CPU-lockable buffer,
    /// which is exactly what <see cref="SharedFrameSource.Generate"/> needs
    /// to <c>Marshal.Copy</c> decoded NV12 bytes into.
    /// </summary>
    public HRESULT Set3DManager(object manager) => HRESULTS.S_OK;

    public HRESULT GetEvent(uint flags, out IMFMediaEvent evt)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                evt = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.GetEvent(flags, out evt);
        }
    }

    public HRESULT BeginGetEvent(IMFAsyncCallback callback, object state)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.BeginGetEvent(callback, state);
        }
    }

    public HRESULT EndGetEvent(IMFAsyncResult result, out IMFMediaEvent evt)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                evt = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.EndGetEvent(result, out evt);
        }
    }

    public HRESULT QueueEvent(uint type, Guid extendedType, HRESULT hrStatus, PROPVARIANT value)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.QueueEventParamVar(type, extendedType, hrStatus, value);
        }
    }

    public HRESULT GetMediaSource(out IMFMediaSource mediaSource)
    {
        lock (_lock)
        {
            mediaSource = _source;
            return HRESULTS.S_OK;
        }
    }

    public HRESULT GetStreamDescriptor(out IMFStreamDescriptor streamDescriptor)
    {
        lock (_lock)
        {
            var descriptor = _descriptor;
            if (descriptor == null)
            {
                streamDescriptor = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            streamDescriptor = descriptor.Object;
            return HRESULTS.S_OK;
        }
    }

    public HRESULT RequestSample(object token)
    {
        try
        {
            lock (_lock)
            {
                var queue = _queue;
                var frameSource = _frameSource;
                if (queue == null || frameSource == null)
                {
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                // Allocates a fresh sample/buffer per call instead of pulling
                // from the Frame-Server-provided IMFVideoSampleAllocatorEx
                // pool (matches Microsoft's own documented Custom Media
                // Source sample - see
                // learn.microsoft.com/windows-hardware/drivers/stream/frame-server-custom-media-source).
                // The pooled-allocator approach was observed, reproducibly,
                // hanging after exactly NUM_ALLOCATOR_SAMPLES calls to
                // RequestSample (confirmed by bumping the pool size and
                // watching the hang point move with it) - Frame Server never
                // requested another sample once the pool was exhausted, i.e.
                // whatever's downstream wasn't releasing pooled samples fast
                // enough (or at all) to free up slots. We never attach a
                // D3D manager (see Set3DManager's doc comment - AllocateSample
                // always handed back a plain CPU buffer anyway), so there was
                // no GPU-interop benefit to the pooled allocator we were
                // actually using; a plain COM-refcounted buffer that's freed
                // normally once the pipeline is done with it removes the
                // fixed ceiling entirely.
                Functions.MFCreateSample(out var sample).ThrowOnError();
                using var outputSample = new ComObject<IMFSample>(sample);

                Functions.MFCreateMemoryBuffer((uint)VirtualCameraFrameChannel.Nv12FrameByteSize(VirtualCameraFrameChannel.FrameResolution), out var mediaBuffer).ThrowOnError();
                using var outputBuffer = new ComObject<IMFMediaBuffer>(mediaBuffer);
                sample.AddBuffer(mediaBuffer).ThrowOnError();

                sample.SetSampleTime(Functions.MFGetSystemTime()).ThrowOnError();
                sample.SetSampleDuration(333333).ThrowOnError();

                frameSource.Generate(outputSample);
                if (token != null)
                {
                    sample.SetUnknown(MFConstants.MFSampleExtension_Token, token).ThrowOnError();
                }

                queue.Object.QueueEventParamUnk((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEMediaSample, Guid.Empty, HRESULTS.S_OK, sample).ThrowOnError();
                return HRESULTS.S_OK;
            }
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT SetStreamState(_MF_STREAM_STATE value)
    {
        try
        {
            if (_state != value)
            {
                switch (value)
                {
                    case _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED:
                        return Stop();

                    case _MF_STREAM_STATE.MF_STREAM_STATE_PAUSED:
                        if (_state != _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING)
                        {
                            return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                        }

                        _state = value;
                        break;

                    case _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING:
                        return Start(null);

                    default:
                        return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                }
            }

            return HRESULTS.S_OK;
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT GetStreamState(out _MF_STREAM_STATE value)
    {
        value = _state;
        return HRESULTS.S_OK;
    }

    public HRESULT KsProperty(ref KSIDENTIFIER property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Utilities.Constants.ErrorSetNotFound);
    }

    public HRESULT KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Utilities.Constants.ErrorSetNotFound);
    }

    public HRESULT KsEvent(ref KSIDENTIFIER evt, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Utilities.Constants.ErrorSetNotFound);
    }

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _descriptor!, null)?.Dispose();
        Interlocked.Exchange(ref _queue!, null)?.Dispose();
        Interlocked.Exchange(ref _allocator!, null)?.Dispose();
        Interlocked.Exchange(ref _frameSource, null)?.Dispose();
        base.Dispose(disposing);
    }
}
