# Wondarr

**The \*arr for one-hit wonders.** You want *that* song — the stand-alone hit — not the artist's obscure 12-track album of B-sides that Lidarr would pull down to get it. Wondarr is a self-hosted \*arr whose unit is **one song**. Tell it which recordings you want (one at a time, a pasted list, a Deezer/YouTube Music playlist, a Spotify CSV export, or your existing music folder). It finds the best copy on **Soulseek** first (bundled slskd), **YouTube Music** second (yt-dlp), and **torrents/usenet** last (qBittorrent, SABnzbd), verifies that the file really is that recording (duration + AcoustID fingerprint), tags it properly, files it into your library in the layout you chose (flat, per artist, per artist/album, or a **Plexamp** preset that keeps albums tidy), and keeps looking for a better copy until your quality cutoff is met.

Conceptually it is Sonarr/Radarr/Lidarr for **one song at a time** — which Lidarr cannot do, by design (its unit is the album).

> **Status (2026-10-09):** every phase of the plan is built — Soulseek, YouTube Music, torrents and usenet, verified imports, upgrades, import lists, reference libraries, Plex, the mass editor. The release candidate **0.1.0-rc.1** is out (`ghcr.io/wulfftech/wondarr:0.1.0-rc.1`, [release notes](https://github.com/wulfftech/wondarr/releases/tag/v0.1.0-rc.1)); `:latest` follows once 0.1.0 is final. **User documentation: https://wulfftech.github.io/wondarr/** (sources in [`website/`](website/)).

## Quick start (Docker)

```yaml
services:
  wondarr:
    image: ghcr.io/wulfftech/wondarr:0.1.0-rc.1   # :latest once 0.1.0 is final; :develop follows main
    container_name: wondarr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Etc/UTC
    volumes:
      - ./config:/config
      - /srv/data:/data          # your music and every download, on one mount
    ports:
      - "1077:1077"              # web UI and API
      - "50300:50300"            # Soulseek (bundled slskd); forward it on your router
    restart: unless-stopped
```

`docker compose up -d`, then open **http://<host>:1077**, set a login under **Settings → General**, and sign in to Soulseek under **Settings → Soulseek**. The image (linux/amd64, linux/arm64) bundles slskd, ffmpeg, `fpcalc`, Deno and yt-dlp. Step-by-step guides for Plex, qBittorrent, SABnzbd, indexers and Unraid are on the [docs site](https://wulfftech.github.io/wondarr/); `docker/docker-compose.yml` builds the image from source.

## Documentation map

| Read this | For |
|---|---|
| [The docs site](https://wulfftech.github.io/wondarr/) ([`website/`](website/)) | Installing and using Wondarr: Docker, Unraid, Soulseek, Plex, qBittorrent, SABnzbd, indexers, the library, operations, configuration, troubleshooting |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Prerequisites, build/test commands, repository map and contribution rules |
| [`NOTICE.md`](NOTICE.md) | Ported-code attributions and the third-party programs bundled in the image |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in each release |
| [`AGENTS.md`](AGENTS.md) | Instructions for AI coding sessions: the orchestrator/worker build workflow, rules, commands |
| [`docs/HANDOVER.md`](docs/HANDOVER.md) | Where things stand and exactly how to start the next session |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) · [`docs/adr/`](docs/adr/) | Every decision taken with the owner, and the architectural ones as ADRs |
| [`docs/product/PRODUCT.md`](docs/product/PRODUCT.md) · [`GOALS.md`](docs/product/GOALS.md) · [`RISKS.md`](docs/product/RISKS.md) | What we are building, what we are not, and the risk register |
| [`docs/architecture/ARCHITECTURE.md`](docs/architecture/ARCHITECTURE.md) | Components, pipeline, plugin interfaces, data model, scheduler, API |
| [`docs/architecture/MATCHING_ENGINE.md`](docs/architecture/MATCHING_ENGINE.md) | Candidate normalisation, rejections, scoring, per-source search strategy, verification |
| [`docs/architecture/LIBRARY_OUTPUT.md`](docs/architecture/LIBRARY_OUTPUT.md) | Layouts, the album policy ("fewest albums"), tag spec, Plex/Plexamp rules, reference libraries |
| [`docs/architecture/STACK.md`](docs/architecture/STACK.md) · [`DEPLOYMENT.md`](docs/architecture/DEPLOYMENT.md) · [`QUALITY_DEFINITIONS.md`](docs/architecture/QUALITY_DEFINITIONS.md) · [`CONFIG_EXAMPLES.md`](docs/architecture/CONFIG_EXAMPLES.md) | Stack and code to port, Docker/bundled slskd, quality seed, reference config |
| [`docs/build/PHASES.md`](docs/build/PHASES.md) · [`PROGRESS.md`](docs/build/PROGRESS.md) · [`AGENT_WORKFLOW.md`](docs/build/AGENT_WORKFLOW.md) · [`CODING_STANDARDS.md`](docs/build/CODING_STANDARDS.md) · [`REPO_LAYOUT.md`](docs/build/REPO_LAYOUT.md) | How the build is sequenced, what each task delivered, how AI subagents are used, conventions |
| [`docs/build/NEXT_SESSION_PROMPT.md`](docs/build/NEXT_SESSION_PROMPT.md) | The prompt that starts the next build session |
| [`docs/research/FINDINGS.md`](docs/research/FINDINGS.md) + six full reports | The URL-cited research behind every decision |
| [`docs/PLAN.md`](docs/PLAN.md) | The complete planning document (revision 3), frozen as the record of the research phase |

## Stack

C# / .NET 10 (ASP.NET Core, EF Core + SQLite) with a React 19 + Vite front end, aligned with the other \*arrs so their GPL-3.0 subsystems can be ported; one Docker image with slskd bundled; HTTP on port **1077**, Soulseek on 50300.

## Licence

GPL-3.0 (see `LICENSE`). slskd is redistributed unmodified under its own AGPL-3.0 licence and additional terms. See [`NOTICE.md`](NOTICE.md) for the full attribution list (ported code and bundled third-party programs) and [`CHANGELOG.md`](CHANGELOG.md) for release notes.
