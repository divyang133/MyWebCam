using System.Runtime.InteropServices;
using System.Threading;
using DirectN;
using LocalWebcam.VirtualCamera.Windows.Utilities;
using Windows.ApplicationModel;
using Constants = LocalWebcam.VirtualCamera.Windows.Utilities.Constants;

namespace LocalWebcam.VirtualCamera.Windows;

/// <summary>
/// The Media Foundation media source the Frame Server loads (in its own
/// process) once an app opens our virtual camera. Owns exactly one
/// <see cref="MediaStream"/> (single video stream, NV12 only — see
/// <see cref="MediaStream"/> for why we don't offer RGB32 like the reference
/// sample). Adapted from smourier/VCamNetSample's MediaSource.cs; the event
/// queue / presentation descriptor plumbing here is boilerplate every
/// <c>IMFMediaSourceEx</c> implementation needs and is intentionally kept
/// close to the reference rather than reinvented.
/// </summary>
public sealed class MediaSource : MFAttributesBase, IMFMediaSourceEx, IMFSampleAllocatorControl, IMFGetService, IKsControl, ICustomQueryInterface
{
    private readonly object _lock = new();
    private readonly MediaStream[] _streams;
    private IComObject<IMFMediaEventQueue>? _queue;
    private IComObject<IMFPresentationDescriptor>? _presentationDescriptor;

