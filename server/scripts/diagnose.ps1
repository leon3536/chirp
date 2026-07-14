# rgas-booth diagnostics -- SPEC S-15. Read-only; safe to run any time.
param([string]$ExeDir = "$PSScriptRoot\..\publish")

function Section([string]$Title) { Write-Host "`n=== $Title ===" }

Section "Auto-start (Startup-folder shortcut, S-11)"
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "RGAS Booth Receiver.lnk"
if (Test-Path $lnk) { Write-Host "present: $lnk" }
else { Write-Host "MISSING - run install.ps1" -ForegroundColor Red }

Section "Receiver process"
$proc = Get-Process RgasReceiver -ErrorAction SilentlyContinue
if ($proc) { Write-Host "running (pid $($proc.Id), started $($proc.StartTime))" }
else { Write-Host "NOT RUNNING" -ForegroundColor Red }

Section "Ports"
$cfgPath = Join-Path $ExeDir "config.json"
$pcmPort = 4953; $statusPort = 1780
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $pcmPort = [int]$cfg.listen_port; $statusPort = [int]$cfg.status_port
}
$listen = Get-NetTCPConnection -LocalPort $pcmPort -State Listen -ErrorAction SilentlyContinue
if ($listen) { Write-Host "PCM port ${pcmPort}: LISTENING" } else { Write-Host "PCM port ${pcmPort}: not listening" -ForegroundColor Red }
$estab = Get-NetTCPConnection -LocalPort $pcmPort -State Established -ErrorAction SilentlyContinue
if ($estab) { $estab | ForEach-Object { Write-Host "rinkside source connected from $($_.RemoteAddress):$($_.RemotePort)" } }
else { Write-Host "no rinkside source connected right now" }

Section "Status endpoint"
try {
    $status = Invoke-RestMethod "http://127.0.0.1:$statusPort/status" -TimeoutSec 3
    $status | Format-List | Out-String | Write-Host
} catch {
    Write-Host "unreachable: $($_.Exception.Message)" -ForegroundColor Red
}

Section "Firewall"
foreach ($name in @("RGAS PCM in", "RGAS status in")) {
    $rule = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($rule) { Write-Host "$name : $($rule.Enabled)" } else { Write-Host "$name : MISSING" -ForegroundColor Red }
}

Section "WiFi adapter power management (should be disabled)"
foreach ($adapter in Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
        Where-Object { $_.PhysLayer -like "*802.11*" -or $_.Name -like "*Wi-Fi*" }) {
    try {
        $pm = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
        Write-Host "$($adapter.Name): AllowComputerToTurnOffDevice = $($pm.AllowComputerToTurnOffDevice)"
    } catch { Write-Host "$($adapter.Name): (no power management info)" }
}

Section "Recent receiver log"
$log = Join-Path $ExeDir "logs\receiver.log"
if (Test-Path $log) { Get-Content $log -Tail 15 } else { Write-Host "(no log yet at $log)" }

Section "Recent supervisor log"
$slog = Join-Path $ExeDir "logs\supervisor.log"
if (Test-Path $slog) { Get-Content $slog -Tail 5 } else { Write-Host "(no log yet at $slog)" }
