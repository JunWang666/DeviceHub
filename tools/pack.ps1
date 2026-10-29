# Package DeviceHub for distribution. ASCII-only (Windows PowerShell 5.1 reads scripts as ANSI).
#   dist\app\                              self-contained App + Agent + PluginHost, plugins\*.dhplugin bundled
#   dist\<plugin-id>-<version>.dhplugin     standalone plugin packages (install via Plugins > Install plugin)
#   dist\DeviceHub-<ver>-win-x64.zip        portable zip of dist\app
#   dist\DeviceHub-Setup-<ver>-win-x64.exe  per-user installer (needs Inno Setup 6)
#   dist\DeviceHub-<ver>-win-x64.msix       MSIX package, signed with a self-signed cert
#   dist\DeviceHub-Dev.cer                  public cert to trust before installing the MSIX
param([string]$Version = "0.1.0", [string]$Config = "Release")

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo "dist"
$app  = Join-Path $dist "app"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Run($exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "$exe failed ($LASTEXITCODE)" }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null

# 1) App + Agent + PluginHost, self-contained, into one folder (Agent finds PluginHost next to itself).
foreach ($proj in "DeviceHub.Agent", "DeviceHub.PluginHost", "DeviceHub.App") {
    Write-Host "Publishing $proj ..." -ForegroundColor Cyan
    Run dotnet @("publish", (Join-Path $repo "src\$proj\$proj.csproj"), "-c", $Config, "-r", "win-x64",
        "--self-contained", "true", "-p:Platform=x64", "-p:InformationalVersion=$Version", "-p:FileVersion=$Version.0", "-o", $app, "--nologo", "-v", "q")
}

# 2) Plugins -> .dhplugin (zip: manifest.json + backend\ + ui\), layout identical to the plugin store.
function Pack-Plugin($backendProj, $uiProj) {
    Write-Host "Packing $backendProj ..." -ForegroundColor Cyan
    foreach ($p in $backendProj, $uiProj) {
        Run dotnet @("build", (Join-Path $repo "plugins\$p\$p.csproj"), "-c", $Config, "-p:Platform=x64", "--nologo", "-v", "q")
    }
    $manifestPath = Join-Path $repo "plugins\$backendProj\plugin.json"
    $m = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $backendBin = Join-Path $repo "plugins\$backendProj\bin\x64\$Config\net10.0"
    $uiBin      = Join-Path $repo "plugins\$uiProj\bin\x64\$Config\net10.0-windows10.0.26100.0\win-x64"

    $stage = Join-Path $dist ".stage\$($m.id)"
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "backend"), (Join-Path $stage "ui") | Out-Null
    Copy-Item $manifestPath (Join-Path $stage "manifest.json")
    Copy-Item "$backendBin\$backendProj.dll" (Join-Path $stage "backend")
    if (Test-Path "$backendBin\$backendProj.deps.json") { Copy-Item "$backendBin\$backendProj.deps.json" (Join-Path $stage "backend") }
    Copy-Item "$uiBin\$uiProj.dll" (Join-Path $stage "ui")
    Copy-Item "$uiBin\$uiProj.pri" (Join-Path $stage "ui")
    if (Test-Path "$uiBin\$uiProj") { Copy-Item "$uiBin\$uiProj" (Join-Path $stage "ui") -Recurse }

    $pkg = Join-Path $dist "$($m.id)-$($m.version).dhplugin"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $pkg)
    New-Item -ItemType Directory -Force -Path (Join-Path $app "plugins") | Out-Null
    Copy-Item $pkg (Join-Path $app "plugins")
    Write-Host "  -> $pkg"
}
Pack-Plugin "DeviceHub.Plugin.Gk65" "DeviceHub.Plugin.Gk65.Ui"
Pack-Plugin "DeviceHub.Plugin.AirPods" "DeviceHub.Plugin.AirPods.Ui"
Pack-Plugin "DeviceHub.Plugin.GameSir" "DeviceHub.Plugin.GameSir.Ui"
Pack-Plugin "DeviceHub.Plugin.Logitech" "DeviceHub.Plugin.Logitech.Ui"
Remove-Item (Join-Path $dist ".stage") -Recurse -Force

# 3) Portable zip
$zip = Join-Path $dist "DeviceHub-$Version-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip)
Write-Host "Portable: $zip"

# 4) Installer
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Run $iscc @("/Q", "/DAppVersion=$Version", "/DSourceDir=$app", (Join-Path $repo "installer\DeviceHub.iss"))
    Write-Host "Installer: $(Join-Path $dist "DeviceHub-Setup-$Version-win-x64.exe")"
} else {
    Write-Host "Inno Setup 6 not found; skipped installer." -ForegroundColor Yellow
}

# 5) MSIX: packaged App build + Agent/PluginHost/bundled plugins from dist\app, self-signed.
#    The publisher in src\DeviceHub.App\Package.appxmanifest must match the certificate subject.
$sdkBin = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdkBin) { Write-Host "makeappx not found; skipped MSIX." -ForegroundColor Yellow; Write-Host "done." -ForegroundColor Green; return }
$makeappx = $sdkBin.FullName
$signtool = Join-Path $sdkBin.DirectoryName "signtool.exe"

Write-Host "Building MSIX layout ..." -ForegroundColor Cyan
$layout = Join-Path $dist ".msix-layout"
Run dotnet @("build", (Join-Path $repo "src\DeviceHub.App\DeviceHub.App.csproj"), "-c", $Config, "-p:Platform=x64",
    "-p:WindowsPackageType=MSIX", "-p:SelfContained=true", "-p:AppxPackageSigningEnabled=false",
    "-p:InformationalVersion=$Version", "-p:FileVersion=$Version.0", "-p:OutDir=$layout\", "--nologo", "-v", "q")
# Agent, PluginHost (and their runtime files) + plugins\*.dhplugin; never overwrite the packaged App's own files.
Get-ChildItem $app -Recurse -File | ForEach-Object {
    $dst = Join-Path $layout $_.FullName.Substring($app.Length + 1)
    if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null; Copy-Item $_.FullName $dst }
}
$manifest = Join-Path $layout "AppxManifest.xml"
$xml = [IO.File]::ReadAllText($manifest)
$xml = [regex]::Replace($xml, '(<Identity\b[^>]*\bVersion=")[^"]*(")', "`${1}$Version.0`${2}")
[IO.File]::WriteAllText($manifest, $xml, (New-Object Text.UTF8Encoding($false)))
$publisher = [regex]::Match($xml, '<Identity\b[^>]*\bPublisher="([^"]*)"').Groups[1].Value

$msix = Join-Path $dist "DeviceHub-$Version-win-x64.msix"
Run $makeappx @("pack", "/d", $layout, "/p", $msix, "/o", "/nv")

# Self-signed code-signing cert in CurrentUser\My (created once, reused). Only the public .cer is exported;
# trusting it (Local Machine > Trusted People) is left to the user.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed certificate $publisher ..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "DeviceHub package signing (dev)" `
        -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}
Run $signtool @("sign", "/fd", "SHA256", "/sha1", $cert.Thumbprint, "/s", "My", $msix)
$cer = Join-Path $dist "DeviceHub-Dev.cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host "MSIX: $msix"
Write-Host "Certificate (import into Local Machine > Trusted People before installing the MSIX): $cer"
Remove-Item $layout -Recurse -Force

Write-Host "done." -ForegroundColor Green
