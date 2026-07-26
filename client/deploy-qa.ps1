<#
  Publishes rgas-source (client) and syncs the built exe into the QA folder.

  The QA folder is a live rgas-source install with real config.json
  (announcer_api_key etc.) and the real library\ clip collection - see
  "The library folder - handle with care" in README.md. This script only
  ever touches the exe/pdb; config.json and library\ are never copied over
  or deleted.
#>
param(
    [string]$QaPath = "C:\Users\praem\OneDrive\Documents2018\2026-27 Hockey\rgas-client",
    [switch]$Launch
)

$ErrorActionPreference = "Stop"

$clientDir = $PSScriptRoot
$appDir = Join-Path $clientDir "app"
$publishDir = Join-Path $clientDir "publish"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $candidate = "$env:ProgramFiles\dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate } else { throw "dotnet SDK not found" }
} else {
    $dotnet = $dotnet.Source
}

Write-Host "Publishing rgas-source..."
Push-Location $appDir
try {
    & $dotnet publish -c Release -r win-x64 -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

if (-not (Test-Path $QaPath)) {
    Write-Host "QA folder does not exist yet, creating: $QaPath"
    New-Item -ItemType Directory -Path $QaPath -Force | Out-Null
}

Write-Host "Deploying exe to QA folder: $QaPath"
Copy-Item -Path (Join-Path $publishDir "RgasSoundboard.exe") -Destination $QaPath -Force
$pdb = Join-Path $publishDir "RgasSoundboard.pdb"
if (Test-Path $pdb) {
    Copy-Item -Path $pdb -Destination $QaPath -Force
}

$qaConfig = Join-Path $QaPath "config.json"
if (-not (Test-Path $qaConfig)) {
    Write-Host "No config.json in QA folder yet - seeding from config.example.json (edit it to add real keys)."
    Copy-Item -Path (Join-Path $clientDir "config.example.json") -Destination $qaConfig
}

Write-Host "Deployed. config.json and library\ in the QA folder were left untouched."

if ($Launch) {
    Write-Host "Launching from QA folder..."
    Start-Process -FilePath (Join-Path $QaPath "RgasSoundboard.exe") -WorkingDirectory $QaPath
}
