# Build, deploy the plugin, then start Agent and App.
param([string]$Config = "Debug")
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

Write-Host "Building..." -ForegroundColor Cyan
Get-Process DeviceHub.Agent, DeviceHub.PluginHost, DeviceHub.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet build (Join-Path $repo "DeviceHub.slnx") -c $Config --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "Deploying plugin..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "deploy-plugin.ps1") -Config $Config

$agent = Join-Path $repo "src\DeviceHub.Agent\bin\x64\$Config\net10.0-windows10.0.26100.0\win-x64\DeviceHub.Agent.exe"
$app   = Join-Path $repo "src\DeviceHub.App\bin\x64\$Config\net10.0-windows10.0.26100.0\win-x64\DeviceHub.App.exe"

Get-Process DeviceHub.Agent, DeviceHub.PluginHost, DeviceHub.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Starting Agent..." -ForegroundColor Cyan
Start-Process $agent
Start-Sleep 2
Write-Host "Starting App..." -ForegroundColor Cyan
Start-Process $app
Write-Host "Done." -ForegroundColor Green
