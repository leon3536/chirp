RGAS BOOTH RECEIVER
===================

This folder is the complete booth installation. It receives game audio from
the rinkside soundboard over WiFi and plays it out this machine's audio
output into the mixer / PA. No internet needed, nothing else to install.

WHAT'S IN HERE
  RgasReceiver.exe     the receiver (dashboard window + audio)
  config.json          settings (created on install; see below)
  config.example.json  documented copy of every setting
  install.ps1          one-time machine setup (run as Administrator)
  supervisor.ps1       keeps the receiver running (auto-started at logon)
  diagnose.ps1         read-only health check
  logs\                receiver.log / supervisor.log appear here

FIRST-TIME SETUP (once per booth machine)
  1. Copy this WHOLE folder somewhere permanent, e.g.  C:\RGAS
  2. Open PowerShell AS ADMINISTRATOR in that folder and run, filling in the
     booth account name and password so it boots hands-off:
         powershell -ExecutionPolicy Bypass -File .\install.ps1 `
             -AutoLogonUser booth -AutoLogonPassword "the-password"
     This sets up: firewall, no-sleep power, WiFi power save off, silent
     sound scheme, AUTO-LOGIN, and auto-start at logon. Safe to re-run.
     (Leave the two AutoLogon options off and it prints manual instructions
     instead. The password is stored in the registry in plaintext -- fine for
     a dedicated machine in a locked booth.)
  3. Reboot. Within ~2 minutes the dashboard window should be on screen
     saying WAITING FOR RINKSIDE, with NO login prompt. Done.
     If a login prompt appears: the account name/password were wrong, or a
     PIN/Windows Hello is set on the account -- remove the PIN and re-run.

EVERYDAY
  Nobody touches this machine. The dashboard window shows:
     WAITING FOR RINKSIDE  (amber) = no laptop connected yet
     BUFFERING             (blue)  = connecting
     ON AIR                (green) = receiving and playing
  Closing the window is fine -- it restarts by itself within 2 seconds.

SETTINGS (config.json, edit with Notepad, then close the window once)
  buffer_ms              latency vs stability knob. 150 default.
                         Lower = snappier horn, higher = fewer dropouts.
  output_device_match    part of the output device's name to lock onto,
                         e.g. "USB Audio". Empty = follow system default.
  output_volume_percent  e.g. 100 = force volume to 100% and un-mute every
                         time audio starts. null = leave volume alone.

IF SOMETHING IS WRONG
  - Dashboard says WAITING but the laptop says streaming:
      both machines on the same WiFi? booth IP correct on the laptop?
  - No sound in the rink but dashboard says ON AIR with moving meters:
      check the cable from this machine to the mixer, and mixer channel.
  - Health check: right-click diagnose.ps1 > Run with PowerShell,
      or browse to  http://THIS-MACHINE-IP:1780/status  from any device.

Rink Game Audio System (RGAS) -- spec and source: see the project repo.
