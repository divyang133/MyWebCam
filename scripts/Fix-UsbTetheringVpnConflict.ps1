<#
.SYNOPSIS
    Stops the phone's USB-tethering (RNDIS) network adapter from competing
    with a VPN for general internet/DNS traffic, while leaving it usable for
    LocalWebcam's direct phone<->PC communication on its own subnet.

.DESCRIPTION
    Confirmed on real hardware: enabling the RNDIS adapter accepts a DHCP
    offer from the phone that includes (1) a default route (0.0.0.0/0) at
    metric 0, and (2) the phone's own IP as the adapter's sole DNS server -
    combined with Windows' automatic interface metric ranking this adapter
    (25) *below* (i.e. more preferred than) the Wi-Fi adapter (35), general
    internet/DNS traffic gets silently routed through the phone instead of
    the VPN-protected Wi-Fi path, breaking VPN-gated resources even though
    the VPN tunnel itself stays connected.

    This does NOT touch the VPN, Wi-Fi, or any routing for other adapters -
    it only raises this one adapter's metric above Wi-Fi's and stops it
    being used for DNS. The direct on-link route to the phone's own subnet
    is unaffected (that's a same-subnet route with no competing
    alternative, so it isn't influenced by this change) - LocalWebcam's USB
    discovery/streaming keeps working.

    Fully reversible: run with -Undo to restore automatic metric and DNS.

.PARAMETER AdapterName
    The network adapter's name as Windows shows it. Defaults to "Ethernet"
    (what Windows names the phone's RNDIS adapter on this machine).

.PARAMETER Undo
    Restores automatic metric and automatic DNS on the adapter.
#>
[CmdletBinding()]
param(
    [string]$AdapterName = "Ethernet",
    [switch]$Undo
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Host "Administrator rights are required to change adapter metric/DNS. Relaunching elevated - please approve the prompt promptly, it can disappear if you're not watching for it." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"", '-AdapterName', "`"$AdapterName`"")
    if ($Undo) { $argList += '-Undo' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
    exit $LASTEXITCODE
}

if ($Undo) {
    Write-Host "Restoring automatic metric and DNS on '$AdapterName'..." -ForegroundColor Cyan
    Set-NetIPInterface -InterfaceAlias $AdapterName -AutomaticMetric Enabled -AddressFamily IPv4
    Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ResetServerAddresses
    Write-Host "Done." -ForegroundColor Green
    exit 0
}

Write-Host "Setting '$AdapterName' to a low-priority metric (above Wi-Fi's) and overriding its DNS to match Wi-Fi's..." -ForegroundColor Cyan
Set-NetIPInterface -InterfaceAlias $AdapterName -AutomaticMetric Disabled -InterfaceMetric 9999 -AddressFamily IPv4

# "addr=none" does NOT stick while DHCP stays enabled on the adapter as a
# whole (confirmed: netsh reported success and "show config" still listed
# "DNS servers configured through DHCP: <phone IP>" right after) - DHCP's
# own DNS offer keeps winning over an *empty* static override. A real
# static override DOES take precedence over DHCP's, so point it at
# whatever a real, already-working adapter (Wi-Fi, if up) is using -
# harmless if this adapter's DNS is ever actually queried, since it'll
# just get the same correct answer Wi-Fi would have given.
$fallbackDns = (Get-DnsClientServerAddress -InterfaceAlias "Wi-Fi" -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses | Select-Object -First 1
if (-not $fallbackDns) { $fallbackDns = "1.1.1.1" }

$netshOutput = netsh interface ip set dns name="$AdapterName" source=static addr=$fallbackDns 2>&1
$netshExit = $LASTEXITCODE
Start-Sleep -Milliseconds 500
$verify = (Get-DnsClientServerAddress -InterfaceAlias $AdapterName -AddressFamily IPv4).ServerAddresses
$config = netsh interface ip show config name="$AdapterName" 2>&1
@"
AdapterName param: [$AdapterName]
Fallback DNS used: $fallbackDns
netsh exit code: $netshExit
netsh output: $($netshOutput -join ' | ')
DNS servers after setting: $($verify -join ', ')
--- netsh show config ---
$($config -join "`n")
"@ | Out-File -FilePath "$env:TEMP\usb-vpn-fix-result.txt" -Encoding utf8
Write-Host "Done. '$AdapterName' will only be used for its own directly-connected subnet (the phone) - general internet/DNS/VPN traffic will keep using Wi-Fi." -ForegroundColor Green
Write-Host "To undo: .\Fix-UsbTetheringVpnConflict.ps1 -AdapterName '$AdapterName' -Undo" -ForegroundColor DarkGray
