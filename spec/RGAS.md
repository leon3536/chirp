# Rink Game Audio System (RGAS) Specification

Version: 0.2 draft
Owner: Leon Zhao, TVMHA
Status: Pre-implementation. Bluetooth-to-WiiM alternative is being field tested; this spec covers the LAN-PCM build that is either the primary system or the fallback.

Revision history:

- 0.2: **All machines are now Windows 11 Pro.** Booth: Snapcast is replaced by a purpose-built receiver — Snapcast ships no official Windows snapserver binary, and with a single output zone (multiroom sync is a §1 non-goal) its remaining role reduces to "TCP PCM in, buffered audio out". Snapweb diagnostics are replaced by a local status endpoint. Rinkside: incorporates the client-owner decisions of 2026-07-13 (client SPEC v2 §6 Deviations) — native single-exe soundboard with modes/hotkeys and direct in-app TCP push, replacing the web UI + VB-Cable + ffmpeg chain and the offline clip pipeline. The booth wire contract (raw s16le 48 kHz stereo over TCP :4953) is unchanged from 0.1.
- 0.1: initial draft (Debian 12 booth, snapserver + loopback snapclient, web soundboard).

## 1. Business Purpose

TVMHA runs game operations (music, goal horn, sound effects, announcements bed) at a rink where:

- The scorekeeper sits rinkside; the audio equipment (mixer, PA, WiiM Pro) is in the announcer booth. Distance and obstructions rule out Bluetooth as a reliable link at soundboard latency.
- Game audio is operated by rotating volunteers with no audio or mixer training. Any workflow that requires touching the mixer, selecting audio sources, or cueing into tracks by hand fails in practice.
- Current music workflow produces bad results: tracks are started from the beginning, intros are weak, levels are inconsistent between songs, and cause-and-effect cues (goal horn) are not possible over the existing 2+ second casting protocols.

### Goals

1. Button-to-PA latency under 300 ms so goal horns and stoppage cues feel immediate.
2. Zero-training operator surface: a web soundboard with labeled buttons and a stop control. No mixer, no WiiM app, no audio settings.
3. Consistent output: every clip pre-trimmed to its usable section and loudness normalized, so no clip is quiet, hot, or opens on a bad intro.
4. Unattended booth: the booth machine boots, self-configures, and recovers from power loss and network blips with no human intervention.
5. Interchangeable rinkside hardware: any laptop that can run one script can be the source machine.

### Non-goals

- Multiroom sync (single output zone).
- Lip sync to video or live monitoring (latency floor of this design does not support it).
- Internet streaming. Everything is LAN-local.
- Replacing the WiiM for casual music streaming outside games.

## 2. System Architecture

```
RINKSIDE (scorekeeper bench)                 BOOTH
+---------------------------+                +----------------------------+
| Windows laptop            |                | ThinkBook, Windows 11 Pro  |
|                           |                |                            |
| Soundboard web UI (any    |    5 GHz WiFi  | RGAS receiver (supervised) |
| browser on laptop/phone)  |   ---------->  |   TCP listen :4953         |
|   -> soundboard backend   |    TCP :4953   |   jitter buffer (150 ms)   |
|   -> VB-Cable virtual dev |    raw PCM     |   |                        |
|   -> ffmpeg capture/push  |                | WASAPI -> 3.5mm/USB DAC    |
+---------------------------+                |   |                        |
                                             | RCA -> mixer channel       |
                                             | (fallback: WiiM line-in)   |
                                             +----------------------------+
```

Roles:

- **Booth machine (fixed node, "rgas-booth")**: runs the RGAS receiver as a supervised auto-starting task. Holds all buffering state. Nobody interacts with it after install.
- **Rinkside machine (interchangeable, "rgas-source")**: runs the soundboard app and pushes PCM to the booth over TCP. All operator interaction happens here or on a phone browsing to it.
- **Network**: dedicated 5 GHz SSID preferred (travel router or AP in the booth). Rink house WiFi is acceptable only if it passes the acceptance tests in section 8.

Audio output decision (site-dependent, resolve at install):

