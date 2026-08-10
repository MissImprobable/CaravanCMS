<#
.SYNOPSIS
  Creates the self-signed HTTPS certificate CaravanCMS needs for the Outlook add-in, and
  exports both the .pfx (for the API server) and the .cer (to distribute to office PCs).

.DESCRIPTION
  Run this ONCE, on the machine that runs CaravanCMS.Api. No admin rights needed — the
  certificate is created in your user profile, exported, then removed from there.

  Outlook (classic desktop) trusts a self-signed certificate as long as it's installed in
  Windows' Trusted Root store on the machine reading email — no domain, DNS, or internet
  access required. This script issues that certificate for the API server's LAN address.

.PARAMETER Address
  The hostname or IP address staff's Outlook will use to reach this API, e.g. 192.168.1.50
  or caravancms.local. Use a STATIC address — if it changes later, mail clients will start
  rejecting the certificate and you'll need to regenerate and redistribute it.

.PARAMETER PfxPassword
  Password protecting the private key in the exported .pfx. Put the same value in
  appsettings.Production.json under Kestrel:Endpoints:Https:Certificate:Password.

.EXAMPLE
  .\New-CaravanCmsCertificate.ps1 -Address 192.168.1.50 -PfxPassword "correct horse battery staple"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Address,

    [Parameter(Mandatory = $true)]
    [string]$PfxPassword,

    [int]$ValidYears = 5
)

$ErrorActionPreference = "Stop"
$outputDir = $PSScriptRoot

Write-Host "Creating self-signed certificate for '$Address' (valid $ValidYears years)..." -ForegroundColor Cyan

# Chromium-based renderers (incl. WebView2, which Outlook's task pane uses) require an
# IP Address-typed SAN when connecting to a literal IP — a DNS Name SAN that merely
# contains the IP as text is rejected ("not signed by a valid security certificate"),
# even though older engines tolerate it. -DnsName alone always produces a DNS Name SAN,
# so IPs need an explicit -TextExtension instead.
[ipaddress]$parsedIp = $null
$isIpAddress = [ipaddress]::TryParse($Address, [ref]$parsedIp)

$certParams = @{
    CertStoreLocation = "Cert:\CurrentUser\My"
    NotAfter          = (Get-Date).AddYears($ValidYears)
    FriendlyName      = "CaravanCMS ($Address)"
    KeyExportPolicy   = "Exportable"
    KeyUsage          = "DigitalSignature", "KeyEncipherment"
    Type              = "SSLServerAuthentication"
}

if ($isIpAddress) {
    $certParams["Subject"] = "CN=$Address"
    $certParams["TextExtension"] = @("2.5.29.17={text}IPAddress=$Address")
} else {
    $certParams["DnsName"] = $Address
}

$cert = New-SelfSignedCertificate @certParams

$pfxPath = Join-Path $outputDir "caravancms.pfx"
$cerPath = Join-Path $outputDir "caravancms.cer"
$securePassword = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

# The private-key copy in the certificate store isn't needed once it's exported to the
# .pfx that Kestrel will load — remove it so there's only one place the key lives.
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Server certificate (keep on this machine only): $pfxPath"
Write-Host "  Client trust file  (distribute to office PCs):   $cerPath"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. In appsettings.Production.json, set:"
Write-Host "       Kestrel:Endpoints:Https:Url = https://0.0.0.0:61655"
Write-Host "       Kestrel:Endpoints:Https:Certificate:Path = certs/caravancms.pfx"
Write-Host "       Kestrel:Endpoints:Https:Certificate:Password = <the password you just used>"
Write-Host "       CaravanCMS:PublicBaseUrl = https://${Address}:61655"
Write-Host "  2. Copy caravancms.cer to every office PC running Outlook."
Write-Host "  3. On each of those PCs, run Install-CaravanCmsTrust.ps1 as Administrator,"
Write-Host "     pointing it at the copy of caravancms.cer on that machine."
Write-Host "  4. Restart CaravanCMS.Api, then browse to https://${Address}:61655/health to confirm."
