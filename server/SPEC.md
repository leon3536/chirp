# rgas-booth (server) — App Specification

Version: 2.0 — supersedes v1 (S-1..S-13, Debian 12 / Snapcast) in full; all
requirements renumbered.
Derived from: [../spec/RGAS.md](../spec/RGAS.md) v0.2 draft (§3.1, §3.2).
Scope: everything on the booth machine (ThinkBook, **Windows 11 Pro**).
Out of scope: the rinkside soundboard (see [../client/SPEC.md](../client/SPEC.md));
physical cabling and the mixer/WiiM decision (RGAS §2, §9).

## 1. Purpose

Turn a Windows 11 Pro machine into an appliance: it accepts one raw-PCM TCP
stream on :4953, holds it in a jitter buffer, and plays it out the local WASAPI
device into the mixer (or WiiM line-in). After install, **nobody interacts with
it** — it survives power loss, WiFi blips, and lid closes with zero human
action (RGAS §1 Goal 4, §5).

Why not Snapcast (v1): no official Windows snapserver binary exists, and with a
single output zone Snapcast's only unique value (multiroom sync) is a RGAS §1
non-goal. The wire contract is preserved: raw s16le 48 kHz stereo over TCP
:4953, exactly what the rinkside app pushes (client SPEC C-29).

## 2. Requirements

### Receiver

| ID | Requirement | Trace |
|---|---|---|
| S-1 | Single self-contained executable (`RgasReceiver.exe`, .NET self-contained single-file publish): copy-and-run, no installer, no runtime installs. | RGAS §3.1, §7.1 |
| S-2 | Listens on `tcp://0.0.0.0:<listen_port>` (default 4953) for raw PCM, s16le 48 kHz stereo. No auth (trust boundary is the WiFi, RGAS §6). | RGAS §3.1 |
| S-3 | One active source: a newly accepted connection replaces the current one (old socket closed). A rinkside laptop swap therefore needs no booth action. | RGAS §3.1, §5 |
| S-4 | Jitter buffer: play out only after `buffer_ms` (default 150) is buffered; on underrun, play silence and re-fill to `buffer_ms` before resuming (no crash, no stall). Underruns are counted. | RGAS §3.1, §4 |
| S-5 | Latency anchoring: if the buffer sustains fill above `2 × buffer_ms + 200 ms`, trim it back to `buffer_ms` so end-to-end latency cannot creep after network hiccups. | RGAS §3.1, §4 |
| S-6 | WASAPI output; device chosen by case-insensitive substring `output_device_match` from config, else the system default. Selection is logged. USB plug/unplug must not steal a pinned output. | RGAS §3.1, §3.2 |
| S-7 | Status endpoint on `http://0.0.0.0:<status_port>` (default 1780): `GET /status` returns JSON — state (waiting/filling/playing), source address, buffer fill ms, underrun count, output device, uptime. Diagnostics only; never an operator surface. Implemented without HTTP.sys URL ACLs (plain TCP) so no extra OS config is needed. | RGAS §3.1, §7.5 |
| S-8 | All tunables in `config.json` next to the executable: `listen_port`, `buffer_ms`, `output_device_match`, `status_port`. Missing file or keys fall back to defaults, logged. | RGAS §3.1, §5 |
| S-9 | Logs to console and to a size-capped `logs/receiver.log` next to the executable (connection events, underruns, device selection) for after-the-fact triage. | RGAS §7.5 |
| S-10 | No cloud or internet dependency at runtime; fully functional with building internet down. | RGAS §1 Non-goals |

### Unattended operation (Windows 11 Pro)

