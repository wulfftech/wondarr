# Handover — Wondarr

> **Name:** the project was renamed from *Compilarr* to **Wondarr** on 2026-09-28 (Phase 1a, `docs/DECISIONS.md` build session 2 #10). Session log entries before Phase 1a use the old name and paths.

**Updated:** 2026-09-28, end of build session 2 (Phase 1 + Phase 1a) · **For:** the next build session, run locally from `D:\Code\wondarr` with Claude Code on **Claude Opus 5.5** as orchestrator.

## 1. Where things stand

- Research and design are **complete and approved by the owner**. Every decision is in `docs/DECISIONS.md` (with ADRs in `docs/adr/`); the full planning record is `docs/PLAN.md`; the evidence is in `docs/research/`.
- **Phase 0 and Phase 1 are done (2026-09-28)** — both gates pass in CI on every push (`scripts/smoke-test.sh`, Phase 1 replaying recorded metadata) and passed on the test host `ch01`; see the Session log below and `docs/build/PROGRESS.md`.
- **Phase 1a renamed the project Compilarr → Wondarr** (owner, 2026-09-28): the image is `ghcr.io/wulfftech/wondarr` (`:develop` from main), the repository `github.com/wulfftech/wondarr` (the old URLs redirect). The folder on the dev PC is to be renamed `D:\Code\compilarr` → `D:\Code\wondarr` by the owner between sessions (see the Phase 1a entry in the Session log).
- The next unit of work is **Phase 2 — the Soulseek source, slskd management and the import pipeline** (`docs/build/PHASES.md`). Writing `docs/build/PHASE_2_TASKS.md` is the first job of the next session (start from the three tasks in the Session log).

## 2. The product in one paragraph

A self-hosted *arr whose unit is one **MusicBrainz recording**. Wanted songs are searched on Soulseek (bundled slskd) first, YouTube Music Art Tracks (yt-dlp) second, torrents/usenet (qBittorrent, SABnzbd) last; candidates are scored (identity, quality, availability, source preference), downloads are verified (ffprobe + duration + AcoustID), tagged (Picard mapping), and filed under a library layout (flat / artist / artist-album / **Plexamp** preset with a "fewest albums per artist" policy). Quality profiles with cutoff/upgrades (default cutoff 320). Inputs: individual adds, pasted lists, Deezer/YouTube Music playlists, Spotify CSV exports, Last.fm/ListenBrainz, and the user's existing music folder (reference library with a match queue). C#/.NET 10 + React 19, GPL-3.0, one Docker image, port 1077.

## 3. Start the next session (Windows, `D:\Code\wondarr`)

```powershell
git clone https://github.com/wulfftech/wondarr D:\Code\wondarr      # or: git -C D:\Code\wondarr pull
cd D:\Code\wondarr
copy .env.example .env      # then fill OPENROUTER_API_KEY and WONDARR_WORKER_MODEL (see docs/build/AGENT_WORKFLOW.md §6)
```

An existing `.env` with the old `COMPILARR_*` keys keeps working (`scripts/worker.py` falls back to them). Prerequisites: .NET SDK 10.0.4xx (`global.json`), Node 22.12+ (24 LTS on the dev box), Python 3.10+, Git, the `claude` CLI on `PATH`, and the `claude-terminals` MCP server connected for visible worker tabs (its tabs run **cmd.exe**). No local Docker is needed: the image is built and gate-tested in CI and on `ch01`.

Then open Claude Code in the folder, select the orchestrator model (`/model claude-opus-5-5`), and paste the kickoff prompt from `docs/build/NEXT_SESSION_PROMPT.md` (Phase 2).

Sanity check before delegating anything: `python scripts/worker.py --dry-run run docs/build/tasks/P1-01.md` prints the exact command and environment (key redacted) a worker would get — it should show 100 turns / 60 min.

## 4. Things the owner still has to provide

- A **dedicated Soulseek account** for the bundled slskd if the current one (the owner's own) gets kicked by duplicate logins — validate first (Phase 2, first task).
- Setting the new **`ghcr.io/wulfftech/wondarr` package public** after its first push (package settings on GitHub).
- Optional: qBittorrent/SABnzbd/Prowlarr test instances for Phase 7.
- Provided already: AcoustID key, Plex token, Soulseek credentials (all in `.env`), worker model (DeepSeek V4.1 Flash), phase budget (USD 10), worker caps (100 turns / 60 min).

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
- **Owner update (2026-09-28, after the session):**
  - The **repository will be made public before Phase 1 starts.** Check the GHCR package afterwards: a container package's visibility is set on the package (github.com/users/wulfftech/packages/container/compilarr → Package settings) and may still be private; once public, test hosts can `docker pull ghcr.io/wulfftech/compilarr:develop` without a token (until then use the CI image artifact, kept 3 days).
  - **`.env` now holds `ACOUSTID_CLIENT_KEY`, `PLEX_TOKEN`, `SOULSEEK_USERNAME` and `SOULSEEK_PASSWORD`** (all set; never print them). They are for the orchestrator's opt-in live checks (`COMPILARR_LIVE_TESTS=1`, never in CI) and for running a test container: pass them as `APP__SOULSEEK__USERNAME` / `APP__SOULSEEK__PASSWORD` (the app reads its own config, not `.env`).
  - **The Soulseek credentials are the owner's own account, not a dedicated one — still to be validated.** Soulseek allows one login per username: if the owner's desktop client is online with the same account, the bundled slskd and that client will keep kicking each other off. Validate early in Phase 2: start the image on `ch01` with the credentials, confirm `/api/v1/health` reports "logged in as …", and watch for duplicate-login kicks; if they happen, ask the owner for a dedicated account.
- Forwarded headers are not configured (`UseForwardedHeaders` is a no-op) — set known proxies before anything relies on the client IP behind a reverse proxy.
- A secret containing JSON-escaped characters would not be matched verbatim by the log redactor — relevant when Soulseek/Plex secrets land (Phase 2/3).
- Dependabot PR #2 (Serilog 4.4) is open for review.

**Next: the first three Phase 1 tasks to spec** (write `docs/build/PHASE_1_TASKS.md` first, from `PHASES.md` Phase 1):
1. **P1-01 Domain model and seed data** — EF entities + migration for `artist`, `song`, `song_artist`, `album_context`, `quality` (seeded from `docs/architecture/QUALITY_DEFINITIONS.md`, including the Opus tiers), `quality_profile` (defaults "Standard 320" with cutoff MP3-320 and "Lossless" with cutoff FLAC) and a default `library`, per `ARCHITECTURE.md` §5.4; repository tests; no API yet.
2. **P1-02 MusicBrainz client** — typed `HttpClient` with a descriptive User-Agent, 1 req/s limiter, `Retry-After`, a `metadata_cache` table with TTLs; recording search, lookup by MBID and ISRC, releases-for-recording; contract tests against recorded fixtures under `tests/fixtures/musicbrainz/` (have the researcher agent verify the current WS/2 query syntax and response shapes first).
3. **P1-03 Version-flag parser** — golden-tested parser for live/remix/acoustic/instrumental/radio edit/remaster/explicit/cover from title + MB disambiguation + release-group secondary types (`MATCHING_ENGINE.md`), cases in `tests/fixtures/version-flags.json`. Independent of 1 and 2, so it can run in parallel.

### 2026-09-28 — Build session 2: Phase 1 (orchestrator on Opus 5.5, workers on DeepSeek V4.1 Flash)

**Outcome.** Phase 1 gate **passed live** (50 pasted songs → 48 MusicBrainz recordings with the right duration and cover art (96 %), 1 via Deezer, 0 unresolved, every song with an album assignment; 363 s at MusicBrainz' 1 req/s) and is automated in CI: `scripts/smoke-test.sh` replays the recorded metadata (`tests/gate/replay`, via `scripts/metadata-replay.py`) inside the built image and fails on any unrecorded request — green in CI (run 36443741783) and on `ch01` against the published `ghcr.io/wulfftech/compilarr:develop`. 14 worker tasks (13 planned + one fix task), all merged; 591 backend + 52 frontend tests. Worker spend **USD 1.69** of the USD 10 budget (key counter). Details: `docs/build/PROGRESS.md`; decisions: `docs/DECISIONS.md` "Build session 2" (#1–#12).

**What exists now.**
- Domain model (`artist`, `song`, `song_artist`, `album_context`, `song_file`, seeded `quality` 1–43, profiles "Standard 320"/"Lossless", default Plexamp library), plus `metadata_cache`, `history`, `blocklist`, `import_list`/`import_list_item`.
- Metadata: MusicBrainz (search, lookup, ISRC, release browse, release), Cover Art Archive, Deezer, iTunes clients; per-host request spacing (MusicBrainz 1 req/s process-wide, retries inside the gate), SQLite response cache with TTLs, cover-art chain, version-flag parser (79 golden cases), album-policy engine (5 policies, sticky, 10 golden cases), identity resolver (Deezer reference → ISRC bridge → duration-bounded MB search, title-only retry, bootleg skip, Deezer-only fallback).
- API: songs (CRUD, lookup, album contexts/override), artists, preview (fresh Deezer URL), bulk paste (`BulkAddSongs` command) + unresolved review, quality definitions/profiles, library, wanted (missing/cutoff), history, blocklist.
- UI: Library, Add songs (search with disambiguation, length, types, cover, preview; paste with live progress), Unresolved review, Wanted, Activity (History/Blocklist), Settings (Quality profiles, Library). Checked in a real browser against live services.

**Worker lessons (Phase 1).**
1. *Turn cap still the main limit:* 7 of 14 tasks hit 50 (then 100) turns — usually with green, nearly finished work. Committing it myself was cheaper than a continuation run. Caps are now 100 turns / 60 min (owner).
2. *Final newlines:* the worker's Write tool drops them; fixed repo-wide once, now checked at every merge.
3. *Test infrastructure bugs surface under load, not in the task:* `ClearAllPools()` disposing other tests' SQLite connections, `WebApplicationFactory`'s re-entrant `Dispose`, jsdom lacking `ResizeObserver`, a fetch mock that could not see `Request` objects. Loop the whole suite (not one project) before merging.
4. *Platform traps the tests did not show:* `InvariantGlobalization` makes `Normalize(FormD)` a no-op (diacritics never stripped); `Task.Delay` can wake ~15 ms early on Windows (rate gate now re-checks the clock).
5. *Live data beats fixtures:* a live probe over the gate songs found three wrong recordings the fixture tests could not (a 1983 live take that *is* the album version, an "original studio mix" read as a remix, a bootleg-only recording); a browser pass found two UI bugs (disabled preview on the top result, stale paste counts).
6. *The claude-terminals tabs run cmd.exe:* set caps with `set "VAR=…" &&`; continuation notes go into the worker's own worktree (see the session memory).

**Open items / follow-ups.**
- Known resolver gap: "Robyn - Dancing On My Own" resolves to the 218 s edit (Deezer has no reference for it) → add iTunes as a third duration reference.
- Concurrent adds of the same recording surface as HTTP 500 (unique index) → map to 409.
- Paste resolution takes ~7 s per line against live MusicBrainz (release browse pages at 1 req/s) — fine for 50, slow for 1000; consider fetching releases lazily or from the ISRC lookup's release list.
- Version-flag badges show wire names (`radio_edit`).
- The OpenRouter key has a lifetime limit of USD 10 and has used ~3.2.
- Still open from Phase 0: forwarded headers, JSON-escaped secrets in the log redactor, Dependabot PR #2.
- **Phase 1a (owner, 2026-09-28): rename Compilarr → Wondarr before Phase 2** — plan in `docs/build/PHASE_1A_TASKS.md`, decision in `docs/DECISIONS.md` #10.

**Next: Phase 1a, then the first three Phase 2 tasks to spec** (write `docs/build/PHASE_2_TASKS.md` first, from `PHASES.md` Phase 2):
1. **P2-01 Validate the Soulseek account on `ch01` and the slskd client for search** — start the image on `ch01` with `APP__SOULSEEK__USERNAME/PASSWORD` from `.env`, confirm `/api/v1/health` says "logged in as …" and watch for duplicate-login kicks (the account is the owner's own; ask for a dedicated one if kicks happen). Then the slskd search client: `POST /api/v0/searches`, `/hub/search` streaming with polling fallback, always-delete, 30 s wall clock, the global token bucket (30 searches / 4 min, ≤ 2 outstanding, ≥ 5 s apart).
2. **P2-02 Candidate normalisation and Soulseek filename/path parsing** (`MATCHING_ENGINE.md` §6.1) — golden cases in `tests/fixtures/filenames.json`, reusing the P1-03 version-flag parser for bracketed hints.
3. **P2-03 Decision engine v1** (§6.2–6.3) — hard rejections and the 0–1000 score with persisted reasons, golden cases in `tests/fixtures/decisions.json`; uses `QualityProfile.IsAllowed/MeetsCutoff/IsUpgrade` from P1-01.

