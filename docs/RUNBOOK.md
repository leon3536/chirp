# RGAS Runbook (Leon)

Maintainer procedures for the Snapcast build (RGAS §7.5). Operator-facing
instructions live on the [operator card](OPERATOR-CARD.md).

## 1. Buffer tuning (RGAS §3.1, §4, §8)

The single knob is `buffer` in `/etc/snapserver.conf` on the booth (default
150 ms, spec range 80–250).

1. SSH to the booth, edit `/etc/snapserver.conf`, change `buffer`.
2. `sudo systemctl restart snapserver` (snapclient reconnects on its own).
3. Re-run the latency measurement (RGAS §8.1): record button-click sound and PA
   output on one phone, measure the offset in Audacity. Total must stay < 300 ms.
4. Walk **down** in 25 ms steps toward 100 on a clean link; go back **up** at the
   first audible dropout during a 60-minute endurance run (RGAS §8.2). Stability
   wins over latency — up to 250 is acceptable.

## 2. Adding or changing clips (client SPEC C-17..C-20)

On any machine with ffmpeg + Python (offline task — never at game time):

1. Drop source files into `client/library/raw/`.
2. Edit `client/library/clips.yaml` (format: [clips.example.yaml](../client/library/clips.example.yaml)):
   pick `start`/`duration` so the clip opens on its hook, not its intro.
3. From `client/`: `python tools/build_clips.py`
   — trims, normalizes to −14 LUFS, writes `library/clips/*.wav` + `manifest.json`.
4. Copy `library/clips/` to the rinkside laptop (or rebuild there) and restart
   the soundboard (close the minimized backend window, double-click the icon).
5. Sanity-check: every new button plays at the same perceived volume.

## 3. Swapping the rinkside machine (RGAS §5)

Any Windows laptop becomes rgas-source (client SPEC C-15, C-16):

1. Prereqs per [client/docs/VB-CABLE.md](../client/docs/VB-CABLE.md)
   (VB-Cable, ffmpeg, Python).
2. Copy the whole `client/` folder over (it contains `config.yaml` and the
   built `library/clips/`).
3. Desktop shortcut to `client\launcher\RGAS-Soundboard.bat`.
4. Double-click, confirm audio in the booth. The booth needs no changes.

## 4. Diagnostics

**Booth (SSH):** `server/scripts/diagnose.sh` — services, :4953 listener,
whether the rinkside push is currently connected, ALSA devices, WiFi power-save
state, recent logs.

**Snapweb:** `http://<booth-ip>:1780` — shows the Rinkside stream and the
loopback client with live status. Diagnostics only; never an operator surface
(RGAS §3.1).

**Rinkside:** the two minimized windows started by the launcher are the backend
and the push loop; their consoles show live logs. The push loop prints a
timestamped line at every ffmpeg restart — a burst of restarts = WiFi trouble.

Triage order for "buttons work, rink silent":

1. Soundboard status bar green? (backend up)
2. Push-loop window: connect/retry spam → rinkside WiFi or booth IP wrong.
3. `diagnose.sh`: is there an ESTABLISHED connection on :4953? Is snapclient
   connected?
4. Snapweb: stream `Rinkside` idle vs playing; client muted/volume.
5. Cable from booth jack to mixer/WiiM (out of software scope — it's labeled).

## 5. Booth reinstall / re-provision

`sudo server/install.sh` on the booth is idempotent (server SPEC S-11) — safe
to re-run after any config drift. Options (soundcard pinning, static IP) in
[server/README.md](../server/README.md).

## 6. Acceptance test checklist (RGAS §8)

Run after any material change (buffer, AP, laptop swap):

- [ ] Latency < 300 ms (§8.1)
- [ ] 60 min endurance, zero dropouts (§8.2)
- [ ] 20 clip triggers after 30 s idle gaps, no clipped starts (§8.3)
- [ ] 10 s WiFi kill mid-playback self-heals (§8.4)
- [ ] Booth cold boot to working audio < 2 min, no login (§8.5)
- [ ] Un-briefed volunteer runs warmup + goal horn from the card (§8.6)
