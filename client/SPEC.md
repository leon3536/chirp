# rgas-source (client) — App Specification

Version: 2.0 — supersedes v1 (C-1..C-21 of the previous revision) in full; all
requirements renumbered.
Derived from: [../spec/RGAS.md](../spec/RGAS.md) v0.1 draft, amended by client-owner
decisions of 2026-07-13 (see §6 Deviations).
Scope: everything on the interchangeable rinkside Windows 11 laptop — a single
native soundboard app (playback, hotkeys, capture/clip pipeline, booth push).
Out of scope: the booth machine (see [../server/SPEC.md](../server/SPEC.md)).

## 1. Purpose

Give untrained rotating volunteers a zero-training surface (RGAS §1 Goal 2):
open the laptop, double-click one executable, use four modes and a spacebar —
the same muscle memory as running a play clock. The app decodes and mixes all
game audio itself and pushes the mixed stream directly to the booth over TCP.
Any Windows 11 laptop that can run one portable executable can be the source
machine (RGAS §1 Goal 5, §5) — no drivers, no runtimes, no installers.

## 2. Requirements

### Platform & deployment

| ID | Requirement | Trace |
|---|---|---|
| C-1 | Native Windows 11 desktop application. Not a web app, not a phone app; no other OS targets. | Owner decision |
| C-2 | Ships as a single self-contained portable executable: copy to any folder and double-click. No installer, no admin rights, no prerequisite installs (no Python, no ffmpeg, no VB-Cable, no separate runtime). | RGAS §5 (laptop swap), §7.2 |
| C-3 | Touch-screen friendly: every game-time action (play/stop, mode switch, horn, collection select, arm toggle, speaker mode) has a large on-screen touch target. Hotkeys are an accelerator, never the only path. | RGAS §3.5 |
| C-4 | One window, two tabs: **Soundboard** (game-time surface) and **Capture** (clip pipeline, C-22..C-28). No settings beyond what this spec places on each tab; tuning values live in the config file (C-33). | RGAS §3.5 |
| C-36 | Fully resizable window: the layout and all controls (buttons, waveform, indicators) scale proportionally with window size — no fixed-pixel layouts, no clipped or hidden controls at any size. Designed for a minimum resolution of 1920×1080; below that, correct scaling is best-effort. Maximized on first launch. | Owner decision |
| C-38 | **Live spectrum analyzer** (graphic-equalizer style) top-right in the window header: log-spaced bands (~20, 50 Hz–16 kHz) computed by FFT from the **actual mixed output** (music + horn), with peak-hold caps. Purely visual — no audio-path impact. | Owner decision |

### Modes & hotkeys

| ID | Requirement | Trace |
|---|---|---|
| C-5 | Four exclusive modes, switched by keys `1`–`4` or on-screen buttons: **1 PUMP UP**, **2 STOP-IN-ACTION**, **3 RELAX**, **4 SILENCE**. The active mode is always visually obvious. | Owner decision; replaces RGAS §3.5 fixed groups |
| C-6 | **Background key capture is a toggleable armed mode.** When armed, a system-wide low-level keyboard hook captures `Space`, `1`–`4`, and `H` even while the app is unfocused or minimized, and consumes them (the focused app never sees them). When disarmed, keys work only while the app has focus. Toggled by an on-screen button with an unmistakable armed/disarmed visual state. Safe because scorekeeping is mouse/touch-only. | Owner decision |
| C-7 | While armed, hotkeys are suppressed whenever a text-input field inside this app has keyboard focus (e.g. naming a clip on the Capture tab). | Derived from C-6 |
| C-8 | `Space` toggles playback (play-clock semantics): idle → start the next track from the active mode's pool; playing → fade out (C-12). In mode 4 (SILENCE), `Space` does nothing. | Owner decision |
| C-9 | `H` is a **hold-to-sound** goal horn, digitally mixed **over** current playback: key-down starts the horn instantly (attack part); the sustain part loops seamlessly for as long as `H` is held; key-up finishes with the horn's natural decay tail — the horn is never cut abruptly and never faded artificially. Music continues underneath, ducked by the configured amount (default −6 dB) from key-down until the tail ends, then restores. Pressing `H` again during the tail restarts from the attack. A tap (immediate release) still yields a full-sounding blast: attack, then tail. The on-screen horn button behaves identically (press-and-hold). | RGAS §3.5, amended |

