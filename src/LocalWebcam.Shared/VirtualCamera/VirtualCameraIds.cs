namespace LocalWebcam.Shared.VirtualCamera;

/// <summary>
/// The CLSID of <c>LocalWebcam.VirtualCamera.Windows.Activator</c>, the COM
/// class factory the Windows Frame Server loads to create our virtual
/// camera's media source. Shared between two projects that must agree on the
/// exact same value: <c>MFCreateVirtualCamera</c>'s <c>sourceId</c> parameter
/// (called from <c>LocalWebcam.Windows</c>) and the Activator's
/// <c>[Guid(...)]</c> attribute (compiled into
/// <c>LocalWebcam.VirtualCamera.Windows</c>).
/// </summary>
public static class VirtualCameraIds
{
    /// <summary>No braces — this is the exact string format <c>[Guid(...)]</c> expects.</summary>
    public const string ActivatorClsid = "f813c6d7-55ee-4237-b958-ce4205212874";

    /// <summary>Registry/COM string form (with braces) — this is what <c>MFCreateVirtualCamera</c>'s <c>sourceId</c> expects.</summary>
    public const string ActivatorClsidRegistryFormat = "{" + ActivatorClsid + "}";
}
