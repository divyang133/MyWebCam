<#
.SYNOPSIS
    One-time setup: registers the LocalWebcam virtual camera's COM media
    source with Windows so the Frame Server can load it.

.DESCRIPTION
    LocalWebcam.VirtualCamera.Windows is built with <EnableComHosting>true</EnableComHosting>,
    producing a .comhost.dll that must be registered via regsvr32 before
    Windows Settings > Cameras / OBS / Teams can see the "Local Webcam"
    device. This is a real, unavoidable one-time step - the Frame Server
    loads the media source from HKLM, not per-user HKCU registration - see
    docs/WINDOWS_VIRTUAL_CAMERA.md for why.

    This script requires administrator rights and will re-launch itself
    elevated (with a UAC prompt) if it isn't already running as admin. It
    only calls regsvr32 on the one DLL described above - nothing else on
    the system is touched.

.PARAMETER Path
    Optional explicit path to LocalWebcam.VirtualCamera.Windows.comhost.dll.
    If omitted, the script searches this repo's build output under
    src\LocalWebcam.Desktop\bin\ and src\LocalWebcam.VirtualCamera.Windows\bin\
    for the most recently built copy and asks for confirmation before using it.

.EXAMPLE
    .\scripts\Register-VirtualCamera.ps1
    Finds the most recently built comhost.dll and registers it.

.EXAMPLE
    .\scripts\Register-VirtualCamera.ps1 -Path 'C:\path\to\LocalWebcam.VirtualCamera.Windows.comhost.dll'
    Registers a specific build (e.g. a Release build or an installed copy).
#>
[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host "Administrator rights are required to register the COM server. Relaunching elevated..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"")
    if ($Path) {
        $argList += @('-Path', "`"$Path`"")
    }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
    exit $LASTEXITCODE
}

if (-not $Path) {
    $repoRoot = Split-Path -Parent $PSScriptRoot

    # Only "bin" output carries the full dependency closure (DirectNCore.dll,
    # LocalWebcam.Shared.dll, etc.) alongside the comhost.dll - "obj" is
    # intermediate build output missing those, and DllRegisterServer (which
    # has to load the managed assembly and its dependencies to enumerate
    # [ComVisible] types) fails with exit code 5 if they're not next to it.
    $candidates = Get-ChildItem -Path $repoRoot -Recurse -Filter 'LocalWebcam.VirtualCamera.Windows.comhost.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        Sort-Object LastWriteTime -Descending

    if (-not $candidates) {
        throw "Could not find a built LocalWebcam.VirtualCamera.Windows.comhost.dll (with its dependencies) under $repoRoot\*\bin\. Build the solution first (dotnet build LocalWebcam.slnx), or pass -Path explicitly."
    }

    $Path = $candidates[0].FullName
    Write-Host "Using most recently built DLL:`n  $Path" -ForegroundColor Cyan
    if ($candidates.Count -gt 1) {
        Write-Host "($($candidates.Count - 1) other build(s) found; pass -Path to pick a different one.)" -ForegroundColor DarkGray
    }
}

if (-not (Test-Path $Path)) {
    throw "File not found: $Path"
}

Write-Host "Registering the virtual camera COM server..." -ForegroundColor Cyan
$process = Start-Process -FilePath 'regsvr32.exe' -ArgumentList @('/s', "`"$Path`"") -PassThru -Wait
if ($process.ExitCode -ne 0) {
    throw "regsvr32 exited with code $($process.ExitCode). Run 'regsvr32 `"$Path`"' (without /s) manually to see the error dialog."
}

Write-Host "Registered successfully." -ForegroundColor Green
Write-Host "Launch LocalWebcam.Desktop.exe (or restart it if already running) - the 'Local Webcam' device should now be selectable in OBS, Teams, or Settings > Cameras." -ForegroundColor Green
