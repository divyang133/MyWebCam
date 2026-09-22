<#
.SYNOPSIS
    Creates a self-signed code-signing certificate for local/internal use and
    exports it to build/signing/.

.DESCRIPTION
    See docs/CODE_SIGNING.md before running this - a self-signed certificate
    only removes SmartScreen/antivirus warnings on machines where it has been
    explicitly trusted (via Import-TrustedPublisherCert.ps1). It does NOT
    achieve warning-free distribution to the public; that requires a
    CA-issued certificate (see the same doc for options).

    Safe to re-run: if a certificate with the same Subject already exists in
    the current user's store, it's reused rather than duplicated.
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=LocalWebcam Publisher, O=LocalWebcam",
    [int]$ValidYears = 5,
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\build\signing"),
    [string]$Name = "LocalWebcamSelfSigned"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
if ($existing) {
    Write-Host "Reusing existing certificate $($existing.Thumbprint)"
    $cert = $existing
}
else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -FriendlyName "LocalWebcam Code Signing" `
        -NotAfter (Get-Date).AddYears($ValidYears)
    Write-Host "Created certificate $($cert.Thumbprint)"
}

$pfxPath = Join-Path $OutputDir "$Name.pfx"
$cerPath = Join-Path $OutputDir "$Name.cer"
$passwordPath = "$pfxPath.password"

$rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
$bytes = New-Object byte[] 24
$rng.GetBytes($bytes)
$password = [System.Convert]::ToBase64String($bytes)
$securePassword = ConvertTo-SecureString -String $password -Force -AsPlainText

Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $securePassword | Out-Null
Set-Content -Path $passwordPath -Value $password -NoNewline
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cerPath | Out-Null

Write-Host "Exported:"
Write-Host "  $pfxPath (private key, used for signing - never commit)"
Write-Host "  $cerPath (public certificate - distribute this to machines that should trust the signature)"
Write-Host ""
Write-Host "To stop seeing warnings on THIS machine for binaries signed with this cert, run:"
Write-Host "  scripts\Import-TrustedPublisherCert.ps1"
