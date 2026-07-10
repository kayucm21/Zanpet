param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content (Join-Path $root "ZapretUI.csproj") -Raw
    if ($csproj -match '<Version>(\d+\.\d+\.\d+)</Version>') { $Version = $Matches[1] }
    else { throw "Version not found in csproj" }
}

$pub = Join-Path $root "bin\Release\net9.0-windows\win-x64\publish"
if (-not (Test-Path (Join-Path $pub "ZapretUI.exe"))) {
    Write-Host "dotnet publish -c Release ..."
    Push-Location $root
    dotnet publish -c Release
    Pop-Location
}

$zip = Join-Path $pub "ZapretUI-v$Version.zip"
Get-ChildItem $pub -Filter "ZapretUI-v*.zip" | Remove-Item -Force -ErrorAction SilentlyContinue
$items = Get-ChildItem $pub | Where-Object { $_.Name -notlike "ZapretUI-v*.zip" }
if ($items.Count -eq 0) { throw "Publish folder is empty: $pub" }
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path ($items | ForEach-Object { $_.FullName }) -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
