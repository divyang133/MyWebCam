using System.Runtime.InteropServices;

namespace LocalWebcam.Windows.Video;

/// <summary>
/// Minimal hand-rolled declaration of the classic COM <c>ICodecAPI</c>
/// interface (icodecapi.h) - not wrapped by Vortice.MediaFoundation at all.
/// Needed for exactly one call (<see cref="SetValue"/>, to set
/// <c>CODECAPI_AVLowLatencyMode</c>): setting the identically-GUID'd
/// <c>MF_LOW_LATENCY</c> via <c>IMFAttributes</c> (already done elsewhere in
/// <c>WindowsVideoDecoder</c>) reliably works for software decode, but many
/// hardware/DXVA decoder implementations only actually check this setting
/// via <c>ICodecAPI</c>, not the generic attribute store - confirmed on real
/// hardware as ~584ms of decode+render latency with hardware decode enabled
/// (versus the near-zero latency MF_LOW_LATENCY achieved for software
/// decode), consistent with this exact, documented gap.
///
/// Vtable order below matches icodecapi.h's ICodecAPIVtbl exactly (verified
/// against the actual header, not from memory) up through
/// <see cref="SetValue"/> - the only method this app calls. COM interop
/// only requires declared methods to match the *real* interface's order for
/// the ones actually declared; methods after the last one this app calls
/// don't need to be declared at all.
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApi
{
    [PreserveSig]
    int IsSupported(in Guid api);

    [PreserveSig]
    int IsModifiable(in Guid api);

    [PreserveSig]
    int GetParameterRange(in Guid api, out IntPtr valueMin, out IntPtr valueMax, out IntPtr steppingDelta);

    [PreserveSig]
    int GetParameterValues(in Guid api, out IntPtr values, out uint valuesCount);

    [PreserveSig]
    int GetDefaultValue(in Guid api, out IntPtr value);

    [PreserveSig]
    int GetValue(in Guid api, out IntPtr value);

    /// <summary>
    /// <paramref name="value"/> is a raw pointer to a native VARIANT
    /// (allocated and populated via <c>Marshal.GetNativeVariantForObject</c>
    /// - see the caller), not a typed managed parameter. The newer
    /// <c>System.Runtime.InteropServices.Marshalling.ComVariant</c> type was
    /// tried first for this and consistently produced E_INVALIDARG from a
    /// real decoder's SetValue despite QueryInterface for ICodecAPI itself
    /// succeeding and the GUID being accepted - switched to this
    /// unambiguous, decades-proven marshaling path instead of spending
    /// further time on why ComVariant's layout didn't satisfy this specific
    /// native callee.
    /// </summary>
    [PreserveSig]
    int SetValue(in Guid api, IntPtr value);
}
