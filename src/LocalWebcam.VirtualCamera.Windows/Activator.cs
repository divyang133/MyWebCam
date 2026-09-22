using System.Runtime.InteropServices;
using DirectN;
using LocalWebcam.Shared.VirtualCamera;
using LocalWebcam.VirtualCamera.Windows.Utilities;

namespace LocalWebcam.VirtualCamera.Windows;

/// <summary>
/// COM class factory the Windows Frame Server activates (by
/// <see cref="VirtualCameraIds.ActivatorClsid"/>, passed as
/// <c>MFCreateVirtualCamera</c>'s <c>sourceId</c> from
/// <c>LocalWebcam.Windows.Video.WindowsVirtualCamera</c>) to obtain our
/// <see cref="MediaSource"/>. Adapted from
/// smourier/VCamNetSample's Activator.cs.
/// </summary>
[ComVisible(true), Guid(VirtualCameraIds.ActivatorClsid)]
public sealed class Activator : MFAttributesBase, IMFActivateImpl, ICustomQueryInterface
{
    public Activator()
    {
        EventProvider.LogInfo($"ctor commandLine:{Environment.CommandLine}");
        SetDefaultAttributes(this);
    }

    private static void SetDefaultAttributes(IMFAttributes attributes)
    {
        attributes.SetUINT32(MFConstants.MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1).ThrowOnError();
        attributes.SetGUID(MFConstants.MFT_TRANSFORM_CLSID_Attribute, typeof(Activator).GUID).ThrowOnError();
    }

    public HRESULT ActivateObject(Guid riid, out nint ppv)
    {
        try
        {
            EventProvider.LogInfo($"{riid}");
            if (riid == typeof(IMFMediaSourceEx).GUID || riid == typeof(IMFMediaSource).GUID)
            {
                var source = new MediaSource();
                SetDefaultAttributes(source);
                ppv = ComObject.QueryObjectInterface(source, riid, false);
                if (ppv != IntPtr.Zero)
                {
                    return HRESULTS.S_OK;
                }
            }

            ppv = 0;
            EventProvider.LogInfo($"{riid} => E_NOINTERFACE");
            return HRESULTS.E_NOINTERFACE;
        }
        catch (Exception e)
        {
            EventProvider.LogError(e.ToString());
            throw;
        }
    }

    public HRESULT ShutdownObject()
    {
        EventProvider.LogInfo();
        return HRESULTS.S_OK;
    }

    public HRESULT DetachObject()
    {
        EventProvider.LogInfo();
        return HRESULTS.S_OK;
    }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }

    [ComRegisterFunction]
    public static void RegisterFunction(Type type) => EventProvider.LogInfo("type:" + type);

    [ComUnregisterFunction]
    public static void UnregisterFunction(Type type) => EventProvider.LogInfo("type:" + type);
}
