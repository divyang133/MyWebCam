namespace LocalWebcam.VirtualCamera.Windows.Utilities;

/// <summary>IIDs Media Foundation / the Frame Server queries for via <c>ICustomQueryInterface</c> that we intentionally don't implement.</summary>
internal static class Constants
{
    public const int ErrorSetNotFound = 1170;

    public static readonly Guid IidMFDeviceSourceInternal = new("7f02a37e-4e81-11e0-8f3e-d057dfd72085");
    public static readonly Guid IidMFDeviceSourceStatus = new("43937DC1-0BE6-4ADD-8A14-9EA68FF31252");
    public static readonly Guid IidMFDeviceController = new("A1F58958-A5AA-412F-AF20-1B7F1242DBA0");
    public static readonly Guid IidMFDeviceController2 = new("2032C7EF-76F6-492A-94F3-4A81F69380CC");
}