| ID | Requirement | Trace |
|---|---|---|
| S-11 | Idempotent PowerShell install script (run as admin) that configures: inbound firewall allow rules (`listen_port`, `status_port`); power settings — sleep/hibernate off on AC, lid close = do nothing (AC and DC); WiFi adapter power management off and wireless power policy at maximum performance; auto-start via a **Startup-folder shortcut** (`shell:startup`) to the supervisor — deliberately not a service or Scheduled Task (owner decision 2026-07-13: no background machinery beyond the receiver process itself; the shortcut is visible and trivially removable). Re-running is safe. | RGAS §3.2, §7.1; owner decision |
| S-12 | Supervisor loop: restarts the receiver within 2 s of any exit, forever; logs restarts. Launched by the S-11 startup shortcut as a **minimized console window titled "RGAS Booth Receiver"** — never hidden, so a glance at the taskbar answers "is it running?", and clicking it shows live logs. Also runnable by double-click. | RGAS §3.2, §5, §7.5 |
| S-13 | Boot straight to working audio with no interaction, within 2 minutes: auto-login enabled (manual `netplwiz` step — Windows requires the password interactively; the install script prints the instructions) + S-11 startup shortcut + S-12. | RGAS §3.2, §5, §8.5 |
| S-14 | Static IP hook: install script accepts `-StaticIp/-PrefixLength/-Gateway/-InterfaceAlias` and applies them; default is DHCP (site uses a DHCP reservation). The rinkside app targets this address. | RGAS §3.2 |
| S-15 | Read-only diagnostics script: startup-shortcut presence, receiver process, :4953 listener + established source connection, `/status` JSON, firewall rules, power-save state, tail of logs. Enough to triage remotely without touching config. | RGAS §7.5 |
| S-16 | Status window: the receiver is a windowed app (owner request 2026-07-13) showing at a glance — color-coded state (WAITING / BUFFERING / ON AIR), connected source, buffer fill vs target, live L/R output level from the actual PCM, underruns, output device, uptime, and a live log pane. Read-only: no controls that could break audio (config stays in `config.json`). Closing the window exits the process; the S-12 supervisor restarts it within 2 s, so the window is effectively always present on the booth desktop. | RGAS §3.2, §7.5; owner request |
| S-17 | Telemetry (owner request 2026-07-13): (a) a connection history — timestamped connect / replaced / disconnect events with source address, shown in the window and counted as total connections; (b) a **sound-bites counter**: the wire is one continuous PCM stream, so bites are detected as non-silent segments of the played audio (level above −40 dBFS sustained ≥ 0.3 s starts a bite; ≥ 1 s back under the threshold ends it); (c) indicator lights — SOURCE (lit while a client connection is active) and SIGNAL (lit while audio is non-silent). All three are also exposed in `/status` (`total_connections`, `sound_bites`, `source_connected`). | Owner request; RGAS §7.5 |

## 3. Acceptance mapping

| RGAS §8 test | Verified by |
|---|---|
| 8.1 Latency (< 300 ms) | S-4 buffer (150 ms default) + S-5 anchoring, per §4 budget |
| 8.2 Endurance (60 min, zero dropouts) | S-4 + S-11 power/WiFi hardening; field test |
| 8.4 Self-heal (10 s WiFi kill) | S-3 (reconnect replaces), S-4 (silence + refill); field test |
| 8.5 Cold boot (< 2 min, no interaction) | S-11..S-13; field test: power cycle, verify audio |

## 4. Implementation map

| Requirement | File |
|---|---|
| S-1 | [app/RgasReceiver.csproj](app/RgasReceiver.csproj) (publish settings) |
| S-2, S-3 | [app/ReceiverCore.cs](app/ReceiverCore.cs) (accept/reader loops) |
| S-4, S-5, S-6 | [app/JitterBuffer.cs](app/JitterBuffer.cs), [app/Player.cs](app/Player.cs) |
| S-7 | [app/StatusServer.cs](app/StatusServer.cs) |
| S-8 | [app/Config.cs](app/Config.cs), [config.example.json](config.example.json) |
| S-9 | [app/Log.cs](app/Log.cs) |
| S-11, S-13, S-14 | [install.ps1](install.ps1) |
| S-12 | [supervisor.ps1](supervisor.ps1) |
| S-15 | [scripts/diagnose.ps1](scripts/diagnose.ps1) |
| S-16 | [app/MainWindow.xaml](app/MainWindow.xaml), [app/MainWindow.xaml.cs](app/MainWindow.xaml.cs), [app/App.xaml](app/App.xaml) |
| S-17 | [app/JitterBuffer.cs](app/JitterBuffer.cs) (bite/level detection), [app/ReceiverCore.cs](app/ReceiverCore.cs) (connection history), window + status endpoint |
