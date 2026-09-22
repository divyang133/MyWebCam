<#
.SYNOPSIS
    Authenticode-signs one or more files with the project's code-signing
    certificate.

.DESCRIPTION
    Uses build/signing/LocalWebcamSelfSigned.pfx by default - a self-signed
    certificate created for local/internal use (see
    docs/CODE_SIGNING.md for what this does and does not achieve). To switch
    to a real CA-issued certificate later, drop its .pfx in the same location
    (or pass -PfxPath) and its password via -PfxPassword/build/signing's
    .pfx.password file; no other change is needed.

.PARAMETER Path
    One or more files to sign (exe, dll, msi).

.PARAMETER PfxPath
    Path to the signing certificate. Defaults to the project's
    build/signing/LocalWebcamSelfSigned.pfx.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server - keeps the signature valid after the
    certificate itself expires. DigiCert's is free and doesn't require
    owning a DigiCert certificate.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$Path,

    [string]$PfxPath = (Join-Path $PSScriptRoot "..\build\signing\LocalWebcamSelfSigned.pfx"),

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PfxPath)) {
    throw "Signing certificate not found at '$PfxPath'. Run scripts/New-SigningCertificate.ps1 first, or pass -PfxPath to an existing one."
}

$passwordFile = "$PfxPath.password"
if (-not (Test-Path $passwordFile)) {
    throw "Expected a password file next to the certificate at '$passwordFile'."
}
$pfxPassword = Get-Content -Path $passwordFile -Raw

$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\x64\*" } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    throw "signtool.exe not found under the Windows 10 SDK (Program Files (x86)\Windows Kits\10\bin). Install the Windows SDK's signing tools component."
}

foreach ($file in $Path) {
    if (-not (Test-Path $file)) {
        Write-Warning "Skipping '$file' - not found."
        continue
    }

    Write-Host "Signing $file"
    & $signtool sign /f $PfxPath /p $pfxPassword /fd SHA256 /tr $TimestampUrl /td SHA256 /d "Local Webcam" $file
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed (exit $LASTEXITCODE) for '$file'."
    }
}
