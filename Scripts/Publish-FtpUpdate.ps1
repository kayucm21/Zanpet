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
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content (Join-Path $root "ZapretUI.csproj") -Raw
    if ($csproj -match '<Version>(\d+\.\d+\.\d+)</Version>') { $Version = $Matches[1] }
    else { throw "Version not found in csproj" }
}

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $buildScript = Join-Path $PSScriptRoot "Build-ReleaseZip.ps1"
    & $buildScript -Version $Version
    $ZipPath = Join-Path $root "bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\ZapretUI-v$Version.zip"
    if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }
}

$fileName = Split-Path $ZipPath -Leaf
$zipVersion = [regex]::Match($fileName, '\d+\.\d+\.\d+').Value
if ($zipVersion -and $zipVersion -ne $Version) {
    throw "Zip version ($zipVersion) != target ($Version)"
}
$manifest = @{
    version = $Version
    tag     = "v$Version"
    build   = 5
    file    = $fileName
    notes   = "v2.9.14: fix WinDivert64.sys locked on startup, safe engine file copy"
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

Write-Host "Uploading to ftp://$FtpHost$FtpPath ..."
Upload-FtpFile $ZipPath $fileName
Upload-FtpFile $manifestPath "update.json"
Write-Host "Done. Clients will see v$Version on update check."
