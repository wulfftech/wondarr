# Phase 0 tasks — repository and skeleton

Each task is sized for one cheap-worker run (see `AGENT_WORKFLOW.md`). Write the full spec into `docs/build/tasks/<id>.md` from `WORKER_TASK_TEMPLATE.md` before delegating (P0-01 is already written as the example). Dependencies are listed; tasks without shared dependencies can run in parallel worktrees. Layout names come from `REPO_LAYOUT.md`; rules from `CODING_STANDARDS.md`; targets from `docs/architecture/*.md`.

| ID | Title | Depends on | Size | Tier |
|---|---|---|---|---|
| P0-01 | Solution skeleton and build props | — | S | worker |
| P0-02 | Configuration loading (`/config/config.yml` + `APP__` env overrides, typed options, validation) | P0-01 | S | worker |
| P0-03 | Persistence: EF Core + SQLite (WAL), `DbContext`, first migration (`setting`, `job`), startup migrate | P0-01 | M | worker |
| P0-04 | Auth + host plumbing: API key (X-Api-Key / apikey / Bearer), Forms login with "disabled for local addresses", URL base, `/ping`, `/api/v1/system/status`, `/api/v1/health` | P0-02, P0-03 | M | worker (port from Lidarr) |
| P0-04b | System status (Lidarr field set) + health-check framework and `/api/v1/health` (split out of P0-04 on 2026-09-28 to keep diffs reviewable) | P0-04 | S | worker (port from Lidarr) |
| P0-05 | Serilog: JSON console + rolling files under `/config/logs`, secret redaction filter, request logging | P0-02 | S | worker |
| P0-06 | Command queue + `/api/v1/command` (Lidarr-shaped), Quartz scheduler, job table, heartbeat job, "run now" | P0-03 | M | worker |
| P0-06b | Quartz scheduler for the built-in tasks, `job` table sync (last/next run survive restarts), `/api/v1/system/task` (split out of P0-06) | P0-06 | S | worker |
| P0-07 | SignalR hub `/signalr/events` with typed events (health, job, queue placeholders); JS client wiring in P0-08 | P0-04 | S | worker |
| P0-08a | SPA hosting behind the URL base (`<base href>` rewrite, UI-protected fallback), OpenAPI document + committed snapshot (split out of P0-08) | P0-04 | S | worker |
| P0-08 | Frontend shell: React 19 + TS + Vite + Mantine + TanStack; routes (Library, Wanted, Activity, Settings, System); API client from OpenAPI; theme; served by the API host | P0-04 | M | worker |
| P0-09a | Soulseek settings, `slskd.yml` renderer, slskd client, pinned release fetch script (split out of P0-09) | P0-05 | M | worker |
| P0-09 | `SlskdHost`: fetch script for the pinned slskd release, hosted service that renders `slskd.yml`, launches slskd headless, monitors, restarts, captures logs; settings model for Soulseek | P0-02, P0-05 | M | worker (orchestrator reviews closely) |
| P0-10 | Dockerfile (multi-stage; ffmpeg/ffprobe static, fpcalc, Deno, slskd), s6-overlay + PUID/PGID/UMASK/TZ, HEALTHCHECK `/ping`, `docker/docker-compose.yml` (ports 1077, 50300) | P0-08, P0-09 | M | worker |
| P0-11 | GitHub Actions: `ci.yml` (dotnet build/test, frontend lint/typecheck/test, docker build + gate smoke test), `release.yml` (multi-arch to GHCR on tag), dependabot | P0-10 | S | orchestrator (workers may not edit `.github/workflows/`) |
| P0-12 | `CONTRIBUTING.md`, `NOTICE.md` entries for ported code, `CHANGELOG.md` scaffold, ADR template check | P0-04 | S | single-shot API |

## Acceptance criteria per task (summary; full criteria go in the task files)

- **P0-01** `dotnet build -warnaserror` and `dotnet test` succeed on an empty solution with `Wondarr.Core`, `Wondarr.Api`, `Wondarr.Sources.Slskd`, `Wondarr.Sources.YouTube`, `Wondarr.Sources.Torznab`, tests projects; `global.json` pins .NET 10; `Directory.Build.props` enables nullable, warnings-as-errors, analyzers, `InvariantGlobalization`; `Directory.Packages.props` central versions.
- **P0-02** `APP__SERVER__PORT=1077` overrides `server.port` from `config.yml`; missing file → defaults; invalid values → startup error naming the key; `IOptions<ServerOptions>` etc. bound and validated.
- **P0-03** App creates `/config/wondarr.db` in WAL mode on first run and applies migrations; `setting` and `job` tables exist; a repository test round-trips a setting.
- **P0-04** `/ping` returns 200 without auth; `/api/v1/system/status` requires the key and returns the Lidarr-shaped fields (`appName`, `version`, `urlBase`, `authentication`, `databaseType`, `startTime`, `isDocker`…); `/api/v1/health` returns 200 with no critical issues and non-200 when one exists; URL base `/wondarr` works for API and SPA; local-address bypass works.
- **P0-05** Log lines are JSON with `timestamp/level/message/props`; the string of the API key never appears in logs (test asserts redaction); rolling files under `/config/logs`.
- **P0-06** `POST /api/v1/command {"name":"Heartbeat"}` runs the job and `GET /api/v1/command/{id}` shows `completed`; the Quartz schedule for `Heartbeat` runs every minute; after process restart the job table still holds last-run times.
- **P0-07** A SignalR client receives `health` and `job` events; the hub honours the API key/cookie auth.
- **P0-08** `npm run build` outputs into the API's `wwwroot`; navigating to any route returns the SPA; dark/light theme; an "About" page shows `/api/v1/system/status` data; Mantine + TanStack Query wired; no `any` types.
- **P0-09** With no credentials, health shows "Soulseek: not configured"; with credentials, slskd starts headless, `GET /api/v0/application` succeeds via the generated API key, logs are visible in the app's log viewer, and toggling a restart-required setting restarts the child process exactly once; slskd's port 5030 is bound to localhost only.
- **P0-10** `docker compose up --build` starts the container; `HEALTHCHECK` passes; files created in `/data` carry the PUID/PGID; `ffprobe -version`, `fpcalc -version`, `deno --version` and the slskd binary run inside the image; image size recorded in the done-report.
- **P0-11** CI passes on the PR; a tag `v0.0.1-alpha.1` builds and pushes `ghcr.io/wulfftech/wondarr` for amd64 and arm64.
- **P0-12** Documents exist and are linked from README; `NOTICE.md` lists every ported file so far.
