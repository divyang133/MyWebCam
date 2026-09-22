<#
.SYNOPSIS
    Undoes Register-VirtualCamera.ps1: unregisters the LocalWebcam virtual
    camera's COM media source.

.DESCRIPTION
    Useful when uninstalling, or when re-registering a newly rebuilt DLL at
    a different path (regsvr32 doesn't overwrite a stale registration at a
    different location by itself). Requires administrator rights and will
    re-launch itself elevated if needed - see Register-VirtualCamera.ps1.

.PARAMETER Path
    Optional explicit path to LocalWebcam.VirtualCamera.Windows.comhost.dll.
    If omitted, searches this repo's build output the same way
    Register-VirtualCamera.ps1 does.
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
    Write-Host "Administrator rights are required to unregister the COM server. Relaunching elevated..." -ForegroundColor Yellow
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
    $candidates = Get-ChildItem -Path $repoRoot -Recurse -Filter 'LocalWebcam.VirtualCamera.Windows.comhost.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        Sort-Object LastWriteTime -Descending

    if (-not $candidates) {
        throw "Could not find LocalWebcam.VirtualCamera.Windows.comhost.dll anywhere under $repoRoot. Pass -Path explicitly if it was already deleted."
    }

    $Path = $candidates[0].FullName
    Write-Host "Using: $Path" -ForegroundColor Cyan
}

Write-Host "Unregistering the virtual camera COM server..." -ForegroundColor Cyan
$process = Start-Process -FilePath 'regsvr32.exe' -ArgumentList @('/s', '/u', "`"$Path`"") -PassThru -Wait
if ($process.ExitCode -ne 0) {
    throw "regsvr32 exited with code $($process.ExitCode)."
}

Write-Host "Unregistered." -ForegroundColor Green