### Playback semantics

| ID | Requirement | Trace |
|---|---|---|
| C-10 | Per-mode order toggle, visible on the Soundboard tab: **shuffle** (no repeats until the pool is exhausted) or **sequential** (stable listed order). Persisted across restarts. | Owner decision |
| C-11 | Per-mode advance behavior, set in config (not game UI): **continuous** (auto-advance through the pool, looping) or **single-shot** (stop at natural track end). Defaults: mode 1 continuous, mode 2 single-shot, mode 3 continuous. | Owner decision |
| C-12 | Every human-initiated stop is a graceful fade-out, default 2 s, never an abrupt cut. The only instant-onset sound is the horn (C-9). | RGAS §3.5 |
| C-13 | Switching modes while audio is playing crossfades (default 2 s) immediately into the next track of the new mode. Switching to mode 4 fades to silence. Switching while idle just changes the armed pool. | Owner decision |
| C-14 | Mixing engine: clips pre-decoded/cached in memory as 48 kHz 16-bit stereo; mixed in a low-latency callback; a soft limiter on the master bus prevents clipping when horn and music sum. No decode-on-trigger. | RGAS §4, §3.6 |
| C-15 | No cloud dependencies; fully functional with building internet down. | RGAS §3.5 |

### Collections & library

| ID | Requirement | Trace |
|---|---|---|
| C-16 | A clip can belong to **many collections**; a collection belongs to **exactly one** of modes 1–3. Mode 4 (SILENCE) has no collections. | Owner decision |
| C-17 | The Soundboard tab lets the operator select/deselect collections per mode. The last selected collection of a mode **cannot** be deselected while that mode has at least one collection available — a mode is never accidentally left empty. A mode with zero collections defined is allowed; `Space` then no-ops with a visible hint. | Owner decision |
| C-18 | The active pool = union of the selected collections of the active mode. | Owner decision |
| C-37 | **Collection browser** dominates the Soundboard surface (~70%, below compact mode/transport/horn controls): left, the active mode's collections, each with an activation checkbox (C-17 rule enforced); right, the clips of the highlighted collection as a playlist — **each row carries its own play control that toggles to a stop control while that clip is playing** (standard playlist convention; no separate play/stop button pair). Impromptu play replaces current playback with the configured crossfade (instant when idle); what happens at the clip's natural end follows the active mode's advance behavior (C-11). | Owner decision |
| C-39 | **Context menus** for in-place library management, all destructive actions confirmed first. Soundboard: right-press a collection → *delete collection* (its clips stay in the library; if the mode retains collections but none is selected, one is auto-selected to honor C-17); right-press a clip row → *remove from this collection* / *delete from library*. Capture tab: the library list's context menu toggles the clip's membership per collection (checkmark = member) and offers *delete from library*. | Owner decision |
| C-19 | Library persisted under `library/`: audio as 48 kHz 16-bit stereo WAV plus `library/library.json` (clip id, file, label, duration, collection memberships; collection id, name, mode, selected-state; per-mode order toggle). Survives restarts. | RGAS §3.6, amended |
| C-20 | The goal horn is a built-in asset shipped inside the repo/executable — the **only** audio committed to the repo. It is a three-part sampler set supporting C-9: `horn_attack.wav`, `horn_loop.wav` (seam pre-crossfaded for gapless butt-looping), `horn_tail.wav` (natural decay), 48 kHz 16-bit stereo, extracted from a Pixabay-licensed freesound_community horn recording (no attribution required). | Owner decision |
| C-21 | No audio other than the horn asset enters git: `library/` and all `*.mp3` / `*.wav` / `*.flac` / `*.m4a` are gitignored (copyright). Enforced by the repo `.gitignore`. | Owner decision |

### Capture tab (clip pipeline)

