# rgas-booth supervisor -- SPEC S-12: restart the receiver within 2 s of any
# exit, forever. Started hidden by the "RGAS Booth Receiver" logon task.
param([string]$ExePath = "$PSScriptRoot\RgasReceiver.exe")

$ErrorActionPreference = "Continue"
$Host.UI.RawUI.WindowTitle = "RGAS Booth Receiver"  # S-12: recognizable in the taskbar
$logDir = Join-Path (Split-Path $ExePath -Parent) "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir "supervisor.log"

while ($true) {
    Add-Content $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') starting $ExePath"
    & $ExePath
    Add-Content $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') receiver exited (code $LASTEXITCODE); restart in 2 s"
    Start-Sleep -Seconds 2
}
