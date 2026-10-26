# Passive ETW capture of HID traffic to the GK65 receiver (VID_1A2C) while the
# official software sends a command. Admin required (self-elevates). Capture only
# reads existing traffic; it does not write to the device.
param([string]$Label = "action")

$ErrorActionPreference = "Stop"

# self-elevate
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Label `"$Label`""
    return
}

$out = Join-Path $env:TEMP "gk65cap"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$etl = Join-Path $out "$Label.etl"
$xml = Join-Path $out "$Label.xml"

logman stop gk65cap -ets 2>$null | Out-Null
# Microsoft-Windows-Input-HIDCLASS
logman create trace gk65cap -p "{6465DA78-E7A0-4F39-B084-8F53C7C30DC6}" 0xffffffffffffffff 0xff -o $etl -ets | Out-Null

Write-Host "`n>>> 抓包已开始。现在到官方软件 (ShinetekTools) 里做【一个】操作：$Label" -ForegroundColor Yellow
Write-Host ">>> 例如把 DPI 改一档、或切一个灯效。做完回到本窗口。" -ForegroundColor Yellow
Read-Host ">>> 完成后按 Enter 停止抓包"

logman stop gk65cap -ets | Out-Null
tracerpt $etl -o $xml -of XML -y | Out-Null

# dump any byte-array payloads associated with the 1a2c device
Write-Host "`n=== 解析结果（含数据的 HID 事件）===" -ForegroundColor Cyan
[xml]$doc = Get-Content $xml -Raw
$count = 0
foreach ($ev in $doc.Events.Event) {
    $txt = $ev.OuterXml
    if ($txt -match '1a2c' -or $txt -match '1A2C') {
        $hex = ($ev.SelectNodes('.//*') | ForEach-Object { $_.'#text' } | Where-Object { $_ -match '^[0-9A-Fa-f ]{8,}$' })
        if ($hex) { $count++; Write-Host ("[{0}] {1}" -f $ev.System.Task.'#text', ($hex -join ' ')) }
    }
}
Write-Host "`n匹配事件数: $count  （ETL/XML 保留在 $out）" -ForegroundColor Green
Write-Host "把上面的输出贴回给我即可。" -ForegroundColor Green
Read-Host "按 Enter 关闭"
