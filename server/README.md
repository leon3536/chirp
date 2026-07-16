# rgas-booth (server)

The booth appliance: one self-contained `RgasReceiver.exe` on Windows 11 Pro.
It accepts raw PCM (s16le 48 kHz stereo) on TCP :4953 from the rinkside
soundboard, holds it in a jitter buffer, and plays it out WASAPI into the mixer
(or WiiM line-in). After install, nobody touches it.

Spec: [SPEC.md](SPEC.md) · Master spec: [../spec/RGAS.md](../spec/RGAS.md) (v0.2)

Deployment model: **no installer, no service, no Scheduled Task.** Two files
(`RgasReceiver.exe` + `config.json`) plus a visible shortcut in the Startup
folder so audio survives a power cycle. Delete the folder and the shortcut to
remove it completely.

## Build (dev machine — the only place the .NET SDK is needed)

```powershell
# from server\
dotnet publish app -c Release -r win-x64 -o publish
```

Produces `publish\` as the **complete booth folder**: `RgasReceiver.exe`
(single file, runtime included) plus `install.ps1`, `supervisor.ps1`,
`diagnose.ps1`, `config.example.json`, and `readme.txt` copied in flat
(sources live in [deploy/](deploy/)).

## Install on the booth machine

1. Copy the `publish\` folder onto the booth machine (e.g. `C:\RGAS`).
   `readme.txt` inside it is the on-machine copy of these instructions.
2. As Administrator, from inside that folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   This is idempotent (S-11) and configures: firewall allow (4953, 1780),
   sleep/hibernate off, lid close = do nothing, WiFi power save off, the
   Startup-folder shortcut, and starts the receiver now.

   Optional static IP instead of a DHCP reservation (S-14):

   ```powershell
   .\install.ps1 -StaticIp 192.168.8.10 -PrefixLength 24 -Gateway 192.168.8.1 -InterfaceAlias "Wi-Fi"
   ```

3. **One manual step (S-13):** enable auto-login — run `netplwiz`, untick
   "Users must enter a user name and password", Apply, enter the password.
   Then set Windows Update active hours to cover game times.
4. Edit `publish\config.json` if needed (see below), power-cycle, and confirm
   audio is back within 2 minutes with no interaction (RGAS §8.5).

## Config (`config.json` next to the exe)

| Key | Default | Meaning |
|---|---|---|
| `listen_port` | 4953 | PCM ingest port the rinkside app targets |
| `buffer_ms` | 150 | **The latency/stability knob** (tunable 80–250, RGAS §4) |
| `output_device_match` | `""` | Substring to pin the WASAPI output (e.g. `"USB Audio"`); empty = default device |
| `status_port` | 1780 | Diagnostics endpoint |

Changing config: edit the file, then end the `RgasReceiver` process (Task
Manager) — the supervisor restarts it with the new values within 2 s.

## Diagnostics

- **The window itself** (S-16/S-17): color-coded WAITING / BUFFERING / ON AIR,
  SOURCE and SIGNAL indicator lights, buffer gauge, live L/R levels, counters
  for connections / sound bites / underruns, connection history, live log.
  Read-only by design — nothing on it can break audio.
- `diagnose.ps1` (in the deployed folder) — startup shortcut, process, ports,
  `/status`, firewall, WiFi power state, recent logs. Read-only.
- `http://<booth-ip>:1780/status` — live JSON: state (waiting/filling/playing),
  connected source, buffer fill, underrun count, output device, uptime.
- `publish\logs\receiver.log` / `supervisor.log` — connection events,
  underruns, restarts.

Buffer tuning procedure lives in the runbook: [../docs/RUNBOOK.md](../docs/RUNBOOK.md).
