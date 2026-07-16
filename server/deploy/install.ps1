# rgas-booth install -- SPEC S-11, S-13, S-14 (RGAS sec 3.2, sec 7.1).
# Turns a Windows 11 Pro machine into the booth appliance. Idempotent.
#
# This script ships INSIDE the published folder (flat next to the exe); the
# whole folder is what gets copied to the booth machine. Run AS ADMINISTRATOR
# from inside that folder:
#     powershell -ExecutionPolicy Bypass -File install.ps1
#
# Optional static IP (S-14; default is DHCP + a router-side reservation):
#     .\install.ps1 -StaticIp 192.168.8.10 -PrefixLength 24 -Gateway 192.168.8.1 -InterfaceAlias "Wi-Fi"
#
# Auto-login (S-13): pass the booth account + password to configure it fully,
# no netplwiz dance:
#     .\install.ps1 -AutoLogonUser booth -AutoLogonPassword "hunter2"
# The password is stored in the Winlogon registry key IN PLAINTEXT (readable by
# admins). For a dedicated booth machine in a locked room this is the accepted
# trade for deterministic hands-off boot. Omit these to skip (manual netplwiz).

param(
    [string]$StaticIp,
    [int]$PrefixLength = 24,
    [string]$Gateway,
    [string]$InterfaceAlias = "Wi-Fi",
    [string]$AutoLogonUser,
    [string]$AutoLogonPassword,
    [string]$ExePath = "$PSScriptRoot\RgasReceiver.exe"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "FATAL: run as Administrator." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $ExePath)) {
    Write-Host "FATAL: RgasReceiver.exe not found next to this script." -ForegroundColor Red
    Write-Host "Copy the WHOLE published folder to the booth machine and run install.ps1 from inside it."
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
# First delete any auto-created per-exe rules named "RgasReceiver". Windows
# creates these when the receiver first listens; if the "allow access?" prompt
# was dismissed/denied it makes a BLOCK rule, and a block OVERRIDES our allow
# rules (block wins in Windows Firewall), silently killing external access
# while localhost still works. Remove them so only our explicit rules govern.
if (Get-NetFirewallRule -DisplayName "RgasReceiver" -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName "RgasReceiver"
    Write-Host "[firewall] removed auto-created 'RgasReceiver' rule(s) (they can BLOCK inbound)"
}

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
# DC (battery) sleep must be off too: during an outage the battery has to
# BRIDGE the gap, not sleep through it -- AC restore may not wake a sleeper.
Write-Host "[power] sleep/hibernate off on AC and DC, lid = do nothing, wireless max performance"
powercfg /change standby-timeout-ac 0
powercfg /change standby-timeout-dc 0
powercfg /change hibernate-timeout-ac 0
powercfg /change hibernate-timeout-dc 0
powercfg /hibernate off
# Lid close action: 0 = do nothing (AC and DC, so a battery-bridged outage
# survives a closed lid). On many OEM laptops this setting is HIDDEN (only the
# Start-menu power button shows under SUB_BUTTONS), and the "LIDACTION" alias
# then silently no-ops -- so unhide it by explicit GUID first, then set it.
$lidSub  = "4f971e89-eebd-4455-a8de-9e59040e7347"  # SUB_BUTTONS (Power buttons and lid)
$lidGuid = "5ca83367-6e45-459f-a27b-476b1d01c936"  # Lid close action
powercfg -attributes $lidSub $lidGuid -ATTRIB_HIDE    # remove the "hidden" attribute
powercfg /setacvalueindex SCHEME_CURRENT $lidSub $lidGuid 0
powercfg /setdcvalueindex SCHEME_CURRENT $lidSub $lidGuid 0
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

# --- PA hygiene (S-11): the booth output feeds the rink PA ---
# 1. Sound scheme "No Sounds": a Windows notification ding must never play in
#    the arena. Applies to the CURRENT user (run install.ps1 as the booth
#    account, which is also the auto-login account).
Write-Host "[audio] setting sound scheme to 'No Sounds' (no dings on the PA)"
Set-ItemProperty -Path "HKCU:\AppEvents\Schemes" -Name "(Default)" -Value ".None"
foreach ($event in Get-ChildItem "HKCU:\AppEvents\Schemes\Apps\*\*" -ErrorAction SilentlyContinue) {
    $current = Join-Path $event.PSPath ".Current"
    if (Test-Path $current) {
        try { Set-ItemProperty -Path $current -Name "(Default)" -Value "" -ErrorAction Stop } catch {}
    }
}

# 2. Communications ducking off: Windows must never attenuate the PA feed
#    because something registered as a "call". 3 = do nothing.
Write-Host "[audio] communications ducking -> do nothing"
New-Item -Path "HKCU:\Software\Microsoft\Multimedia\Audio" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Multimedia\Audio" -Name "UserDuckingPreference" -Value 3 -Type DWord

# 3. Startup boot chime off (the "No Sounds" scheme doesn't always cover it).
Write-Host "[audio] startup boot chime -> off"
$boot = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation"
New-Item -Path $boot -Force | Out-Null
Set-ItemProperty -Path $boot -Name "DisableStartupSound" -Value 1 -Type DWord

# 4. Mono output (item 5): sum L+R so a single mixer channel / one speaker gets
#    everything. Ease-of-Access "Mono audio" toggle.
Write-Host "[audio] mono output -> on"
New-Item -Path "HKCU:\Software\Microsoft\Multimedia\Audio" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Multimedia\Audio" -Name "AccessibilityMonoMixState" -Value 1 -Type DWord

# --- turn off "Complete Windows setup" nags (item 3) ---
# The checkboxes under Settings > Notifications > Additional settings. These
# post-update "finish setting up your device / welcome experience" screens can
# pop over the dashboard and steal focus on a booth that nobody attends.
Write-Host "[experience] disabling welcome-experience / finish-setup / tips nags"
$cdm = "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
New-Item -Path $cdm -Force | Out-Null
Set-ItemProperty -Path $cdm -Name "SubscribedContent-310093Enabled" -Value 0 -Type DWord  # welcome experience after updates
Set-ItemProperty -Path $cdm -Name "SubscribedContent-338389Enabled" -Value 0 -Type DWord  # tips and suggestions
$scoobe = "HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement"
New-Item -Path $scoobe -Force | Out-Null
Set-ItemProperty -Path $scoobe -Name "ScoobeSystemSettingEnabled" -Value 0 -Type DWord    # suggest ways to finish setup

# --- responsiveness: lock the CPU clock (item 7) ---
# Min = Max = 100% keeps the processor at full base clock, no down-throttling,
# which minimizes DPC latency / audio micro-stutters. Applied to the active
# scheme on AC (the booth runs on wall power).
Write-Host "[perf] processor power state min=100 max=100 (AC)"
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100
powercfg /setactive SCHEME_CURRENT

# --- Windows Update: never restart mid-game (item 8) ---
# Two layers: (a) active hours, but Windows caps the window at 18 h so a literal
# 5am-midnight (19 h) is rejected -- we set 5:00-23:00, the max; (b) the real
# guarantee: never auto-reboot while a user is logged on (auto-login => always
# logged on), so an update waits for a manual reboot instead of a forced one.
Write-Host "[update] active hours 05:00-23:00 + no auto-reboot while logged on"
$uxSettings = "HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"
New-Item -Path $uxSettings -Force | Out-Null
Set-ItemProperty -Path $uxSettings -Name "SmartActiveHoursState" -Value 0 -Type DWord  # 0 = manual, honor the values below
Set-ItemProperty -Path $uxSettings -Name "ActiveHoursStart" -Value 5  -Type DWord
Set-ItemProperty -Path $uxSettings -Name "ActiveHoursEnd"   -Value 23 -Type DWord
$auPolicy = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
New-Item -Path $auPolicy -Force | Out-Null
Set-ItemProperty -Path $auPolicy -Name "NoAutoRebootWithLoggedOnUsers" -Value 1 -Type DWord

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

# --- stop any existing supervisor/receiver first (idempotent re-install) ---
# Prevents two supervisors racing to spawn the receiver -> port-in-use loop.
Write-Host "[startup] stopping any existing receiver/supervisor"
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*supervisor.ps1*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process RgasReceiver -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# --- auto-start: Startup-folder shortcut -> supervisor loop (S-11, S-12) ---
# Deliberately a plain shell:startup shortcut, not a service or Scheduled Task:
# visible, trivially removable, no background machinery (owner decision).
# A single, fixed-name shortcut -> re-install overwrites rather than duplicates.
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

# --- auto-login (S-13) ---
# Two registry facts, often confused:
#  * DevicePasswordLessBuildVersion (PasswordLess\Device): 0 only UN-HIDES the
#    netplwiz checkbox and disables the "Hello sign-in only" requirement that
#    can otherwise block auto-login. It does NOT log anyone in.
#  * AutoAdminLogon + DefaultUserName/Domain/Password (Winlogon): THIS is what
#    actually performs the sign-in at boot.
$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
$plPath   = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device"
New-Item -Path $plPath -Force | Out-Null
Set-ItemProperty -Path $plPath -Name "DevicePasswordLessBuildVersion" -Value 0 -Type DWord

if ($AutoLogonUser -and $AutoLogonPassword) {
    # Domain for a LOCAL account is the machine name; a real domain would differ.
    $domain = $env:COMPUTERNAME
    Set-ItemProperty -Path $winlogon -Name "AutoAdminLogon"    -Value "1"             -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultUserName"   -Value $AutoLogonUser  -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultDomainName" -Value $domain         -Type String
    Set-ItemProperty -Path $winlogon -Name "DefaultPassword"   -Value $AutoLogonPassword -Type String
    # A leftover count can make auto-login fire only N times, then stop.
    Remove-ItemProperty -Path $winlogon -Name "AutoLogonCount" -ErrorAction SilentlyContinue
    Write-Host "[login] auto-login configured for '$domain\$AutoLogonUser' (S-13)" -ForegroundColor Green
    Write-Host "[login] NOTE: password is stored in the Winlogon registry key in plaintext."
} else {
    Write-Host ""
    Write-Host "=== auto-login NOT configured (S-13) ===" -ForegroundColor Yellow
    Write-Host "Re-run with credentials to set it up hands-off (recommended for the booth):"
    Write-Host "    .\install.ps1 -AutoLogonUser <account> -AutoLogonPassword <password>"
    Write-Host ""
    Write-Host "Or do it by hand: run  netplwiz  (checkbox is now un-hidden), select the"
    Write-Host "account, untick 'Users must enter a user name and password', Apply, type"
    Write-Host "the password. Use a LOCAL account with a non-expiring password."
}
Write-Host ""
Write-Host "=== ONE BIOS STEP (item 1: auto power-on after outage) ===" -ForegroundColor Yellow
Write-Host "Not OS-scriptable. In BIOS/UEFI set 'Restore on AC Power Loss' = Power On"
Write-Host "(a.k.a. 'AC Recovery' / 'After Power Failure'). Do this once per machine."
Write-Host ""
Write-Host "Then verify: reboot and confirm audio path within 2 min, no interaction (RGAS sec 8.5)."
Write-Host "Diagnostics: .\diagnose.ps1  |  http://<booth-ip>:$statusPort/status"