- **Primary**: direct into a permanently assigned, labeled mixer channel. Cleanest path, no source contention.
- **Fallback**: RCA into WiiM Pro line-in, riding the always-open WiiM mixer channel. Adds 20 to 40 ms conversion delay and a source-contention risk (incoming casts can seize the WiiM input). Use only if a dedicated mixer channel cannot be secured.

## 3. Components

### 3.1 Booth: RGAS receiver

- Windows 11 Pro; a single self-contained console executable (`RgasReceiver.exe`) run as a supervised auto-start task. No other audio software on the box.
- Listens on `tcp://0.0.0.0:4953` for raw PCM, s16le 48 kHz stereo. One active source; a **new connection replaces the current one**, so a rinkside laptop swap needs no booth change.
- Jitter buffer: prebuffer `buffer_ms` (default 150, tunable 80 to 250) before playout; on underrun, play silence and rebuffer; sustained overfill is trimmed so latency stays anchored at the target.
- WASAPI output, device pinned by name substring in `config.json` so a plugged or unplugged USB device cannot silently steal the output.
- Status endpoint `http://0.0.0.0:1780/status` (JSON: state, source address, buffer fill, underrun count, output device). Diagnostics only; it is not an operator surface.
- Rationale (0.2): Snapcast has no official Windows snapserver build; with multiroom sync a non-goal, its role here reduces to buffered PCM playout, which this receiver implements directly.

### 3.2 Booth: OS hardening for unattended use (Windows 11 Pro)

- WiFi power save off, persistent (adapter power management off + wireless power policy at maximum performance). This is mandatory; power save is the top cause of periodic dropouts.
- Lid close action: do nothing (AC and battery). Sleep and hibernate off on AC power.
- Auto-login enabled; the receiver starts from a logon scheduled task under a supervisor loop (restart within 2 s). The machine must boot straight to working audio with no interaction.
- Windows Firewall inbound allow rules for 4953 (PCM) and 1780 (status).
- Static IP or DHCP reservation. The rinkside app targets this address.
- Windows Update active hours set so automatic restarts cannot land mid-game (runbook).

### 3.3 Rinkside: soundboard application

Native Windows 11 desktop app (client SPEC v2 is normative for detail):

- Single self-contained portable executable: copy the app folder to any Windows 11 laptop and double-click. No installer, no drivers, no runtimes — no VB-Cable, no ffmpeg.
- Decodes and mixes all game audio in-app, and pushes the mixed output as raw s16le 48 kHz stereo PCM **directly** to `tcp://<booth-ip>:4953`. The stream is continuous (silence frames when idle) so the booth buffer stays primed and clip starts are never clipped.
- On any disconnect the app reconnects within 1 s, retrying forever; a connection-status indicator is always visible.
- Operator model (play-clock semantics): four exclusive modes — **1 PUMP UP, 2 STOP-IN-ACTION, 3 RELAX, 4 SILENCE** — via keys `1`–`4` or on-screen buttons; `Space` toggles play / 2 s fade-out; `H` fires the goal horn mixed over the music with a configured duck. A toggleable armed mode installs a global keyboard hook so hotkeys work while the app is unfocused.
- Clips are organized in user-managed collections (a clip may be in many; a collection belongs to one mode); the active pool is the union of the mode's selected collections, played shuffled or sequential per a visible toggle.
- All tuning (booth IP, fades, duck, loudness targets, advance behavior) lives in `config.json` next to the executable. No cloud dependencies; fully functional with building internet down.

### 3.4 Rinkside: clip capture (in-app pipeline)

- A Capture tab inside the app replaces the offline pipeline: record system audio via WASAPI loopback of the default output device (whatever the laptop plays — browser, Spotify), or import files (mp3/wav/flac/m4a, picker or drag-and-drop).
- Trim editor with waveform and draggable start/end markers; preview locally (never to the booth); save.
- On save, clips are loudness-normalized to −16 LUFS integrated and encoded 48 kHz 16-bit stereo WAV into `library/` with `library.json` metadata. The built-in goal horn is mastered at −11 LUFS so the horn always reads louder than music.
- Library growth is a rink-side activity open to volunteers. Only the horn asset is committed to the repository; all other audio stays out of git (copyright).

