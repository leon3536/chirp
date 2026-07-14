# RGAS — Rink Game Audio System

Low-latency (< 300 ms) game-audio system for TVMHA: a web soundboard at the
scorekeeper bench drives the booth PA over LAN via Snapcast.

Master specification: [spec/RGAS.md](spec/RGAS.md) (v0.1 draft, Leon Zhao).

## Repository layout

Two apps, each developed spec-first:

| App | Role | Spec |
|---|---|---|
| [server/](server/) | **rgas-booth** — Debian 12 booth machine: snapserver, loopback snapclient, unattended-operation hardening | [server/SPEC.md](server/SPEC.md) |
| [client/](client/) | **rgas-source** — interchangeable Windows 11 rinkside laptop: single portable native soundboard app (modes + hotkeys, clip capture tab, direct TCP push to the booth) | [client/SPEC.md](client/SPEC.md) |

Shared operator/maintainer docs:

- [docs/OPERATOR-CARD.md](docs/OPERATOR-CARD.md) — the one-page laminated card (RGAS §7.4)
- [docs/RUNBOOK.md](docs/RUNBOOK.md) — Leon's runbook: buffer tuning, adding clips, machine swap, diagnostics (RGAS §7.5)

## Spec-based development workflow

1. **The spec is the source of truth.** Each app's `SPEC.md` derives numbered
   requirements (`S-*` for server, `C-*` for client) from the master spec, with
   traceability back to RGAS section numbers.
2. **Change the spec first.** Behavior changes start as a `SPEC.md` edit (new or
   amended requirement ID), then the implementation follows. Code and commits
   reference requirement IDs.
3. **Every requirement maps to code.** Each `SPEC.md` ends with an
   *Implementation map* table (requirement → file) and an *Acceptance mapping*
   (requirement → RGAS §8 acceptance test). A requirement with no row in the map
   is unimplemented by definition.

## Quick start

- Booth: `sudo server/install.sh` on a fresh Debian 12 machine → [server/README.md](server/README.md)
- Rinkside: copy the folder containing `RgasSoundboard.exe` + `config.json` to any Windows 11 laptop and double-click — no installs (client SPEC C-2)
- Clips: record or import them on the app's Capture tab (trim, normalize, assign to collections); audio files never enter git except the horn asset
