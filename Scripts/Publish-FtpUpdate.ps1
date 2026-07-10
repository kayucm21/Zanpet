param(
    [Parameter(Mandatory = $true)][string]$FtpHost,
    [Parameter(Mandatory = $true)][string]$FtpUser,
    [Parameter(Mandatory = $true)][string]$FtpPassword,
    [string]$FtpPath = "/updates",
    [int]$FtpPort = 21,
    [switch]$UseSsl,
    [string]$ZipPath = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $root = Split-Path -Parent $PSScriptRoot
    $zip = Get-ChildItem -Path (Join-Path $root "bin\Release\net9.0-windows\win-x64\publish") -Filter "ZapretUI-v*.zip" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $zip) { throw "Соберите publish: dotnet publish -c Release" }
    $ZipPath = $zip.FullName
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [regex]::Match((Split-Path $ZipPath -Leaf), '\d+\.\d+\.\d+').Value
    if (-not $Version) { throw "Не удалось определить версию из имени zip" }
}

$fileName = Split-Path $ZipPath -Leaf
$manifest = @{
    version = $Version
    tag     = "v$Version"
    file    = $fileName
} | ConvertTo-Json -Compress

$manifestPath = Join-Path $env:TEMP "update.json"
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

function Upload-FtpFile($LocalPath, $RemoteName) {
    $scheme = if ($UseSsl) { "ftps" } else { "ftp" }
    $remotePath = $FtpPath.TrimEnd('/') + "/" + $RemoteName
    $uri = "${scheme}://${FtpHost}:${FtpPort}${remotePath}"
    $request = [System.Net.FtpWebRequest]::Create($uri)
    $request.Method = [System.Net.WebRequestMethods+Ftp]::UploadFile
    $request.Credentials = New-Object System.Net.NetworkCredential($FtpUser, $FtpPassword)
    $request.UseBinary = $true
    $request.UsePassive = $true
    $request.EnableSsl = [bool]$UseSsl
    $bytes = [System.IO.File]::ReadAllBytes($LocalPath)
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    $response = $request.GetResponse()
    $response.Close()
    Write-Host "OK $RemoteName"
}

Write-Host "Загрузка на ftp://$FtpHost$FtpPath ..."
Upload-FtpFile $ZipPath $fileName
Upload-FtpFile $manifestPath "update.json"
Write-Host "Готово. Друзья увидят v$Version при «Проверить обновления»."
