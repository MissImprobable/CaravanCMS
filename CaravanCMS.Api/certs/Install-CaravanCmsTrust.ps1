<#
.SYNOPSIS
  Trusts the CaravanCMS self-signed certificate on this PC, so desktop Outlook (and the
  Admin/Client apps, if you switch them to HTTPS too) stop warning about it.

.DESCRIPTION
  Run this ONCE on every office PC that uses the CaravanCMS Outlook add-in. It only needs
  the public certificate file (caravancms.cer) — never send the .pfx anywhere, that file
  holds the private key and stays on the server only.

.PARAMETER CerPath
  Path to the caravancms.cer file (e.g. a copy on a USB stick or a shared drive).

.PARAMETER Scope
  LocalMachine (default) trusts it for every Windows account on this PC, but requires
  running this script as Administrator.
  CurrentUser trusts it only for the account running this script, and needs no admin
  rights — use this for a quick single-user test, but switch to LocalMachine for the real
  office rollout so it isn't tied to one login.

.EXAMPLE
  .\Install-CaravanCmsTrust.ps1 -CerPath "\\server\share\caravancms.cer"

.EXAMPLE
  .\Install-CaravanCmsTrust.ps1 -CerPath ".\caravancms.cer" -Scope CurrentUser
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CerPath,

    [ValidateSet("LocalMachine", "CurrentUser")]
    [string]$Scope = "LocalMachine"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $CerPath)) {
    throw "Certificate file not found: $CerPath"
}

# certutil, not Import-Certificate: adding to the Root store via Import-Certificate pops an
# interactive Windows security confirmation dialog, which fails with "UI is not allowed in
# this operation" under any non-interactive session (remote scripts, RMM tools, etc.).
# certutil performs the same import silently.
Write-Host "Importing $CerPath into $Scope\Root..." -ForegroundColor Cyan
$certutilArgs = if ($Scope -eq "CurrentUser") { @("-user", "-addstore", "Root", $CerPath) } else { @("-addstore", "Root", $CerPath) }
$output = & certutil @certutilArgs
if ($LASTEXITCODE -ne 0) {
    throw "certutil failed:`n$output"
}

Write-Host "Done. Restart Outlook on this PC for the change to take effect." -ForegroundColor Green
if ($Scope -eq "CurrentUser") {
    Write-Host "Note: this only trusts the cert for your Windows account. Re-run with -Scope LocalMachine (as Administrator) before rolling this out to other staff on shared PCs." -ForegroundColor Yellow
}
