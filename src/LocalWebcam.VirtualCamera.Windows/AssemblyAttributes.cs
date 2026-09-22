using System.Runtime.Versioning;

// GenerateAssemblyInfo is false for this project (EnableComHosting's build
// customizes assembly attribute generation), so this is written by hand.
// Declaring the OS platform here quiets the ~400 CA1416 warnings that would
// otherwise fire on every single Media Foundation call in this assembly -
// they are all guaranteed safe since the whole assembly only ever loads
// inside the Windows Frame Server on Windows 11 22H2+.
[assembly: SupportedOSPlatform("windows10.0.22621.0")]
