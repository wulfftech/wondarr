# Compilarr

**A self-hosted \*arr for single songs.** Tell it which recordings you want (one at a time, a pasted list, a Deezer/YouTube Music playlist, a Spotify CSV export, or your existing music folder). It finds the best copy on **Soulseek** first (bundled slskd), **YouTube Music** second (yt-dlp), and **torrents/usenet** last (qBittorrent, SABnzbd), verifies that the file really is that recording (duration + AcoustID fingerprint), tags it properly, files it into your library in the layout you chose (flat, per artist, per artist/album, or a **Plexamp** preset that keeps albums tidy), and keeps looking for a better copy until your quality cutoff is met.

Conceptually it is Sonarr/Radarr/Lidarr for **one song at a time** — which Lidarr cannot do, by design (its unit is the album).

> **Status (2026-09-28):** Phase 0 (skeleton, auth, jobs, UI shell, bundled slskd, image, CI) is done — `0.0.1-alpha.1`; Phase 1 (song identity and the Wanted list) is next. See `docs/HANDOVER.md` if you are picking this up, and `CLAUDE.md` if you are an AI coding session.

## Quick start (Docker)

Build and run everything (API, UI and the bundled slskd) with the Compose file in `docker/`:

```bash
docker compose -f docker/docker-compose.yml up --build
```

Then open **http://localhost:1077**. Persistent state lives in `/config`, the library in `/data`; `PUID`, `PGID`, `UMASK` and `TZ` control file ownership and time zone, and Soulseek needs port **50300** reachable for incoming connections. The first build downloads the pinned slskd, ffmpeg, `fpcalc` and Deno releases, so it needs network access and takes a few minutes.

## Documentation map

| Read this | For |
|---|---|
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Prerequisites, build/test commands, repository map and contribution rules |
| [`NOTICE.md`](NOTICE.md) | Ported-code attributions and the third-party programs bundled in the image |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in each release |
| [`CLAUDE.md`](CLAUDE.md) | Instructions for AI coding sessions: the orchestrator/worker build workflow, rules, commands |
| [`docs/HANDOVER.md`](docs/HANDOVER.md) | Where things stand and exactly how to start the next session |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) · [`docs/adr/`](docs/adr/) | Every decision taken with the owner, and the architectural ones as ADRs |
| [`docs/product/PRODUCT.md`](docs/product/PRODUCT.md) · [`GOALS.md`](docs/product/GOALS.md) · [`RISKS.md`](docs/product/RISKS.md) | What we are building, what we are not, and the risk register |
| [`docs/architecture/ARCHITECTURE.md`](docs/architecture/ARCHITECTURE.md) | Components, pipeline, plugin interfaces, data model, scheduler, API |
| [`docs/architecture/MATCHING_ENGINE.md`](docs/architecture/MATCHING_ENGINE.md) | Candidate normalisation, rejections, scoring, per-source search strategy, verification |
| [`docs/architecture/LIBRARY_OUTPUT.md`](docs/architecture/LIBRARY_OUTPUT.md) | Layouts, the album policy ("fewest albums"), tag spec, Plex/Plexamp rules, reference libraries |
| [`docs/architecture/STACK.md`](docs/architecture/STACK.md) · [`DEPLOYMENT.md`](docs/architecture/DEPLOYMENT.md) · [`QUALITY_DEFINITIONS.md`](docs/architecture/QUALITY_DEFINITIONS.md) · [`CONFIG_EXAMPLES.md`](docs/architecture/CONFIG_EXAMPLES.md) | Stack and code to port, Docker/bundled slskd, quality seed, reference config |
| [`docs/build/PHASES.md`](docs/build/PHASES.md) · [`PHASE_0_TASKS.md`](docs/build/PHASE_0_TASKS.md) · [`AGENT_WORKFLOW.md`](docs/build/AGENT_WORKFLOW.md) · [`CODING_STANDARDS.md`](docs/build/CODING_STANDARDS.md) · [`REPO_LAYOUT.md`](docs/build/REPO_LAYOUT.md) | How the build is sequenced, the first task list, how AI workers are used, conventions |
| [`docs/build/NEXT_SESSION_PROMPT.md`](docs/build/NEXT_SESSION_PROMPT.md) | The prompt that starts the next build session (currently Phase 1) |
| [`docs/research/FINDINGS.md`](docs/research/FINDINGS.md) + six full reports | The URL-cited research behind every decision |
| [`docs/PLAN.md`](docs/PLAN.md) | The complete planning document (revision 3), frozen as the record of the research phase |

## Planned stack

C# / .NET 10 (ASP.NET Core, EF Core + SQLite) with a React 19 + Vite front end, aligned with the other \*arrs so their GPL-3.0 subsystems can be ported; one Docker image with slskd bundled; HTTP on port **1077**, Soulseek on 50300.

## Licence

GPL-3.0 (see `LICENSE`). slskd is redistributed unmodified under its own AGPL-3.0 licence and additional terms. See [`NOTICE.md`](NOTICE.md) for the full attribution list (ported code and bundled third-party programs) and [`CHANGELOG.md`](CHANGELOG.md) for release notes.
