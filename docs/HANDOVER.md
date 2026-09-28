# Handover — Compilarr

**Prepared:** 2026-09-28 · **By:** the planning session (Claude Code, cloud) · **For:** the first build session, run locally from `D:\Code\compilarr` with Claude Code on **Claude Opus 5.5** as orchestrator.

## 1. Where things stand

- Research and design are **complete and approved by the owner**. Every decision is in `docs/DECISIONS.md` (with ADRs in `docs/adr/`); the full planning record is `docs/PLAN.md`; the evidence is in `docs/research/`.
- **Phase 0 is done (2026-09-28)** — the gate passed in CI and on the test host; see the Session log below and `docs/build/PROGRESS.md`. The image is `ghcr.io/wulfftech/compilarr` (`:develop` from main, `:0.0.1-alpha.1`).
- The next unit of work is **Phase 1 — song identity and the Wanted list** (`docs/build/PHASES.md`). No `PHASE_1_TASKS.md` exists yet: writing it is the first job of the next session (start from the three tasks in the Session log).

## 2. The product in one paragraph

A self-hosted *arr whose unit is one **MusicBrainz recording**. Wanted songs are searched on Soulseek (bundled slskd) first, YouTube Music Art Tracks (yt-dlp) second, torrents/usenet (qBittorrent, SABnzbd) last; candidates are scored (identity, quality, availability, source preference), downloads are verified (ffprobe + duration + AcoustID), tagged (Picard mapping), and filed under a library layout (flat / artist / artist-album / **Plexamp** preset with a "fewest albums per artist" policy). Quality profiles with cutoff/upgrades (default cutoff 320). Inputs: individual adds, pasted lists, Deezer/YouTube Music playlists, Spotify CSV exports, Last.fm/ListenBrainz, and the user's existing music folder (reference library with a match queue). C#/.NET 10 + React 19, GPL-3.0, one Docker image, port 1077.

## 3. Start the next session (Windows, `D:\Code\compilarr`)

```powershell
git clone https://github.com/wulfftech/compilarr D:\Code\compilarr      # or: git -C D:\Code\compilarr pull
cd D:\Code\compilarr
copy .env.example .env      # then fill OPENROUTER_API_KEY and COMPILARR_WORKER_MODEL (see docs/build/AGENT_WORKFLOW.md §6)
```

Prerequisites: .NET 10 SDK, Node 22+, Python 3.10+ (for `scripts/worker.py`), Docker Desktop (WSL 2), Git, the `claude` CLI on `PATH`.

Then open Claude Code in the folder, select the orchestrator model (`/model claude-opus-5-5`), and paste the full kickoff prompt from `docs/build/NEXT_SESSION_PROMPT.md` (short form below):

> Read CLAUDE.md, docs/HANDOVER.md and docs/build/PROGRESS.md. Run the worker-model bake-off from docs/build/AGENT_WORKFLOW.md §6 (or skip it if COMPILARR_WORKER_MODEL is already pinned), then execute Phase 0 task by task using cheap workers via scripts/worker.py, reviewing every diff and keeping PROGRESS.md current. Stop at the Phase 0 gate and report.

Sanity check before delegating anything: `python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md` prints the exact command and environment (key redacted) that a worker would get.

## 4. Things the owner still has to provide (not blockers for Phase 0)