## 4. Latency Budget

Direct in-app push removes the VB-Cable and ffmpeg/dshow stages of 0.1:

| Stage | Estimate |
|---|---|
| Trigger to mix callback to TCP write | 10 to 25 ms |
| WiFi transit | 5 to 20 ms |
| Booth jitter buffer | 150 ms (tunable 80 to 250) |
| Booth audio out | ~10 ms |
| WiiM line-in pass-through (fallback path only) | 20 to 40 ms |
| **Total** | **~175 to 205 ms** |

Acceptable for all in-scope cues. If field tests show a clean link, buffer may be walked down toward 100 ms; if the RF environment is hostile, up to 250 ms remains acceptable and stability wins.

## 5. Failure Modes and Required Behavior

| Failure | Required behavior |
|---|---|
| WiFi blip / socket drop | Rinkside app reconnects within ~1 s, retrying forever. Booth plays silence during the gap, no crash, no manual step. |
| Booth power loss | Machine boots (auto-login) to fully working audio with no interaction. Laptop battery bridges short outages. |
| Rinkside laptop swap | Any Windows 11 laptop with the app folder works. Booth needs no changes (new connection replaces the old). |
| Volunteer error | Not possible to break audio from the game-time UI. Worst case is the wrong music; `Space` (fade out) or mode 4 recovers. |
| Someone unplugs booth audio cable | Out of scope for software; label the cable. |

## 6. Security Posture

- The booth receiver has no auth or encryption; the trust boundary is the WiFi network. Dedicated SSID with WPA2 and a non-shared password is the control. Anyone on that network can push audio to the PA — that is the accepted risk, same as 0.1's Snapcast posture.
- The soundboard is a native app on the operator's laptop; there is no LAN-exposed operator UI to protect (the 0.1 web-UI PIN is dropped as moot).
- This posture is strictly better than the Bluetooth alternative, where access control is proximity.

## 7. Deliverables

1. PowerShell install script that configures a fresh Windows 11 Pro ThinkBook into rgas-booth (receiver executable, scheduled task + supervisor, power settings, firewall rules, static IP hook).
2. Rinkside portable app folder: `RgasSoundboard.exe`, `config.json`, built-in horn asset. Copy-and-run.
3. Clip pipeline: the in-app Capture tab (record/import, trim, normalize, collections).
4. One-page laminated operator card (open laptop, double-click icon, modes + spacebar + horn key; who to call).
5. Runbook for Leon: buffer tuning, library management, swapping the rinkside machine, diagnostics via the booth status endpoint.

## 8. Acceptance Tests

1. **Latency**: measured button-press-to-PA under 300 ms (record button click sound and PA output on one phone, measure offset in Audacity).
2. **Endurance**: 60 minutes continuous playback during a public session with zero audible dropouts at the chosen buffer.
3. **Clip start integrity**: 20 consecutive short-clip triggers after 30 s idle gaps; no clipped clip starts.
4. **Self-heal**: kill rinkside WiFi for 10 s mid-playback; audio resumes without operator action.
5. **Cold boot**: power cycle the booth machine; audio path working within 2 minutes, no interaction.
6. **Volunteer test**: a person with no briefing beyond the laminated card successfully runs warmup music and fires the goal horn.

## 9. Open Items

- [ ] Bluetooth-to-WiiM field test result (range, stability at 80 ms setting, hijack behavior). If it passes all of: 30 min clean under real-crowd RF, no clipped starts after idle, and firmware supports locking to a known device, Bluetooth becomes primary and this build becomes the fallback.
- [ ] Mixer channel: secure a permanent labeled channel (preferred) or confirm WiiM line-in fallback, including WiiM source-contention behavior when a cast comes in.
- [ ] Dedicated AP: confirm booth power and mounting; pick hardware (any 5 GHz travel router is sufficient).
- [ ] Confirm rink WiFi characteristics if dedicated AP is not approved.
