# Changelog

All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released).

## [Unreleased]

### Changed
- **Renamed from Compilarr to Wondarr** — the \*arr for one-hit wonders (2026-09-28). The image is now `ghcr.io/wulfftech/wondarr`; an existing `/config/compilarr.db` is renamed to `wondarr.db` on first start; `COMPILARR_CONFIG_DIR` is still honoured; users sign in again once (the auth cookie is now `WondarrAuth`).

### Added (Phase 1 — song identity and the Wanted list)
- Song identity from `Artist - Title`, free text, MusicBrainz/Deezer links or ISRCs: Deezer reference → ISRC bridge → MusicBrainz, Deezer-only fallback, unresolved review.
- Album policies (`fewest_albums`, `singles_only`, `original_album`, `single_release`, `compilation`), sticky assignments, per-song override; cover art from Cover Art Archive, Deezer or iTunes.
- Add songs by search (disambiguation, length, release types, cover, 30-second Deezer preview) or by pasting up to 1000 lines; Library, Wanted (Missing / Cutoff Unmet), History, Blocklist, quality profiles and library settings.

### Added
- Planning documentation set, ADRs, research reports, agentic build workflow and worker runner (2026-09-28).

## [0.0.1-alpha.1] — 2026-09-28

Phase 0 — skeleton, foundations and a runnable single-container image (`ghcr.io/wulfftech/compilarr:0.0.1-alpha.1`, amd64 + arm64).

### Added
- Solution skeleton: `Compilarr.sln`, `src/Compilarr.Core`, `src/Compilarr.Api`, test projects, `global.json` pinning the .NET 10 SDK, `Directory.Build.props` and `Directory.Packages.props`.
- Configuration from `/config/config.yml` with `APP__` environment-variable overrides, typed and validated options, and a generated API key on first run.
- SQLite persistence with EF Core, checked-in migrations and a startup migration step.
- Authentication ported from Lidarr: API key (`X-Api-Key`, `apikey`, Bearer), Forms login with "disabled for local addresses", and URL base support for reverse proxies.
- `/ping`, `/api/v1/system/status` (Lidarr field set) and `/api/v1/health` with database, folder and bundled-slskd checks.
- Structured logging with a redaction filter that keeps secrets (API keys, Soulseek password, Plex token) out of logs and log files.
- Persisted command queue with `/api/v1/command` (Lidarr-shaped) and Quartz-scheduled tasks whose last/next run survive restarts.
- SignalR hub for real-time UI events.
- React 19 + TypeScript + Vite frontend shell (routing, theme, API client, loading/empty/error states).
- Bundled slskd supervisor: downloads the pinned release, renders its configuration and runs it as a separate process.
- Docker image (amd64/arm64): multi-stage build with s6-overlay (`PUID`/`PGID`/`UMASK`/`TZ`), ffmpeg/ffprobe, Chromaprint `fpcalc`, Deno and slskd.
- CI workflow building and testing the backend and the frontend, including the ported-code attribution check.

### Security
- No secrets in the repository; `.env` is git-ignored and redaction is applied before log output.