| ID | Requirement | Trace |
|---|---|---|
| C-22 | Record system audio via WASAPI loopback of the default output device — captures whatever the laptop is playing (browser, Spotify, etc.). No virtual-cable driver needed. | Owner decision; replaces RGAS §3.6 offline pipeline |
| C-23 | Import audio files as an alternative to recording: file picker and drag-and-drop; accepts at least mp3/wav/flac/m4a. Same normalization path as recordings (C-25). | Owner decision |
| C-24 | Trim editor: waveform display, draggable start/end markers, preview, save. Preview plays on the local default device only — never pushed to the booth. | Owner decision |
| C-25 | On save, clips are loudness-normalized to a single music target (default −16 LUFS integrated) and encoded 48 kHz 16-bit stereo WAV. The horn asset is mastered hotter (−11 LUFS) so the horn always reads louder than music. | RGAS §3.6, amended targets |
| C-26 | At save (or any time later), a clip can be assigned to one or more collections, including creating a new collection inline (name + mode). | Owner decision |
| C-27 | The Capture tab is visible and usable by volunteers — no gate, no PIN. Growing the library at the rink is a feature. | Owner decision |
| C-28 | Recording must never capture the soundboard's own output: while a loopback recording is in progress, the soundboard's local render (Speaker modes with PLAYBACK, C-32) is auto-muted — the booth stream is unaffected. Trim preview (C-24) is likewise unavailable while recording. | Derived |

### Booth push

| ID | Requirement | Trace |
|---|---|---|
| C-29 | The app pushes its own mixed output as raw s16le 48 kHz stereo PCM directly to `tcp://<booth-ip>:4953`. No VB-Cable, no ffmpeg, no capture hop. The booth side is unchanged. | RGAS §3.4, superseded (see §6) |
| C-30 | The stream is continuous: silence frames are pushed when nothing is playing, so snapserver's buffer stays primed and clip starts are never clipped. | RGAS §8.3 |
| C-31 | On any disconnect the app reconnects within 1 s, retrying forever, with no operator action (WiFi blip self-heals). A connection-status indicator is always visible on the Soundboard tab. | RGAS §3.4, §5, §8.4 |
| C-32 | A **Speaker button** on the Soundboard tab cycles three output modes, with the active mode always visible: **STREAM ONLY** (booth only — game default), **PLAYBACK + STREAM** (booth + local default output device), **PLAYBACK ONLY** (local only — booth-less use, e.g. practice or testing). Persisted across restarts; the fresh-install default comes from config (C-33). In every mode the booth connection stays up and primed per C-30–C-31 (PLAYBACK ONLY pushes silence), so switching modes is instant and never re-buffers. | Owner decision |
| C-33 | One config file next to the executable (`config.json`): booth IP/port, fade and crossfade durations, horn duck dB, loudness targets, per-mode advance behavior, default speaker mode. No values hardcoded. | RGAS §3.3, §5 |
| C-34 | **Booth discovery**: if no booth address is configured (or the configured one is unreachable), a *Scan* action next to the connection indicator sweeps the local subnet for the snapcast stream port (TCP :4953) and offers any host found; accepting it persists the address to config. Manual config entry always remains possible. | RGAS §5 (laptop swap) |
| C-35 | **The booth is optional**: with no booth reachable, the app shows a persistent, non-blocking warning on the connection indicator and everything else works — capture, trim, library management, and local playback (Speaker: PLAYBACK ONLY, C-32). No feature may block, error out, or crash because the booth is absent; when a booth appears, streaming starts without a restart (C-31 retry loop). | RGAS §5, owner decision |

## 3. Latency budget (revised)

Direct push removes the VB-Cable and ffmpeg/dshow stages from RGAS §4:

| Stage | Estimate |
|---|---|
| Trigger → mix callback → TCP write | 10–25 ms |
| WiFi transit | 5–20 ms |
| Snapcast buffer | 150 ms (tunable 80–250) |
| Booth audio out | ~10 ms |
| **Total** | **~175–205 ms** |

## 4. Acceptance mapping

| RGAS §8 test | Verified by |
|---|---|
| 8.1 Latency (< 300 ms button-to-PA) | C-14 + C-29 budget lines; field measurement per RGAS §8.1 |
| 8.2 Endurance (60 min, zero dropouts) | C-30 continuous stream + C-31; field test |
| 8.3 Clip start integrity (20 triggers, no clipped starts) | C-14 (in-memory decode) + C-30 (primed stream) |
| 8.4 Self-heal (10 s WiFi kill) | C-31; field test |
| 8.6 Volunteer test | C-3, C-5..C-9, C-17 + operator card |