- An **AcoustID application key** (`acoustid.org/new-application`) for the verification pipeline (Phase 2 tests).
- A **dedicated Soulseek account** for the bundled slskd (one login per username; do not reuse a personal client's account).
- A **Plex token** and test library if Phase 3 should be validated against a real server.
- The pinned **worker model** after the bake-off, and a **phase budget** for worker spend.
- Optional: qBittorrent/SABnzbd/Prowlarr test instances for Phase 7.

## 5. What not to do (the short list; full list in `CLAUDE.md`)

Do not embed Soulseek.NET; do not copy AGPL code; do not fork Lidarr; do not write `.webm` or fake lossless; do not exceed the Soulseek search budget; do not put secrets or AI identifiers in commits; do not let workers edit decisions, ADRs, `CLAUDE.md`, `.claude/`, workflows or `.env*`; do not merge red.

## 6. Knowledge map (nothing from the planning session is lost)

| Topic | Where |
|---|---|
| Why Lidarr cannot do this; *arr internals to copy | `docs/research/FINDINGS.md` §3.1, `docs/research/research_arr.md` |
| slskd API/config/limits, Soulseek network rules, Sockseek/soularr matching internals | `docs/research/research_soulseek.md`, `docs/architecture/MATCHING_ENGINE.md` §6.4, `docs/architecture/DEPLOYMENT.md` §9.5 |
| yt-dlp 2026 (EJS/Deno, PO tokens, formats), ytmusicapi, spotDL scoring | `docs/research/research_youtube.md`, `MATCHING_ENGINE.md` |
| Torznab reality, qBittorrent/SABnzbd selective download, partial-seed mechanics, Lidarr quality parser | `docs/research/research_torrent_usenet.md`, `QUALITY_DEFINITIONS.md` |
| MusicBrainz/AcoustID/tag mapping/Plex scanner behaviour/naming tokens | `docs/research/research_metadata_plex.md`, `LIBRARY_OUTPUT.md` |
| Stack comparison, Soulseek.NET licence issue, code to port | `docs/research/research_stack.md`, `docs/architecture/STACK.md` |
| Existing projects (SoulSync, DroppedNeedle, …) and name collisions | `docs/research/FINDINGS.md` §3.6 |
| Every owner decision with rationale | `docs/DECISIONS.md`, `docs/adr/` |
| Data model, API, jobs | `docs/architecture/ARCHITECTURE.md` |
| Phases, gates, first tasks | `docs/build/PHASES.md`, `docs/build/PHASE_0_TASKS.md` |
| How to use cheap workers | `docs/build/AGENT_WORKFLOW.md`, `scripts/worker.py`, `.claude/agents/` |

## 7. Session log

### 2026-09-28 — Build session 1: Phase 0 (orchestrator on Opus 5.5, workers on DeepSeek V4.1 Flash)

**Outcome.** Phase 0 gate **passed**: `scripts/smoke-test.sh` (every gate item, automated) is green in CI on every push and was run on the test host `ch01` against the same image. 209 backend tests + 15 frontend tests; CI green on `main`; multi-arch images on GHCR. Worker spend USD 1.12 of the USD 10 phase budget (+ USD 0.35 bake-off). Details per task: `docs/build/PROGRESS.md`; decisions taken: `docs/DECISIONS.md` "Build session 1".

**What exists now.**
- Backend (`src/`): config (`/config/config.yml` + `APP__` env, validated options, API key generated on first run); SQLite/EF Core with migrations (`setting`, `job`, `command`); Lidarr-ported auth (API key header/query/Bearer, Forms login, local-address bypass, URL base); `/ping`, `/api/v1/system/status`, `/api/v1/health`, `/api/v1/command`, `/api/v1/system/task`, `/initialize.json`, OpenAPI at `/docs/v1/openapi.json` (snapshot in `docs/api/openapi.json`, regenerate with `COMPILARR_UPDATE_OPENAPI=1`); Serilog JSON logs with secret redaction; event aggregator; persisted command queue + Quartz schedules (Heartbeat 1 min, CheckHealth 15 min); SignalR `/signalr/events`; bundled slskd: settings, `slskd.yml` renderer, client, supervisor (`SlskdHost`) with backoff and restart-on-settings, health check.
- Frontend (`frontend/`): React 19 + Mantine + TanStack Query shell (Library, Wanted, Activity, Settings, System → Status/Tasks), typed client from the OpenAPI snapshot, SignalR live updates, light/dark theme; served by the API under any URL base.
- Image (`docker/`): s6-overlay, PUID/PGID/UMASK/TZ, slskd 0.26.0, ffmpeg/ffprobe 9.0.2, fpcalc 1.6.1, Deno 2.9.7, all checksum-pinned; 772 MB amd64.
- Tooling: `scripts/worker.py` (real-USD cost accounting, `--id`, live logs + `watch`, single-shot mode fixed), `scripts/smoke-test.sh`, `scripts/check-notice.py` (CI-enforced attribution), `scripts/fetch-slskd.sh`. Workers can be launched in visible VS Code tabs through the `claude-terminals` MCP server (registered locally for this project).

**What was learned about worker quality (DeepSeek V4.1 Flash).**
1. *Good:* conventions, docs comments, attribution headers, Lidarr ports and test structure were consistently solid; cost ≈ USD 0.05–0.30 per task. Out of 16 specs, 3 finished in one run.
2. *Turn budget was the main failure mode:* real tasks need 45–60 turns; at the original 25-turn cap almost every task needed 2–3 runs and several were left mid-debug (probe tests, uncommitted work). The owner raised the caps to 50 turns / 30 min mid-session — use them from Phase 1 on.
3. *Fast-moving APIs:* it wrote against stale APIs (Quartz 4 `ValueTask`/`ScheduleJobOptions`, .NET 10 cookie 401s for API endpoints, TS 7 vs typescript-eslint). Put exact versions **and** the signatures that changed into specs.
4. *Concurrency and lifetimes are its blind spot:* a startup race that orphaned freshly queued commands, a scoped service holding a cache, publish-after-save test races, an unguarded crash path in the slskd supervisor (the reviewer agent caught that and a login timing leak). Keep the reviewer agent on anything with processes, locks, auth or caches, and loop suspicious tests (`for i in 1..8`) before merging.
5. *Tests that do not test:* hard-coded counts, a test asserting the wrong expectation, a regression test that did not catch its bug (checked by mutation). Mutation-check the key regression tests yourself.
6. *Run the real thing:* the running app, a browser and the container found five bugs no unit test did (asset requests answered with index.html, the `<base>` placeholder rewriting itself, slskd refusing missing directories, Ubuntu's uid 1000 user, a per-request health cache). Keep `scripts/smoke-test.sh` growing with each phase gate.
7. *Windows specifics:* SQLite pool locks on temp dirs, log-file sharing, CRLF from YamlDotNet, Git Bash path conversion (`MSYS_NO_PATHCONV=1`), `getaddrinfo` hiccups on `git push` (retry).

**Open items / follow-ups.**
- Owner inputs still needed for later phases: AcoustID key (Phase 2), dedicated Soulseek account (Phase 2), Plex token (Phase 3). The repo and GHCR package are private: pulling on another host needs a token, or use the CI image artifact (3 days).
- Forwarded headers are not configured (`UseForwardedHeaders` is a no-op) — set known proxies before anything relies on the client IP behind a reverse proxy.
- A secret containing JSON-escaped characters would not be matched verbatim by the log redactor — relevant when Soulseek/Plex secrets land (Phase 2/3).
- Dependabot PR #2 (Serilog 4.4) is open for review.

**Next: the first three Phase 1 tasks to spec** (write `docs/build/PHASE_1_TASKS.md` first, from `PHASES.md` Phase 1):
1. **P1-01 Domain model and seed data** — EF entities + migration for `artist`, `song`, `song_artist`, `album_context`, `quality` (seeded from `docs/architecture/QUALITY_DEFINITIONS.md`, including the Opus tiers), `quality_profile` (defaults "Standard 320" with cutoff MP3-320 and "Lossless" with cutoff FLAC) and a default `library`, per `ARCHITECTURE.md` §5.4; repository tests; no API yet.
2. **P1-02 MusicBrainz client** — typed `HttpClient` with a descriptive User-Agent, 1 req/s limiter, `Retry-After`, a `metadata_cache` table with TTLs; recording search, lookup by MBID and ISRC, releases-for-recording; contract tests against recorded fixtures under `tests/fixtures/musicbrainz/` (have the researcher agent verify the current WS/2 query syntax and response shapes first).
3. **P1-03 Version-flag parser** — golden-tested parser for live/remix/acoustic/instrumental/radio edit/remaster/explicit/cover from title + MB disambiguation + release-group secondary types (`MATCHING_ENGINE.md`), cases in `tests/fixtures/version-flags.json`. Independent of 1 and 2, so it can run in parallel.
