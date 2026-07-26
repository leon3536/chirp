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

# Bug fix 2026-07-25: a single unexpected registry error inside a cosmetic
# hygiene step (sound scheme, mono output, DND, etc.) used to abort the WHOLE
# rest of the script under $ErrorActionPreference = Stop -- silently skipping
# the CPU lock, active hours, static IP, startup shortcut, and auto-login that
# come after it. None of these steps are load-bearing for the receiver itself
# (S-18 spirit: a non-critical subsystem must never take down the appliance),
# so each one now runs isolated: a failure is logged and the script moves on.
function Invoke-Hygiene([string]$Label, [scriptblock]$Action) {
    try { & $Action }
    catch { Write-Host "[hygiene] SKIPPED ($Label): $($_.Exception.Message)" -ForegroundColor Yellow }
}

# Bug fix 2026-07-25 (root cause): every registry write in this script used to
# go through PowerShell's registry provider (New-Item -Force + Set-ItemProperty).
# That provider has a known-flaky delete-then-recreate path when -Force targets
# a key that already exists with real subkeys under it (PushNotifications and
# several other Windows-managed keys touched below all qualify) -- it can throw
# "Cannot delete a subkey tree because the subkey does not exist" even though
# nothing is actually broken. reg.exe's own `add` verb creates the full key
# path AND sets the value in one atomic, decades-tested operation, and never
# touches sibling/child subkeys, so every write below goes through it instead.
# Stress-tested against: missing path, already-existing path, DWORD, REG_SZ,
# default/unnamed value (including an EMPTY default -- PowerShell silently
# drops an empty-string argument passed to a native exe, so /d is omitted
# entirely rather than passed as ""), a value name containing a hyphen, a key
# path containing a space ("Windows NT"), a parent key that already has a real
# child subkey (the exact shape of the failure this replaces), and the
# provider-qualified PSPath format Get-ChildItem returns (used by the sound-
# scheme loop below, which is NOT the same string shape as "HKCU:\...").
function ConvertTo-RegExePath([string]$Path) {
    $p = $Path -replace '^Microsoft\.PowerShell\.Core\\Registry::', ''
    $p = $p -replace '^HKEY_CURRENT_USER\\', 'HKCU\'
    $p = $p -replace '^HKEY_LOCAL_MACHINE\\', 'HKLM\'
    $p = $p -replace '^HKCU:\\', 'HKCU\'
    $p = $p -replace '^HKLM:\\', 'HKLM\'
    return $p
}
function Set-Reg([string]$Path, [string]$Name, [string]$Value, [string]$RegType = "REG_DWORD") {
    $regPath = ConvertTo-RegExePath $Path
    $regArgs = @("add", $regPath, "/v", $Name, "/t", $RegType, "/f")
    if ($Value -ne "") { $regArgs += @("/d", $Value) }
    $out = & reg.exe @regArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg add '$regPath' /v $Name failed: $out" }
}
function Set-RegDefault([string]$Path, [string]$Value) {
    $regPath = ConvertTo-RegExePath $Path
    $regArgs = @("add", $regPath, "/ve", "/f")
    if ($Value -ne "") { $regArgs += @("/d", $Value) }
    $out = & reg.exe @regArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg add '$regPath' (default) failed: $out" }
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

# --- output level pinned to 100 (owner request 2026-07-25) ---
# The booth's WASAPI endpoint volume is fixed at 100; loudness is controlled
# downstream at the mixer, never at the Windows endpoint. S-6b re-asserts this
# (and un-mutes) on every device claim, so an accidental nudge/mute self-heals
# on the next boot or device switch -- this just guarantees the config value
# itself is 100 even on a re-install over an older config.json.
Invoke-Hygiene "output level -> 100" {
    if ($cfg.output_volume_percent -ne 100) {
        Write-Host "[audio] output_volume_percent -> 100 (was $($cfg.output_volume_percent); control loudness at the mixer)"
        # Add-Member (not direct assignment): an older config.json predating S-6b
        # may not have this key at all, and PSCustomObject rejects assigning a
        # property that doesn't already exist.
        $cfg | Add-Member -NotePropertyName output_volume_percent -NotePropertyValue 100 -Force
        $cfg | ConvertTo-Json -Depth 5 | Set-Content -Path "$exeDir\config.json"
    } else {
        Write-Host "[audio] output_volume_percent already 100"
    }
}

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
# Each step below is isolated by Invoke-Hygiene (bug fix 2026-07-25): a
# failure in any ONE of these cosmetic settings -- including one that hasn't
# bitten us yet, e.g. a registry quirk on a Windows build we haven't tested --
# is logged and skipped rather than aborting the CPU lock / active hours /
# static IP / startup shortcut / auto-login that come after it.

# 1. Sound scheme "No Sounds": a Windows notification ding must never play in
#    the arena. Applies to the CURRENT user (run install.ps1 as the booth
#    account, which is also the auto-login account).
Invoke-Hygiene "sound scheme -> No Sounds" {
    Write-Host "[audio] setting sound scheme to 'No Sounds' (no dings on the PA)"
    Set-RegDefault "HKCU:\AppEvents\Schemes" ".None"
    foreach ($event in Get-ChildItem "HKCU:\AppEvents\Schemes\Apps\*\*" -ErrorAction SilentlyContinue) {
        $current = Join-Path $event.PSPath ".Current"
        if (Test-Path $current) {
            try { Set-RegDefault $current "" } catch {}
        }
    }
}

# 2. Communications ducking off: Windows must never attenuate the PA feed
#    because something registered as a "call". 3 = do nothing.
Invoke-Hygiene "communications ducking" {
    Write-Host "[audio] communications ducking -> do nothing"
    Set-Reg "HKCU:\Software\Microsoft\Multimedia\Audio" "UserDuckingPreference" "3"
}

# 3. Startup boot chime off (the "No Sounds" scheme doesn't always cover it).
Invoke-Hygiene "startup boot chime" {
    Write-Host "[audio] startup boot chime -> off"
    Set-Reg "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation" "DisableStartupSound" "1"
}

# 4. Mono output (item 5): sum L+R so a single mixer channel / one speaker gets
#    everything. Ease-of-Access "Mono audio" toggle.
Invoke-Hygiene "mono output" {
    Write-Host "[audio] mono output -> on"
    Set-Reg "HKCU:\Software\Microsoft\Multimedia\Audio" "AccessibilityMonoMixState" "1"
}

# --- turn off "Complete Windows setup" nags (item 3) ---
# The checkboxes under Settings > Notifications > Additional settings. These
# post-update "finish setting up your device / welcome experience" screens can
# pop over the dashboard and steal focus on a booth that nobody attends.
Invoke-Hygiene "Windows setup nags" {
    Write-Host "[experience] disabling welcome-experience / finish-setup / tips nags"
    $cdm = "HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
    Set-Reg $cdm "SubscribedContent-310093Enabled" "0"  # welcome experience after updates
    Set-Reg $cdm "SubscribedContent-338389Enabled" "0"  # tips and suggestions
    Set-Reg "HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" "ScoobeSystemSettingEnabled" "0"  # suggest ways to finish setup
}

# --- Do Not Disturb: no toast banner may ever pop over the dashboard or steal
# focus (owner request 2026-07-25) ---
# Focus Assist's own on/off state lives in an undocumented binary blob
# (CloudStore\...\windows.data.notifications.quiethoursprofile) that isn't
# reliably scriptable and can shift between Windows builds. The documented,
# robust equivalent -- and the thing that actually matters here -- is turning
# toast notifications off entirely: per-user master toggle plus the
# corresponding policy, so nothing (Windows Update, low battery, Bluetooth
# pairing, mail, etc.) can ever appear on screen or make a sound.
Invoke-Hygiene "Do Not Disturb (toast notifications)" {
    Write-Host "[notifications] toast notifications -> off (DND)"
    Set-Reg "HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications" "ToastEnabled" "0"
    Set-Reg "HKCU:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications" "NoToastApplicationNotification" "1"
}

# --- responsiveness: lock the CPU clock (item 7) ---
# Min = Max = 100% keeps the processor at full base clock, no down-throttling,
# which minimizes DPC latency / audio micro-stutters. Applied to the active
# scheme on AC (the booth runs on wall power).
Invoke-Hygiene "CPU power lock" {
    Write-Host "[perf] processor power state min=100 max=100 (AC)"
    powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100
    powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100
    powercfg /setactive SCHEME_CURRENT
}

# --- Windows Update: never restart mid-game (item 8) ---
# Two layers: (a) active hours -- the window the booth is actually in use, so
# Windows avoids restarting inside it; owner request 2026-07-25 sets this to
# 23:00-05:00 (overnight games), which Windows supports as a wraparound span
# (End < Start); (b) the real guarantee regardless of the active-hours window:
# never auto-reboot while a user is logged on (auto-login => always logged
# on), so an update waits for a manual reboot instead of a forced one.
Invoke-Hygiene "Windows Update active hours + no forced reboot" {
    Write-Host "[update] active hours 23:00-05:00 + no auto-reboot while logged on"
    $uxSettings = "HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"
    Set-Reg $uxSettings "SmartActiveHoursState" "0"  # 0 = manual, honor the values below
    Set-Reg $uxSettings "ActiveHoursStart" "23"
    Set-Reg $uxSettings "ActiveHoursEnd" "5"
    Set-Reg "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" "NoAutoRebootWithLoggedOnUsers" "1"
}

# --- optional static IP (S-14) ---
if ($StaticIp) {
    Invoke-Hygiene "static IP" {
        Write-Host "[net] pinning static IP $StaticIp/$PrefixLength on '$InterfaceAlias'"
        Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.PrefixOrigin -eq "Manual" } |
            Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        New-NetIPAddress -InterfaceAlias $InterfaceAlias -IPAddress $StaticIp -PrefixLength $PrefixLength `
            -DefaultGateway $Gateway | Out-Null
        Set-DnsClientServerAddress -InterfaceAlias $InterfaceAlias -ServerAddresses $Gateway
    }
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
Set-Reg $plPath "DevicePasswordLessBuildVersion" "0"

if ($AutoLogonUser -and $AutoLogonPassword) {
    # Domain for a LOCAL account is the machine name; a real domain would differ.
    $domain = $env:COMPUTERNAME
    Set-Reg $winlogon "AutoAdminLogon"    "1"                 "REG_SZ"
    Set-Reg $winlogon "DefaultUserName"   $AutoLogonUser      "REG_SZ"
    Set-Reg $winlogon "DefaultDomainName" $domain             "REG_SZ"
    Set-Reg $winlogon "DefaultPassword"   $AutoLogonPassword  "REG_SZ"
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
