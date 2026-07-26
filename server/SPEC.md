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
| S-3a | Bug fix 2026-07-24: before closing a replaced source's socket, the receiver writes a short kick marker (`RGAS-KICKED\n`) to it. This lets the replaced rinkside app (client SPEC C-31a) tell "deliberately replaced" apart from a plain network blip and stop its C-31 auto-retry immediately, instead of reconnecting and re-kicking whichever client just took over (an infinite ping-pong when two rinkside laptops target the same booth). Best-effort: a 500 ms send timeout on the old socket ensures a stuck peer can never delay accepting the new source. | RGAS §3.1, §5; bug fix |
| S-4 | Jitter buffer: play out only after `buffer_ms` (default 150) is buffered; on underrun, play silence and re-fill to `buffer_ms` before resuming (no crash, no stall). Underruns are counted. | RGAS §3.1, §4 |
| S-5 | Latency anchoring: if the buffer sustains fill above `2 × buffer_ms + 200 ms`, trim it back to `buffer_ms` so end-to-end latency cannot creep after network hiccups. | RGAS §3.1, §4 |
| S-6 | WASAPI output; device chosen by case-insensitive substring `output_device_match` from config, else the system default. Selection is logged, and all active render devices are enumerated to the log at startup (so the right `output_device_match` value is easy to find). | RGAS §3.1, §3.2 |
| S-6a | Device changes are followed at runtime (owner request 2026-07-14): with no pin configured, changing the Windows default output re-routes playback to the new default within ~1 s; with a pin, plugging the pinned device back in re-claims it. A dead render path (device removed, WASAPI error) is rebuilt automatically — never a silent-dashboard/dead-PA state. Every switch is logged and reflected in the window footer and `/status`. | RGAS §3.2, §5; owner request |
| S-6b | Optional endpoint-volume pin: config `output_volume_percent` (absent/null = leave the device alone). When set, the receiver re-asserts that volume and un-mutes the endpoint every time it (re)claims a device — so an accidentally muted or nudged output self-heals on the next boot or device switch. Applied volume is logged. | RGAS §3.2, §5 (volunteer error) |
| S-7 | Status endpoint on `http://0.0.0.0:<status_port>` (default 1780): `GET /status` returns JSON — state (waiting/filling/playing), source address, buffer fill ms, underrun count, output device, uptime. Diagnostics only; never an operator surface. Implemented without HTTP.sys URL ACLs (plain TCP) so no extra OS config is needed. | RGAS §3.1, §7.5 |
| S-8 | All tunables in `config.json` next to the executable: `listen_port`, `buffer_ms`, `output_device_match`, `status_port`. Missing file or keys fall back to defaults, logged. | RGAS §3.1, §5 |
| S-9 | Logs to console and to a size-capped `logs/receiver.log` next to the executable (connection events, underruns, device selection) for after-the-fact triage. | RGAS §7.5 |
| S-10 | No cloud or internet dependency at runtime; fully functional with building internet down. | RGAS §1 Non-goals |

### Unattended operation (Windows 11 Pro)

