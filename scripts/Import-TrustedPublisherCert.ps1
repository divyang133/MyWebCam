<#
.SYNOPSIS
    Trusts LocalWebcam's self-signed code-signing certificate on this
    machine, so binaries signed with it stop showing SmartScreen/publisher
    warnings here.

.DESCRIPTION
    This only affects the machine it's run on. It does nothing for anyone
    else who downloads the app - see docs/CODE_SIGNING.md. Requires
    administrator rights (writes to LocalMachine certificate stores).

    Installs the certificate into two LocalMachine stores:
      - Trusted Root Certification Authorities (Root): makes the cert
        itself a trust anchor, since it's self-signed (no real CA vouches
        for it).
      - Trusted Publishers (TrustedPublisher): the actual store Windows
        checks for "do I trust code signed by this identity" prompts.
#>
[CmdletBinding()]
param(
    [string]$CerPath = (Join-Path $PSScriptRoot "..\build\signing\LocalWebcamSelfSigned.cer")
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this from an elevated PowerShell prompt (writes to LocalMachine certificate stores)."
}

if (-not (Test-Path $CerPath)) {
    throw "Certificate not found at '$CerPath'. Run scripts\New-SigningCertificate.ps1 first."
}

Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

Write-Host "Trusted. Binaries signed with this certificate will no longer show a publisher warning on this machine."