    public MediaSource()
    {
        try
        {
            _streams = [new MediaStream(this, 0)];

            uint streamId = 0;
            Functions.MFCreateSensorProfile(KSMedia.KSCAMERAPROFILE_Legacy, 0, null, out var legacy).ThrowOnError();
            legacy.AddProfileFilter(streamId, "((RES==;FRT<=30,1;SUT==))").ThrowOnError();

            Functions.MFCreateSensorProfileCollection(out var collection).ThrowOnError();
            collection.AddProfile(legacy).ThrowOnError();

            SetUnknown(MFConstants.MF_DEVICEMFT_SENSORPROFILE_COLLECTION, collection).ThrowOnError();

            try
            {
                var current = AppInfo.Current;
                if (current != null)
                {
                    SetString(MFConstants.MF_VIRTUALCAMERA_CONFIGURATION_APP_PACKAGE_FAMILY_NAME, current.PackageFamilyName).ThrowOnError();
                }
            }
            catch (Exception ex)
            {
                EventProvider.LogInfo("Not an AppX: " + ex.Message);
            }

            var descriptors = new IMFStreamDescriptor[_streams.Length];
            for (var i = 0; i < descriptors.Length; i++)
            {
                _streams[i].GetStreamDescriptor(out descriptors[i]).ThrowOnError();
            }

            Functions.MFCreatePresentationDescriptor(descriptors.Length, descriptors, out var descriptor).ThrowOnError();
            _presentationDescriptor = new ComObject<IMFPresentationDescriptor>(descriptor);

            Functions.MFCreateEventQueue(out var queue).ThrowOnError();
            _queue = new ComObject<IMFMediaEventQueue>(queue);
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }

    private int GetStreamIndexById(uint id)
    {
        for (var i = 0; i < _streams.Length; i++)
        {
            if (_streams[i].GetStreamDescriptor(out var desc).IsError)
            {
                return -1;
            }

            if (desc.GetStreamIdentifier(out var sid).IsError)
            {
                return -1;
            }

            if (sid == id)
            {
                return i;
            }
        }

        return -1;
    }

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

    public HRESULT GetCharacteristics(out uint characteristics)
    {
        characteristics = (uint)_MFMEDIASOURCE_CHARACTERISTICS.MFMEDIASOURCE_IS_LIVE;
        return HRESULTS.S_OK;
    }

    public HRESULT CreatePresentationDescriptor(out IMFPresentationDescriptor presentationDescriptor)
    {
        lock (_lock)
        {
            if (_presentationDescriptor == null)
            {
                presentationDescriptor = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return _presentationDescriptor.Object.Clone(out presentationDescriptor);
        }
    }

    public HRESULT Start(IMFPresentationDescriptor presentationDescriptor, nint guidTimeFormat, PROPVARIANT startPosition)
    {
        try
        {
            if (guidTimeFormat != IntPtr.Zero)
            {
                var guid = Marshal.PtrToStructure<Guid>(guidTimeFormat);
                if (guid != Guid.Empty)
                {
                    return HRESULTS.E_INVALIDARG;
                }
            }

            lock (_lock)
            {
                var queue = _queue;
                var ps = _presentationDescriptor;
                if (queue == null || ps == null)
                {
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                presentationDescriptor.GetStreamDescriptorCount(out var count);
                if (count != _streams.Length)
                {
                    return HRESULTS.E_INVALIDARG;
                }

                for (var i = 0; i < count; i++)
                {
                    presentationDescriptor.GetStreamDescriptorByIndex((uint)i, out var selected, out var descriptor).ThrowOnError();
                    descriptor.GetStreamIdentifier(out var id).ThrowOnError();

                    var index = GetStreamIndexById(id);
                    if (index < 0)
                    {
                        return HRESULTS.E_INVALIDARG;
                    }

                    ps.Object.GetStreamDescriptorByIndex((uint)index, out var thisSelected, out var thisDescriptor).ThrowOnError();
                    _streams[i].GetStreamState(out var state).ThrowOnError();

                    if (thisSelected && state == _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED)
                    {
                        thisSelected = false;
                    }
                    else if (!thisSelected && state != _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED)
                    {
                        thisSelected = true;
                    }

                    if (selected != thisSelected)
                    {
                        if (selected)
                        {
                            ps.Object.SelectStream((uint)index).ThrowOnError();
                            queue.Object.QueueEventParamUnk((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MENewStream, Guid.Empty, HRESULTS.S_OK, _streams[index]).ThrowOnError();
                            descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
                            handler.GetCurrentMediaType(out var type).ThrowOnError();
                            _streams[index].Start(type).ThrowOnError();
                        }
                        else
                        {
                            ps.Object.DeselectStream((uint)index).ThrowOnError();
                            _streams[index].Stop().ThrowOnError();
                        }
                    }
                }

                return HRESULTS.S_OK;
            }
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT Stop()
    {
        try
        {
            lock (_lock)
            {
                var queue = _queue;
                var presentationDescriptor = _presentationDescriptor;
                if (queue == null || presentationDescriptor == null)
                {
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                var time = Functions.MFGetSystemTime();
                for (var i = 0; i < _streams.Length; i++)
                {
                    _streams[i].Stop().ThrowOnError();
                    presentationDescriptor.Object.DeselectStream((uint)i).ThrowOnError();
                }

                using var pv = new PropVariant(time);
                queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MESourceStopped, Guid.Empty, HRESULTS.S_OK, pv.Detached).ThrowOnError();
                return HRESULTS.S_OK;
            }
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT Pause() => HRESULTS.MF_E_INVALID_STATE_TRANSITION;

    public HRESULT Shutdown()
    {
        try
        {
            lock (_lock)
            {
                var queue = _queue;
                if (queue == null)
                {
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                queue.Object.Shutdown().ThrowOnError();
                Attributes.Object.DeleteAllItems();
                return HRESULTS.S_OK;
            }
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT GetSourceAttributes(out IMFAttributes attributes)
    {
        attributes = this;
        return HRESULTS.S_OK;
    }

    public HRESULT GetStreamAttributes(uint streamIdentifier, out IMFAttributes attributes)
    {
        lock (_lock)
        {
            if (streamIdentifier >= _streams.Length)
            {
                attributes = null!;
                return HRESULTS.E_FAIL;
            }

            var index = GetStreamIndexById(streamIdentifier);
            if (index < 0)
            {
                attributes = null!;
                return HRESULTS.E_FAIL;
            }

            attributes = _streams[index];
            return HRESULTS.S_OK;
        }
    }

    /// <summary>
    /// No-op by design: unlike the reference sample, we never render into a
    /// D3D texture (we only copy already-decoded NV12 bytes), so a D3D
    /// manager is deliberately never attached to the sample allocator — that
    /// keeps every allocated sample a plain, CPU-lockable memory buffer. See
    /// <see cref="MediaStream.Set3DManager"/>.
    /// </summary>
    public HRESULT SetD3DManager(object manager) => HRESULTS.S_OK;

    public HRESULT SetDefaultAllocator(uint outputStreamID, object allocator)
    {
        lock (_lock)
        {
            if (outputStreamID >= _streams.Length)
            {
                return HRESULTS.E_FAIL;
            }

            var index = GetStreamIndexById(outputStreamID);
            if (index < 0)
            {
                return HRESULTS.E_FAIL;
            }

            return _streams[index].SetAllocator(allocator);
        }
    }

    public HRESULT GetAllocatorUsage(uint outputStreamID, out uint inputStreamID, out MFSampleAllocatorUsage usage)
    {
        lock (_lock)
        {
            if (outputStreamID >= _streams.Length)
            {
                inputStreamID = 0;
                usage = 0;
                return HRESULTS.E_FAIL;
            }

            var index = GetStreamIndexById(outputStreamID);
            if (index < 0)
            {
                inputStreamID = 0;
                usage = 0;
                return HRESULTS.E_FAIL;
            }

            inputStreamID = outputStreamID;
            usage = MediaStream.GetAllocatorUsage();
            return HRESULTS.S_OK;
        }
    }

    public HRESULT GetService(Guid guidService, Guid riid, out object ppv)
    {
        ppv = null!;
        return HRESULTS.E_NOINTERFACE;
    }

    public HRESULT KsProperty(ref KSIDENTIFIER property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ErrorSetNotFound);
    }

    public HRESULT KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ErrorSetNotFound);
    }

    public HRESULT KsEvent(ref KSIDENTIFIER evt, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ErrorSetNotFound);
    }

    protected override void Dispose(bool disposing)
    {
        Shutdown();
        Interlocked.Exchange(ref _presentationDescriptor!, null)?.Dispose();
        Interlocked.Exchange(ref _queue!, null)?.Dispose();
        foreach (var stream in _streams)
        {
            stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
