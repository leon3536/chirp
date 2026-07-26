<#
  Publishes rgas-booth (server) into server\publish\ - the complete booth
  folder: RgasReceiver.exe plus install.ps1/supervisor.ps1/diagnose.ps1/
  config.example.json/readme.txt, copied in automatically by the csproj's
  CopyDeployFiles post-publish target. Local build only - this does not
  deploy anywhere (there is no QA booth machine to sync to).
#>
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot "publish")
)

$ErrorActionPreference = "Stop"

$serverDir = $PSScriptRoot
$appDir = Join-Path $serverDir "app"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $candidate = "$env:ProgramFiles\dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate } else { throw "dotnet SDK not found" }
} else {
    $dotnet = $dotnet.Source
}

Write-Host "Publishing rgas-booth..."
& $dotnet publish $appDir -c Release -r win-x64 -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published to $PublishDir"
