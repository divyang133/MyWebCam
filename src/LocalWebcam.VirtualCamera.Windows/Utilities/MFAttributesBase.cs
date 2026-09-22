using DirectN;

namespace LocalWebcam.VirtualCamera.Windows.Utilities;

/// <summary>
/// Base class providing a plain pass-through <c>IMFAttributes</c>
/// implementation backed by a real Media Foundation attribute store
/// (<c>MFCreateAttributes</c>). <c>Activator</c>, <c>MediaSource</c> and
/// <c>MediaStream</c> all need to implement <c>IMFAttributes</c> (the Frame
/// Server queries device/stream attributes directly), so this exists purely
/// to avoid repeating the same ~25-method forwarding boilerplate three times.
/// Adapted from smourier/VCamNetSample's Utilities/MFAttributes.cs (with the
/// per-call ETW tracing removed — that sample logs every single attribute
/// get/set for deep diagnostics, which is more than this project needs).
/// </summary>
public class MFAttributesBase : IMFAttributes, IDisposable
{
    private bool _disposed;

    protected MFAttributesBase()
    {
        Attributes = MFFunctions.MFCreateAttributes();
    }

    public IComObject<IMFAttributes> Attributes { get; }

    public virtual HRESULT GetItem(Guid guidKey, PROPVARIANT pValue) => Attributes.Object.GetItem(guidKey, pValue);

    public virtual HRESULT GetItemType(Guid guidKey, out _MF_ATTRIBUTE_TYPE pType) => Attributes.Object.GetItemType(guidKey, out pType);

    public virtual HRESULT CompareItem(Guid guidKey, PROPVARIANT pValue, out bool pbResult) => Attributes.Object.CompareItem(guidKey, pValue, out pbResult);

    public virtual HRESULT Compare(IMFAttributes pTheirs, _MF_ATTRIBUTES_MATCH_TYPE type, out bool pbResult) => Attributes.Object.Compare(pTheirs, type, out pbResult);

    public virtual HRESULT GetUINT32(Guid guidKey, out uint punValue) => Attributes.Object.GetUINT32(guidKey, out punValue);

    public virtual HRESULT GetUINT64(Guid guidKey, out ulong punValue) => Attributes.Object.GetUINT64(guidKey, out punValue);

    public virtual HRESULT GetDouble(Guid guidKey, out double pfValue) => Attributes.Object.GetDouble(guidKey, out pfValue);

    public virtual HRESULT GetGUID(Guid guidKey, out Guid pguidValue) => Attributes.Object.GetGUID(guidKey, out pguidValue);

    public virtual HRESULT GetStringLength(Guid guidKey, out uint pcchLength) => Attributes.Object.GetStringLength(guidKey, out pcchLength);

    public virtual HRESULT GetString(Guid guidKey, string pwszValue, uint cchBufSize, nint pcchLength) => Attributes.Object.GetString(guidKey, pwszValue, cchBufSize, pcchLength);

    public virtual HRESULT GetAllocatedString(Guid guidKey, nint ppwszValue, out uint pcchLength) => Attributes.Object.GetAllocatedString(guidKey, ppwszValue, out pcchLength);

    public virtual HRESULT GetBlobSize(Guid guidKey, out uint pcbBlobSize) => Attributes.Object.GetBlobSize(guidKey, out pcbBlobSize);

    public virtual HRESULT GetBlob(Guid guidKey, byte[] pBuf, int cbBufSize, nint pcbBlobSize) => Attributes.Object.GetBlob(guidKey, pBuf, cbBufSize, pcbBlobSize);

    public virtual HRESULT GetAllocatedBlob(Guid guidKey, out nint ppBuf, out uint pcbSize) => Attributes.Object.GetAllocatedBlob(guidKey, out ppBuf, out pcbSize);

    public virtual HRESULT GetUnknown(Guid guidKey, Guid riid, out object ppv) => Attributes.Object.GetUnknown(guidKey, riid, out ppv);

    public virtual HRESULT SetItem(Guid guidKey, PROPVARIANT value) => Attributes.Object.SetItem(guidKey, value);

    public virtual HRESULT DeleteItem(Guid guidKey) => Attributes.Object.DeleteItem(guidKey);

    public virtual HRESULT DeleteAllItems() => Attributes.Object.DeleteAllItems();

    public virtual HRESULT SetUINT32(Guid guidKey, uint unValue) => Attributes.Object.SetUINT32(guidKey, unValue);

    public virtual HRESULT SetUINT64(Guid guidKey, ulong unValue) => Attributes.Object.SetUINT64(guidKey, unValue);

    public virtual HRESULT SetDouble(Guid guidKey, double fValue) => Attributes.Object.SetDouble(guidKey, fValue);

    public virtual HRESULT SetGUID(Guid guidKey, Guid guidValue) => Attributes.Object.SetGUID(guidKey, guidValue);

    public virtual HRESULT SetString(Guid guidKey, string wszValue) => Attributes.Object.SetString(guidKey, wszValue);

    public virtual HRESULT SetBlob(Guid guidKey, byte[] pBuf, int cbBufSize) => Attributes.Object.SetBlob(guidKey, pBuf, cbBufSize);

    public virtual HRESULT SetUnknown(Guid guidKey, object pUnknown) => Attributes.Object.SetUnknown(guidKey, pUnknown);

    public virtual HRESULT LockStore() => Attributes.Object.LockStore();

    public virtual HRESULT UnlockStore() => Attributes.Object.UnlockStore();

    public virtual HRESULT GetCount(out uint pcItems) => Attributes.Object.GetCount(out pcItems);

    public virtual HRESULT GetItemByIndex(uint unIndex, out Guid pguidKey, PROPVARIANT pValue) => Attributes.Object.GetItemByIndex(unIndex, out pguidKey, pValue);

    public virtual HRESULT CopyAllItems(IMFAttributes pDest) => Attributes.Object.CopyAllItems(pDest);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Attributes.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
