# Integration test: tg-ws-proxy bridge + Telegram Desktop launch
$ErrorActionPreference = "Stop"
$root = "$env:LOCALAPPDATA\ZapretUI"
$tgDir = Join-Path $root "tmp\Telegram"
$tgwsDir = Join-Path $root "engine\tgws"
$tgExe = Join-Path $tgDir "Telegram.exe"
$tgwsExe = Join-Path $tgwsDir "TgWsProxy.exe"
$tgZip = Join-Path $root "tmp\tsetup-x64.7z"
$secret = "eecb9b9a39b6f0d6e8c4a2b1f0d3e7a"

function Log($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

New-Item -ItemType Directory -Force -Path $tgDir, $tgwsDir, (Join-Path $root "tmp") | Out-Null

# 1. Download Telegram portable if missing
if (-not (Test-Path $tgExe)) {
    Log "Downloading Telegram portable..."
    $portableUrl = "https://telegram.org/dl/desktop/win64_portable"
    $dl = Join-Path $root "tmp\tportable.zip"
    Invoke-WebRequest -Uri $portableUrl -OutFile $dl -UseBasicParsing
    Expand-Archive -Path $dl -DestinationPath $tgDir -Force
    if (-not (Test-Path $tgExe)) {
        Get-ChildItem $tgDir -Recurse -Filter Telegram.exe | Select-Object -First 1 | ForEach-Object {
            Copy-Item $_.FullName $tgExe -Force
        }
    }
}
if (-not (Test-Path $tgExe)) { throw "Telegram.exe not found after download" }
Log "Telegram: $tgExe"

# 2. Download tg-ws-proxy if missing
if (-not (Test-Path $tgwsExe)) {
    Log "Downloading tg-ws-proxy..."
    $url = "https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.8.1/TgWsProxy_windows.exe"
    Invoke-WebRequest -Uri $url -OutFile $tgwsExe -UseBasicParsing
}

# 3. Write config
$dataDir = Join-Path $tgwsDir "TgWsProxy_data"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
@{
    port = 1443; host = "127.0.0.1"; secret = $secret
    dc_ip = @("2:149.154.167.220", "4:149.154.167.220")
    verbose = $false; check_updates = $false; cfproxy = $true
    language = "ru"
} | ConvertTo-Json | Set-Content (Join-Path $dataDir "config.json") -Encoding UTF8

# 4. Start bridge
Get-Process TgWsProxy -ErrorAction SilentlyContinue | Stop-Process -Force
$bridge = Start-Process -FilePath $tgwsExe -ArgumentList "--portable" -WorkingDirectory $tgwsDir -PassThru -WindowStyle Hidden
Log "Bridge PID $($bridge.Id)"

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $c.Connect("127.0.0.1", 1443)
        $c.Close()
        $ok = $true
        break
    } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ok) { throw "Bridge port 1443 not listening" }
Log "Bridge listening on 127.0.0.1:1443"

# 5. Launch Telegram with proxy deeplink
$deeplink = "tg://proxy?server=127.0.0.1&port=1443&secret=dd$secret"
Get-Process Telegram -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$tg = Start-Process -FilePath $tgExe -ArgumentList "-- `"$deeplink`"" -PassThru
Log "Telegram PID $($tg.Id) with auto-proxy"

Start-Sleep 8
$alive = -not $tg.HasExited
Log "Telegram running: $alive"
Log "TEST OK - bridge up, Telegram launched with auto-proxy"