| ID | Requirement | Trace |
|---|---|---|
| S-11 | Idempotent PowerShell install script (run as admin) that configures: inbound firewall allow rules (`listen_port`, `status_port`) — AND first removes any Windows auto-created per-exe `RgasReceiver` rule, since a dismissed firewall prompt leaves a **Block** rule that overrides the allows and kills external access while localhost still works (the diagnostics script flags this too); power settings — sleep/hibernate off on **AC and DC** (the battery must bridge an outage, never sleep through it), lid close = do nothing (AC and DC); WiFi adapter power management off and wireless power policy at maximum performance; **PA-hygiene** (added 2026-07-14) — Windows sound scheme "No Sounds", startup boot chime disabled, and communications ducking "do nothing" so nothing dings the rink or attenuates the PA feed; **mono output** (Ease-of-Access mono mix) so a single mixer channel gets L+R summed; **output level pinned to 100** (owner request 2026-07-25) — `config.json`'s `output_volume_percent` (S-6b) is forced to 100 on every install run, since the booth's WASAPI endpoint level must always be 100 and loudness is controlled downstream at the mixer, never at the Windows endpoint; **"Complete Windows setup" nags off** — the welcome-experience / finish-setup / tips notification toggles (ContentDeliveryManager + ScoobeSystemSettingEnabled), so post-update screens never steal focus from the dashboard; **Do Not Disturb** (owner request 2026-07-25) — toast notifications disabled (per-user `ToastEnabled` plus the `NoToastApplicationNotification` policy), so no banner (Windows Update, low battery, Bluetooth, mail, etc.) can ever pop over the dashboard or steal focus; **CPU lock** — processor min=max=100% on AC to minimize DPC-latency audio stutter; **Windows Update** — active hours 23:00–05:00 (owner request 2026-07-25: the booth's actual overnight-game usage window; Windows supports the midnight-crossing span) plus `NoAutoRebootWithLoggedOnUsers=1` as the real guarantee against a mid-game forced restart; auto-start via a **Startup-folder shortcut** (`shell:startup`) to the supervisor — deliberately not a service or Scheduled Task (owner decision 2026-07-13: no background machinery beyond the receiver process itself; the shortcut is visible and trivially removable). Re-running is safe. | RGAS §3.2, §7.1; owner decision |
| S-12 | Supervisor loop: restarts the receiver within 2 s of any exit, forever; logs restarts. Launched by the S-11 startup shortcut as a **minimized console window titled "RGAS Booth Receiver"** — never hidden, so a glance at the taskbar answers "is it running?", and clicking it shows live logs. Also runnable by double-click. | RGAS §3.2, §5, §7.5 |
| S-13 | Boot straight to working audio with no interaction, within 2 minutes: auto-login + S-11 startup shortcut + S-12. Auto-login is driven by the **Winlogon** keys (`AutoAdminLogon=1`, `DefaultUserName`, `DefaultDomainName`, `DefaultPassword`) — NOT by `DevicePasswordLessBuildVersion`, which only un-hides the netplwiz checkbox and lifts the Hello-only sign-in requirement. The install script sets both: passwordless=0 (ungate) AND, when `-AutoLogonUser`/`-AutoLogonPassword` are supplied, the Winlogon keys (deterministic, hands-off; password stored plaintext in-registry — accepted for a locked-booth appliance). Without credentials it prints the manual netplwiz path. `AutoLogonCount` is cleared so sign-in doesn't expire after N boots. | RGAS §3.2, §5, §8.5; bug fix 2026-07-14 |
| S-13a | One setup step is **not** OS-scriptable: BIOS "Restore on AC Power Loss = Power On" (auto power-up after an outage, item 1). The install script prints this as a required manual BIOS step; everything else in the booth setup checklist is applied directly. | RGAS §5 (booth power loss) |
| S-14 | Static IP hook: install script accepts `-StaticIp/-PrefixLength/-Gateway/-InterfaceAlias` and applies them; default is DHCP (site uses a DHCP reservation). The rinkside app targets this address. | RGAS §3.2 |
| S-15 | Read-only diagnostics script: startup-shortcut presence, receiver process, :4953 listener + established source connection, `/status` JSON, firewall rules, power-save state, tail of logs. Enough to triage remotely without touching config. | RGAS §7.5 |
| S-16 | Status window: the receiver is a windowed app (owner request 2026-07-13) showing at a glance — color-coded state (WAITING / BUFFERING / ON AIR), connected source, buffer fill vs target, live L/R output level from the actual PCM, underruns, output device, uptime, and a live log pane. The footer shows the machine's real LAN IP for the `/status` URL, discovered without requiring internet (route probe, then a non-loopback-adapter fallback) — never `127.0.0.1` when a LAN address exists, since RGAS runs internet-down. Read-only: no controls that could break audio (config stays in `config.json`). Closing the window exits the process; the S-12 supervisor restarts it within 2 s, so the window is effectively always present on the booth desktop. | RGAS §3.2, §7.5; owner request |
| S-18 | Single-instance guard (bug fix 2026-07-14): only one receiver process ever binds the ports. A named system mutex is taken at startup; a second launch (e.g. the logon Startup shortcut firing while the installer-started copy is already up, or a duplicate supervisor) detects the holder and exits **silently with code 0** — no dialog, no crash. A genuine bind failure (a foreign app on the port) is handled non-modally: it is logged and shown in the window's state area, and the listener retries rather than crash-looping. No modal error dialog is ever shown (a dialog inside the 2 s supervisor restart loop is itself the failure). | RGAS §5; bug fix |
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
| S-2, S-3, S-3a | [app/ReceiverCore.cs](app/ReceiverCore.cs) (accept/reader loops) |
| S-4, S-5, S-6, S-6a | [app/JitterBuffer.cs](app/JitterBuffer.cs), [app/Player.cs](app/Player.cs) |
| S-7 | [app/StatusServer.cs](app/StatusServer.cs) |
| S-8 | [app/Config.cs](app/Config.cs), [config.example.json](config.example.json) |
| S-9 | [app/Log.cs](app/Log.cs) |
| S-11, S-13, S-14 | [deploy/install.ps1](deploy/install.ps1) (ships flat inside the published folder) |
| S-12 | [deploy/supervisor.ps1](deploy/supervisor.ps1) |
| S-15 | [deploy/diagnose.ps1](deploy/diagnose.ps1) |
| Deployable-folder packaging | [app/RgasReceiver.csproj](app/RgasReceiver.csproj) `CopyDeployFiles` target; [deploy/readme.txt](deploy/readme.txt) |
| S-16 | [app/MainWindow.xaml](app/MainWindow.xaml), [app/MainWindow.xaml.cs](app/MainWindow.xaml.cs), [app/App.xaml](app/App.xaml) |
| S-17 | [app/JitterBuffer.cs](app/JitterBuffer.cs) (bite/level detection), [app/ReceiverCore.cs](app/ReceiverCore.cs) (connection history), window + status endpoint |
