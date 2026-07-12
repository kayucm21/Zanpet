param(
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Get-Content (Join-Path $root "ZapretUI.csproj") -Raw

if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($csproj -match '<Version>(\d+\.\d+\.\d+)</Version>') { $Version = $Matches[1] }
    else { throw "Version not found in ZapretUI.csproj" }
}

$infoVersion = $Version
if ($csproj -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
    $infoVersion = $Matches[1].Trim()
}

$pub = Join-Path $root "bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
$exe = Join-Path $pub "ZapretUI.exe"

if (-not $SkipPublish) {
    Write-Host "dotnet publish -c Release (version $Version) ..."
    Push-Location $root
    dotnet publish -c Release -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0 -p:InformationalVersion=$infoVersion
    if ($LASTEXITCODE -ne 0) { Pop-Location; exit $LASTEXITCODE }
    Pop-Location
}

if (-not (Test-Path $exe)) { throw "ZapretUI.exe not found after publish: $exe" }

$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$exeVersion = "$($info.ProductMajorPart).$($info.ProductMinorPart).$($info.ProductBuildPart)"
if ($exeVersion -ne $Version) {
    throw "Version mismatch: ZapretUI.exe=$exeVersion but csproj=$Version. Re-run without -SkipPublish."
}
Write-Host "Verified exe version: $exeVersion"

Get-ChildItem $pub -Filter "ZapretUI-v*.zip" | Remove-Item -Force -ErrorAction SilentlyContinue
$items = Get-ChildItem $pub | Where-Object { $_.Name -notlike "ZapretUI-v*.zip" }
if ($items.Count -eq 0) { throw "Publish folder is empty: $pub" }

$zip = Join-Path $pub "ZapretUI-v$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path ($items | ForEach-Object { $_.FullName }) -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
