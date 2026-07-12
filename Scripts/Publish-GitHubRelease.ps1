param(
    [string]$Version = "",
    [string]$ZipPath = "",
    [string]$Token = "",
    [string]$Repo = "kayucm21/Zanpet"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content (Join-Path $root "ZapretUI.csproj") -Raw
    if ($csproj -match '<Version>(\d+\.\d+\.\d+)</Version>') { $Version = $Matches[1] }
    else { throw "Version not found in ZapretUI.csproj" }
}

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $root "bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\ZapretUI-v$Version.zip"
}
if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }

if ([string]::IsNullOrWhiteSpace($Token)) {
    $Token = $env:GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($Token)) { $Token = $env:GH_TOKEN }
}
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Set GITHUB_TOKEN or GH_TOKEN (PAT with repo scope)."
}

$tag = "v$Version"
$build = 0
$csprojRaw = Get-Content (Join-Path $root "ZapretUI.csproj") -Raw
if ($csprojRaw -match '<InformationalVersion>[^<]+\+(\d+)</InformationalVersion>') {
    $build = [int]$Matches[1]
}

$headers = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"  = "ZapretUI-Release"
}

function Invoke-GhApi($Method, $Uri, $Body = $null) {
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Compress) }
    return Invoke-RestMethod @params
}

Write-Host "Checking release $tag ..."
$release = $null
try {
    $release = Invoke-GhApi GET "https://api.github.com/repos/$Repo/releases/tags/$tag"
} catch {
    Write-Host "Release not found, creating ..."
    $release = Invoke-GhApi POST "https://api.github.com/repos/$Repo/releases" @{
        tag_name = $tag
        name     = "ZapretUI $Version"
        body     = "[build:$build]`n`nZapretUI v$Version"
        draft    = $false
    }
}

$fileName = Split-Path $ZipPath -Leaf
$uploadUrl = ($release.upload_url -replace '\{\?name,label\}', "?name=$fileName")

Write-Host "Uploading $fileName ($([math]::Round((Get-Item $ZipPath).Length/1MB,1)) MB) ..."
$uploadHeaders = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"  = "ZapretUI-Release"
    "Content-Type" = "application/zip"
}
Invoke-RestMethod -Method POST -Uri $uploadUrl -Headers $uploadHeaders -InFile $ZipPath | Out-Null
Write-Host "Done: https://github.com/$Repo/releases/tag/$tag"
