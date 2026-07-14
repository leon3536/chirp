# rgas-booth install -- SPEC S-11, S-13, S-14 (RGAS sec 3.2, sec 7.1).
# Turns a Windows 11 Pro machine into the booth appliance. Idempotent.
#
# Run AS ADMINISTRATOR from the server\ folder, after publishing the receiver:
#     dotnet publish app -c Release -r win-x64 -o publish
#     powershell -ExecutionPolicy Bypass -File install.ps1
#
# Optional static IP (S-14; default is DHCP + a router-side reservation):
#     .\install.ps1 -StaticIp 192.168.8.10 -PrefixLength 24 -Gateway 192.168.8.1 -InterfaceAlias "Wi-Fi"

param(
    [string]$StaticIp,
    [int]$PrefixLength = 24,
    [string]$Gateway,
    [string]$InterfaceAlias = "Wi-Fi",
    [string]$ExePath = "$PSScriptRoot\publish\RgasReceiver.exe"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "FATAL: run as Administrator." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $ExePath)) {
    Write-Host "FATAL: $ExePath not found. Publish first:" -ForegroundColor Red
    Write-Host "  dotnet publish `"$PSScriptRoot\app`" -c Release -r win-x64 -o `"$PSScriptRoot\publish`""
    exit 1
}

# --- config next to the exe (S-8) ---
$exeDir = Split-Path $ExePath -Parent
if (-not (Test-Path "$exeDir\config.json")) {
    Copy-Item "$PSScriptRoot\config.example.json" "$exeDir\config.json"
    Write-Host "[config] created $exeDir\config.json from example"
}

# --- read ports from config for the firewall rules ---
$cfg = Get-Content "$exeDir\config.json" -Raw | ConvertFrom-Json
$pcmPort = [int]$cfg.listen_port
$statusPort = [int]$cfg.status_port

# --- firewall (S-11): rinkside PCM + status endpoint ---
foreach ($rule in @(
        @{ Name = "RGAS PCM in";    Port = $pcmPort },
        @{ Name = "RGAS status in"; Port = $statusPort })) {
    if (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -DisplayName $rule.Name
    }
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $rule.Port -Profile Any | Out-Null
    Write-Host "[firewall] inbound allow TCP $($rule.Port) ($($rule.Name))"
}

# --- power hardening (S-11, RGAS sec 3.2) ---
Write-Host "[power] sleep/hibernate off on AC, lid = do nothing, wireless max performance"
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /hibernate off
# Lid close action: 0 = do nothing (both AC and DC so a battery-bridged outage survives a closed lid)
powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0
powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0
# Wireless adapter power saving: 0 = maximum performance
powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0
powercfg /setactive SCHEME_CURRENT

# WiFi adapter device-level power management off (top dropout cause, RGAS sec 3.2)
foreach ($adapter in Get-NetAdapter -Physical | Where-Object { $_.PhysLayer -like "*802.11*" -or $_.Name -like "*Wi-Fi*" }) {
    try {
        Disable-NetAdapterPowerManagement -Name $adapter.Name -NoRestart -ErrorAction Stop
        Write-Host "[power] adapter power management off: $($adapter.Name)"
    } catch {
        Write-Host "[power] could not adjust $($adapter.Name): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# --- optional static IP (S-14) ---
if ($StaticIp) {
    Write-Host "[net] pinning static IP $StaticIp/$PrefixLength on '$InterfaceAlias'"
    Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.PrefixOrigin -eq "Manual" } |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    New-NetIPAddress -InterfaceAlias $InterfaceAlias -IPAddress $StaticIp -PrefixLength $PrefixLength `
        -DefaultGateway $Gateway | Out-Null
    Set-DnsClientServerAddress -InterfaceAlias $InterfaceAlias -ServerAddresses $Gateway
} else {
    Write-Host "[net] DHCP mode; configure a DHCP reservation for this machine (S-14)"
}

# --- auto-start: Startup-folder shortcut -> supervisor loop (S-11, S-12) ---
# Deliberately a plain shell:startup shortcut, not a service or Scheduled Task:
# visible, trivially removable, no background machinery (owner decision).
$startupDir = [Environment]::GetFolderPath("Startup")
$lnkPath = Join-Path $startupDir "RGAS Booth Receiver.lnk"
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = "powershell.exe"
$lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\supervisor.ps1`" -ExePath `"$ExePath`""
$lnk.WorkingDirectory = $PSScriptRoot
$lnk.WindowStyle = 7  # minimized (S-12): visible in the taskbar, click for live logs
$lnk.Description = "RGAS booth receiver supervisor (SPEC S-11/S-12)"
$lnk.Save()
Write-Host "[startup] shortcut created: $lnkPath"

# Start the supervisor now (minimized console, S-12) if not already running
if (-not (Get-Process RgasReceiver -ErrorAction SilentlyContinue)) {
    Start-Process powershell -WindowStyle Minimized -ArgumentList `
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$PSScriptRoot\supervisor.ps1", "-ExePath", $ExePath
    Write-Host "[startup] supervisor started (minimized taskbar window: 'RGAS Booth Receiver')"
} else {
    Write-Host "[startup] receiver already running"
}

# --- the one manual step (S-13) ---
Write-Host ""
Write-Host "=== MANUAL STEP REQUIRED: auto-login (S-13) ===" -ForegroundColor Yellow
Write-Host "Windows requires the password interactively; a script cannot do this safely:"
Write-Host "  1. Run: netplwiz"
Write-Host "  2. Untick 'Users must enter a user name and password', Apply, enter the password."
Write-Host "  (If the checkbox is hidden on Windows 11, set registry value"
Write-Host "   HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device\DevicePasswordLessBuildVersion = 0 and rerun netplwiz.)"
Write-Host "Also set Windows Update active hours to cover game times (Settings > Windows Update)."
Write-Host ""
Write-Host "Then verify: reboot and confirm audio path within 2 min, no interaction (RGAS sec 8.5)."
Write-Host "Diagnostics: scripts\diagnose.ps1  |  http://<booth-ip>:$statusPort/status"
