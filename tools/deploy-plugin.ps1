# Deploy plugins (dev build) into the Agent's plugin store.
# ASCII-only on purpose (Windows PowerShell 5.1 reads scripts as ANSI).
# Each plugin owns its manifest: plugins\<BackendProj>\plugin.json (id, version, supported devices and channels).
# It is copied verbatim to <store>\<id>\<version>\manifest.json.
# Plugin UI resources (*.pri, xbf) stay in the store; the App resolves them from there at runtime
# (PluginResourceResolver), so nothing is copied next to the App exe.
param([string]$Config = "Debug")

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$data = Join-Path $env:LOCALAPPDATA "DeviceHub"
$utf8 = New-Object Text.UTF8Encoding($false)

function Deploy-One($backendProj, $uiProj, $version = $null, $displaySuffix = $null) {
    $manifestText = [IO.File]::ReadAllText((Join-Path $repo "plugins\$backendProj\plugin.json"), $utf8)
    $m = $manifestText | ConvertFrom-Json
    $id = $m.id
    if ($version) { $manifestText = $manifestText -replace '"version"\s*:\s*"[^"]*"', ('"version": "' + $version + '"') } else { $version = $m.version }
    if ($displaySuffix) { $manifestText = $manifestText -replace '("displayName"\s*:\s*")([^"]*)"', ('$1$2' + $displaySuffix + '"') }

    $backendBin = Join-Path $repo "plugins\$backendProj\bin\x64\$Config\net10.0"
    $uiBin      = Join-Path $repo "plugins\$uiProj\bin\x64\$Config\net10.0-windows10.0.26100.0\win-x64"
    $verDir     = Join-Path $data "plugins\$id\$version"
    if (Test-Path $verDir) { Remove-Item $verDir -Recurse -Force }
    $backend = Join-Path $verDir "backend"; $ui = Join-Path $verDir "ui"
    New-Item -ItemType Directory -Force -Path $backend, $ui | Out-Null

    Copy-Item "$backendBin\$backendProj.dll" $backend -Force
    if (Test-Path "$backendBin\$backendProj.deps.json") { Copy-Item "$backendBin\$backendProj.deps.json" $backend -Force }

    Copy-Item "$uiBin\$uiProj.dll" $ui -Force
    Copy-Item "$uiBin\$uiProj.pri" $ui -Force
    if (Test-Path "$uiBin\$uiProj") { Copy-Item "$uiBin\$uiProj" $ui -Recurse -Force }

    [IO.File]::WriteAllText((Join-Path $verDir "manifest.json"), $manifestText, $utf8)
    @{ active = $version; pending = $null; previous = $null; disabled = $false } | ConvertTo-Json |
        Set-Content (Join-Path $data "plugins\$id\state.json") -Encoding UTF8
    Write-Host "  deployed $id v$version -> $verDir"
    return $id
}

# GK65 driver plugin
Deploy-One "DeviceHub.Plugin.Gk65" "DeviceHub.Plugin.Gk65.Ui" | Out-Null
# AirPods plugin
Deploy-One "DeviceHub.Plugin.AirPods" "DeviceHub.Plugin.AirPods.Ui" | Out-Null

Write-Host "done. plugin store: $data\plugins"
