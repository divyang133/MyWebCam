<#
.SYNOPSIS
    Publishes LocalWebcam.Desktop self-contained, signs it, and zips it up -
    a portable "extract and run" distribution that needs no installer at all.

.DESCRIPTION
    Exists specifically for environments where Windows Installer packages
    (.msi) are blocked by policy (e.g. a corporate-managed machine) but
    running a plain .exe is not - confirmed on real hardware that this is a
    real, common split: the app itself (self-elevating its own one-time COM
    registration via a UAC prompt) was never blocked, only the MSI was.

    A literal single .exe was tried first (`dotnet publish -p:PublishSingleFile=true`)
    and works for launching + auto-registration, but introduces two real
    problems found on real hardware: (1) self-extraction to a temp cache on
    every launch of a new build adds real startup latency and disk I/O
    contention, timing-sensitive enough that it was observed causing the
    virtual camera's Windows Camera app consumer to see a stale/placeholder
    frame; (2) that in no way helps with the actual problem (an MSI-specific
    policy block) since the regular multi-file build was never blocked
    either. A ZIP of the regular publish avoids both: same proven-working
    binary layout used throughout this project's own testing, packaged for
    easy one-time extraction instead of an installer.

.PARAMETER SkipSigning
    Build without signing.
#>
[CmdletBinding()]
param(
    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "publish\LocalWebcam.Desktop"
$zipPath = Join-Path $repoRoot "publish\LocalWebcam-portable.zip"

Write-Host "== Publishing LocalWebcam.Desktop (self-contained, win-x64) ==" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
dotnet publish (Join-Path $repoRoot "src\LocalWebcam.Desktop\LocalWebcam.Desktop.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# The Windows App SDK's self-contained mode unconditionally copies its whole
# native runtime payload, including several large components this app never
# uses (Windows AI/ONNX inference, Windows Search indexing, and a full
# WPF+WinForms interop stack) - confirmed via `deps.json`: none of these are
# in the managed dependency-resolution graph, they're just bulk-copied
# bystanders (a known, unresolved Windows App SDK limitation - there's no
# supported opt-out: https://github.com/dotnet/sdk/issues/27336).
$unusedNativeComponents = @(
    "onnxruntime.dll", "DirectML.dll",
    "Microsoft.Windows.Search.dll", "Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll",
    "Microsoft.Windows.Widgets.dll", "PerceptiveStreaming.dll",
    "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll",
    "ReachFramework.dll", "wpfgfx_cor3.dll",
    "System.Windows.Forms.dll", "System.Windows.Forms.Design.dll", "System.Windows.Forms.Primitives.dll"
)
Write-Host "== Trimming unused Windows App SDK components ==" -ForegroundColor Cyan
foreach ($name in $unusedNativeComponents) {
    $path = Join-Path $publishDir $name
    if (Test-Path $path) {
        Remove-Item -Path $path -Force
    }
}

# This app is US-English only. SatelliteResourceLanguages=en-US in the
# .csproj already drops the *managed* WPF/WinForms satellite-resource
# folders (bare culture codes like `de`, `ja`) - a genuinely different,
# SDK-supported mechanism. What's left is the native Windows App SDK
# runtime's own per-locale MUI folders (region-qualified codes like de-DE,
# fr-FR, ja-JP), holding Microsoft.ui.xaml.dll.mui/Microsoft.UI.Xaml.Phone.dll.mui
# - the framework's OWN fallback strings, not app localization, and it needs
# exactly one of them: `en-us`, matching this app's only supported UI
# language. An earlier version of this project's installer script deleted
# *all* of these, `en-us` included, and crashed the app on real hardware
# (0xc000027b in Microsoft.ui.xaml.dll, ERROR_MUI_FILE_NOT_LOADED) the
# instant it needed a native WinUI resource - it never was safe to remove
# the one the framework actually falls back to. Keeping `en-us` and
# removing only the ~85 others avoids that: verified by launching the
# trimmed output, connecting a real phone over USB, and confirming actual
# video renders in both the desktop preview and the virtual camera (the
# exact scenario that crashed before), not just that the app starts.
Write-Host "== Trimming non-English native resource folders ==" -ForegroundColor Cyan
Get-ChildItem -Path $publishDir -Directory | Where-Object {
    # {2,3}: BCP-47 primary language subtags are usually 2 letters (de, ja)
    # but some are 3 (fil - Filipino, kok - Konkani, quz - Cusco Quechua) -
    # missing this the first time around left those three undeleted.
    $_.Name -match '^[a-z]{2,3}(-[A-Za-z]+)*$' -and $_.Name -ne 'en-us'
} | ForEach-Object {
    Remove-Item -Path $_.FullName -Recurse -Force
}

if (-not $SkipSigning) {
    Write-Host "== Signing LocalWebcam binaries ==" -ForegroundColor Cyan
    $ownBinaries = Get-ChildItem -Path $publishDir -Filter "LocalWebcam.*" |
        Where-Object { $_.Extension -in ".dll", ".exe" } |
        Select-Object -ExpandProperty FullName
    & (Join-Path $PSScriptRoot "Sign-Binaries.ps1") -Path $ownBinaries
}

Write-Host "== Zipping ==" -ForegroundColor Cyan
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "== Done: $zipPath ==" -ForegroundColor Green
Write-Host "Extract it anywhere and run LocalWebcam.Desktop.exe - no installer, no admin rights needed to run it (only a one-time UAC prompt the app itself shows, for its virtual camera's COM registration)." -ForegroundColor Green
