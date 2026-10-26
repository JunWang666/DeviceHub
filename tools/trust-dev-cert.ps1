# Trust the DeviceHub dev signing certificate so the self-signed MSIX can be installed.
# Imports dist\DeviceHub-Dev.cer into Local Machine > Trusted People (what MSIX sideloading requires)
# and removes copies that ended up in other stores (Root / Intermediate CA). The private key in
# CurrentUser\My is left alone (pack.ps1 signs with it).
# ASCII-only on purpose (Windows PowerShell 5.1 reads scripts as ANSI). Self-elevates (UAC).
param([string]$CerPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist\DeviceHub-Dev.cer"))

$ErrorActionPreference = "Stop"
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -CerPath `"$CerPath`""
    return
}

if (-not (Test-Path $CerPath)) { Write-Host "Certificate not found: $CerPath (run tools\pack.ps1 first)" -ForegroundColor Red; Read-Host "Press Enter to close"; return }
$thumb = (Get-PfxCertificate $CerPath).Thumbprint
Write-Host "Certificate: $CerPath"
Write-Host "Thumbprint : $thumb"

Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Write-Host "Imported into LocalMachine\TrustedPeople" -ForegroundColor Green

foreach ($store in "Cert:\LocalMachine\CA", "Cert:\LocalMachine\Root", "Cert:\CurrentUser\Root", "Cert:\CurrentUser\CA") {
    Get-ChildItem $store | Where-Object Thumbprint -eq $thumb | ForEach-Object {
        Remove-Item $_.PSPath
        Write-Host "Removed misplaced copy from $store"
    }
}

$ok = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $thumb
if ($ok) { Write-Host "Done. The DeviceHub MSIX can now be installed." -ForegroundColor Green }
else { Write-Host "Import did not take effect." -ForegroundColor Red }
Read-Host "Press Enter to close"
