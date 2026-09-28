# Changelog

All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released).

## [Unreleased]

### Added
- Planning documentation set, ADRs, research reports, agentic build workflow and worker runner (2026-09-28).

## [0.0.1-alpha.1]

Phase 0 — skeleton, foundations and a runnable single-container image. (Release date to be set when the first image is published.)

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