## 5. Implementation map

Reference stack: .NET 8 WPF, self-contained single-file publish
(`-p:PublishSingleFile=true --self-contained`), NAudio for WASAPI
render/loopback, Win32 `WH_KEYBOARD_LL` for the global hook.

| Requirement | File |
|---|---|
| C-1, C-2 | [app/RgasSoundboard.csproj](app/RgasSoundboard.csproj) (publish settings) |
| C-3, C-4, C-5, C-10, C-17, C-32, C-36, C-37 (UI) | [app/MainWindow.xaml](app/MainWindow.xaml), [app/UI/SoundboardView.xaml](app/UI/SoundboardView.xaml) |
| C-6, C-7 | [app/Hotkeys/GlobalKeyboardHook.cs](app/Hotkeys/GlobalKeyboardHook.cs) |
| C-8, C-9, C-11..C-14, C-18, C-37 (engine), C-38 (FFT feed) | [app/Audio/AudioEngine.cs](app/Audio/AudioEngine.cs) |
| C-38 (display) | [app/MainWindow.xaml](app/MainWindow.xaml), [app/MainWindow.xaml.cs](app/MainWindow.xaml.cs) |
| C-16, C-17, C-19, C-39 (store ops) | [app/Library/LibraryStore.cs](app/Library/LibraryStore.cs) |
| C-39 (menus) | [app/UI/SoundboardView.xaml.cs](app/UI/SoundboardView.xaml.cs), [app/UI/CaptureView.xaml.cs](app/UI/CaptureView.xaml.cs) |
| C-20 | [app/assets/horn_attack.wav](app/assets/horn_attack.wav), [app/assets/horn_loop.wav](app/assets/horn_loop.wav), [app/assets/horn_tail.wav](app/assets/horn_tail.wav) |
| C-21 | [../.gitignore](../.gitignore) |
| C-22, C-28 | [app/Audio/LoopbackRecorder.cs](app/Audio/LoopbackRecorder.cs) |
| C-23, C-24, C-26, C-27 (UI) | [app/UI/CaptureView.xaml](app/UI/CaptureView.xaml) |
| C-25 | [app/Audio/Normalizer.cs](app/Audio/Normalizer.cs) |
| C-29..C-32, C-35 | [app/Audio/BoothPusher.cs](app/Audio/BoothPusher.cs) |
| C-34 | [app/Audio/BoothDiscovery.cs](app/Audio/BoothDiscovery.cs) |
| C-33 | [app/Config.cs](app/Config.cs), [config.example.json](config.example.json) |

## 6. Deviations from RGAS v0.1 (input for the next master-spec revision)

1. **§3.4 audio push**: VB-Cable + ffmpeg replaced by direct in-app TCP push
   (C-29). Rationale: the app already owns the mixed PCM; the capture hop only
   existed to extract it. Removing it satisfies the zero-install deployment
   requirement (C-2) and cuts ~40 ms from the latency budget. The booth
   contract (raw s16le 48 kHz stereo on :4953) is unchanged.
2. **§3.5 frontend**: web UI operable from phones replaced by a native
   Windows 11 touch app (C-1). The phone/tablet operator surface is dropped.
3. **§3.5 fixed groups**: WARMUP / GOAL HORN / POWER PLAY / PENALTY KILL /
   STOPPAGE / MISC replaced by four modes + user-managed collections (C-5,
   C-16). Operator model changes from "press the labeled button for a clip" to
   "pick a mode, spacebar plays/stops" (play-clock semantics).
4. **§3.6 clip pipeline**: the offline YAML + build-script pipeline replaced by
   the in-app Capture tab (C-22..C-26); library growth is now a rink-side
   activity open to volunteers.
5. **§3.5 controls**: master gain and the PIN gate are dropped (native app —
   no LAN-exposed UI to protect); playlist crossfade becomes mode-switch
   crossfade (C-13). Loudness target moves from −14 to −16 LUFS for music with
   the horn at −11 LUFS, so the horn reads louder over a mixed music bed
   (C-25).
