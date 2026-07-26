# rgas-source (client)

The interchangeable rinkside app: a single portable Windows 11 executable that
is the soundboard, the clip pipeline, and the booth push in one (SPEC C-1/C-2).
No installs — no Python, no ffmpeg, no VB-Cable.

Spec: [SPEC.md](SPEC.md) · Master spec: [../spec/RGAS.md](../spec/RGAS.md)

## Setup on a new laptop

1. Copy a folder containing `RgasSoundboard.exe` onto the laptop (USB stick is fine).
2. Double-click it. First run writes a default `config.json` next to the exe.
3. Click **SCAN** to find the booth on the network (C-34), or set `booth_ip`
   in `config.json` by hand.

That's the whole setup. The clip library lives in `library/` next to the exe;
copy that folder along with the exe to move machines (C-19).

## Game time (volunteers)

- **Modes**: `1` PUMP UP · `2` STOP-IN-ACTION · `3` RELAX · `4` SILENCE
- **Space**: play / fade-out (play-clock semantics)
- **H (hold)**: goal horn — loops while held, finishes naturally on release
- **BACKGROUND HOTKEYS** (top-right): keys work even when another app has focus
  (C-6) — this is a passive subscription, not a capture, so the focused app
  (e.g. a scorekeeping program) still receives every key exactly as normal.
- **Speaker** button: STREAM ONLY (game default) → PLAYBACK + STREAM → PLAYBACK ONLY (C-32)
- No booth? The app shows a warning and everything else still works (C-35).

## Adding clips (CAPTURE tab)

Record whatever the laptop is playing, or import/drop an audio file; trim with
the sliders, preview (local only), name it, tick collections, save. Saving
loudness-normalizes to −16 LUFS so every clip plays at the same level (C-25).
Collections belong to one mode; clips can be in many collections (C-16).

## The library folder — handle with care

`library\` (next to the exe, e.g. `client\publish\library\`) holds the real,
irreplaceable clip collection: normalized WAVs plus `library.json` with names,
collections, and mode assignments. It is deliberately **not in git** (C-21) and
is **not covered by OneDrive** (the repo lives outside the OneDrive sync scope).

- **Do not move, rename, restructure, or delete the `library\` folder — or
  anything in it — unless explicitly asked to.** This applies to humans and to
  coding agents working in this repo. All library changes go through the app.
- **`git clean -fdx` will delete it.** Gitignored files are exactly what
  `-x` removes; never run it under `client\` without backing the library up.
- Republishing (`dotnet publish -o ..\publish`) is safe — it overwrites the exe
  and leaves `library\` and `config.json` alone.
- To back up or move machines: copy the whole folder (exe + `config.json` +
  `library\`). If you park a copy in OneDrive, right-click it and choose
  **"Always keep on this device"** — Files On-Demand placeholders would fail at
  the rink where there is no internet (C-7).

## Building from source

```powershell
# from client/app — needs the .NET 8 SDK (dev machine only)
dotnet build                                   # debug build
dotnet publish -c Release -o ..\publish        # the single portable exe (C-2)
.\bin\Debug\net8.0-windows\win-x64\RgasSoundboard.exe --selftest   # headless checks
```

Audio never enters git except the embedded horn assets (C-20/C-21).

## Deploying to QA

```powershell
.\deploy-qa.ps1            # publish + copy the exe into the QA folder
.\deploy-qa.ps1 -Launch    # ...and run it from there afterward
```

Publishes the exe and copies it into the QA install (OneDrive, "Always keep on
this device"), which holds the real `config.json` (with the announcer API key)
and real `library\`. The script only ever overwrites the exe/pdb — it never
touches `config.json` or `library\` there.
