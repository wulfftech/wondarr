# Handover — Wondarr

> **Name:** the project was renamed from *Compilarr* to **Wondarr** on 2026-09-28 (Phase 1a, `docs/DECISIONS.md` build session 2 #10). Session log entries before Phase 1a use the old name and paths.

**Updated:** 2026-10-07, end of build session 6 (Phase 5) · **For:** the next build session, run locally from `D:\Code\wondarr` in Claude Code on Opus 5.5 (`/model claude-opus-5-5`; DECISIONS 2026-10-06), workers on OpenRouter per `docs/build/MODEL_VALUE_MATRIX.md`.

## 1. Where things stand

- Research and design are **complete and approved by the owner**. Every decision is in `docs/DECISIONS.md` (with ADRs in `docs/adr/`); the full planning record is `docs/PLAN.md`; the evidence is in `docs/research/`.
- **Phase 0 and Phase 1 are done (2026-09-28)** — both gates pass in CI on every push (`scripts/smoke-test.sh`, Phase 1 replaying recorded metadata) and passed on the test host `ch01`; see the Session log below and `docs/build/PROGRESS.md`.
- **Phase 1a renamed the project Compilarr → Wondarr** (owner, 2026-09-28): the image is `ghcr.io/wulfftech/wondarr` (`:develop` from main), the repository `github.com/wulfftech/wondarr` (the old URLs redirect). The folder on the dev PC is to be renamed `D:\Code\compilarr` → `D:\Code\wondarr` by the owner between sessions (see the Phase 1a entry in the Session log).
- **Phase 2 is done (2026-09-29)** — Soulseek source, slskd management and the import pipeline; the gate passes in CI (FakeSlskd) and passed live on `ch01` (92 % at or above cutoff, zero wrong recordings in a 30-file audit). A live test instance stays up on `ch01` (http://ch01.ad.wulff.com.au:1077) for ongoing testing.
- **Phase 3 is done (2026-09-30)** — reference libraries (scan, four-tier identification, the Match queue, adoption), the Plexamp preset specifics (covers, `cover.jpg`, folder consistency, lyrics from LRCLIB), Plex (sign-in, partial scans, Settings → Plex), the Compact library task (planner, dry run, executor with the Plex move-out / scan / empty-trash / move-back sequence, verified live against a real Plex server), and notifications (Webhook, Discord, Apprise; Settings → Notifications). The gate passes in CI and live on `ch01`; the owner's real 96-file folder was run live (it is a DJ set of edits, so most of it waits in the Match queue by design). One live check is left: a 500-file folder of unedited files. See the build-session-4 entries.
- **Phase 4 is done (2026-10-06)** — the YouTube source: the keyless InnerTube search client, the yt-dlp runner with its retry-later-vs-blocklist error taxonomy, the per-library output policy (AAC/MP3/keep-Opus, quality recorded as OPUS-160 so files stay upgradeable), the source-tier ordering (Soulseek first, YouTube when Soulseek yields nothing acceptable), the bot-check backoff (one grab per run, never a loop), the settings page with the one-time ToS disclaimer, and the gate tooling (FakeYT, the fake InnerTube, `scripts/phase4-gate.py`, the `SMOKE_PHASE4` smoke stage). The CI gate run and the live ch01 check are the follow-up session's first items. See the build-session-5 entry.
- **Phase 5 is done (2026-10-07)** — the upgrade loop (identity rule, fingerprint-confirmed upgrades, search on add), the per-song guard between imports and compaction, backups with a staged restore, the Tasks/Backup/Logs pages and APIs, external-slskd mode with its settings, `release/push`, Lidarr queue fields and `/docs`. The Phase 4 **and** Phase 5 gates pass in CI (Phases 0–5 green in one run) and live on `ch01` (backup → fresh container, three YouTube fills, real upgrades). See the build-session-6 entry.
- The next unit of work is **Phase 6 — import lists and playlist sync** (`docs/build/PHASES.md`; start with the three tasks at the end of the Session log).

## 2. The product in one paragraph

A self-hosted *arr whose unit is one **MusicBrainz recording**. Wanted songs are searched on Soulseek (bundled slskd) first, YouTube Music Art Tracks (yt-dlp) second, torrents/usenet (qBittorrent, SABnzbd) last; candidates are scored (identity, quality, availability, source preference), downloads are verified (ffprobe + duration + AcoustID), tagged (Picard mapping), and filed under a library layout (flat / artist / artist-album / **Plexamp** preset with a "fewest albums per artist" policy). Quality profiles with cutoff/upgrades (default cutoff 320). Inputs: individual adds, pasted lists, Deezer/YouTube Music playlists, Spotify CSV exports, Last.fm/ListenBrainz, and the user's existing music folder (reference library with a match queue). C#/.NET 10 + React 19, GPL-3.0, one Docker image, port 1077.

## 3. Start the next session (Windows, `D:\Code\wondarr`)

```powershell
git clone https://github.com/wulfftech/wondarr D:\Code\wondarr      # or: git -C D:\Code\wondarr pull
cd D:\Code\wondarr
copy .env.example .env      # then fill OPENROUTER_API_KEY and WONDARR_WORKER_MODEL (see docs/build/AGENT_WORKFLOW.md §6)
```

An existing `.env` with the old `COMPILARR_*` keys keeps working (`scripts/worker.py` falls back to them). Prerequisites: .NET SDK 10.0.4xx (`global.json`), Node 22.12+ (24 LTS on the dev box), Python 3.10+, Git, the `claude` CLI on `PATH`, and the `claude-terminals` MCP server connected for visible worker tabs (its tabs run **cmd.exe**). No local Docker is needed: the image is built and gate-tested in CI and on `ch01`.

Then open Claude Code in the folder, select the orchestrator model (`/model claude-opus-5-5`), and paste the kickoff prompt from `docs/build/NEXT_SESSION_PROMPT.md` (Phase 3).

Sanity check before delegating anything: `python scripts/worker.py --dry-run run docs/build/tasks/P1-01.md` prints the exact command and environment (key redacted) a worker would get — it should show 100 turns / 60 min.

## 4. Things the owner still has to provide

- A **dedicated Soulseek account** only if the current one starts getting kicked by duplicate logins (validated in Phase 2: no duplicate-login kick during any of the live runs).
- A reachable **Plex server** and token for the Phase 3 live checks (the token is in `.env`); a throwaway unclaimed Plex Media Server on `ch01` served the grouping check in build session 4.
- **A real folder of ~500 mixed music files** reachable from `ch01`, for the Phase 3 gate's live 500-file check.
- **Raise the OpenRouter key's lifetime limit before Phase 6:** USD 17.71 of USD 20 used after build session 6 (USD 2.29 left). Phase 5 cost USD 4.48; T3 runs now cost about USD 1 each.
- Deleting the old `ghcr.io/wulfftech/compilarr` package when convenient (the new one is public already).
- Optional: qBittorrent/SABnzbd/Prowlarr test instances for Phase 7.
- Provided already: AcoustID key, Plex token, Soulseek credentials (all in `.env`), worker model (DeepSeek V4.1 Flash), the OpenRouter key (lifetime limit USD 10 as of 2026-09-30; USD 9.77 used after build session 4), worker caps (100 turns / 60 min).

## 5. What not to do (the short list; full list in `AGENTS.md`)

Do not embed Soulseek.NET; do not copy AGPL code; do not fork Lidarr; do not write `.webm` or fake lossless; do not exceed the Soulseek search budget; do not put secrets or AI identifiers in commits; do not let workers edit decisions, ADRs, `AGENTS.md`, `.claude/`, workflows or `.env*`; do not merge red.

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

### 2026-09-29 — Build session 2 (continued): Phase 1a, the rename to Wondarr

**Outcome.** The project is **Wondarr** — "the \*arr for one-hit wonders": it fetches the one stand-alone song you want without pulling in the artist's album of B-sides (owner, DECISIONS #10). Code, image (`ghcr.io/wulfftech/wondarr`), repository (`github.com/wulfftech/wondarr`, old URLs redirect) and living docs are renamed; the Phase 0 and Phase 1 gates pass on the renamed image in CI and on `ch01` (`ghcr.io/wulfftech/wondarr:develop`, public), and a real upgrade from the old image kept its data. Details: `docs/build/PROGRESS.md` "Phase 1a", plan `docs/build/PHASE_1A_TASKS.md`.

**Upgrade path.** An existing `/config/compilarr.db` is renamed to `wondarr.db` (with its WAL/SHM) on first start; `COMPILARR_CONFIG_DIR` still works; users sign in once more (cookie `WondarrAuth`); `scripts/worker.py` reads `WONDARR_*` and falls back to the `COMPILARR_*` keys in an old `.env`.

**For the owner, before the next session:**
1. Nothing to do for GHCR: `ghcr.io/wulfftech/wondarr` is already public (it inherited the repository's visibility). The old `compilarr` package can be deleted whenever convenient.
2. Close Claude Code, rename the folder `D:\Code\compilarr` → `D:\Code\wondarr` (the git remote already points at the new repository), and re-add the `claude-terminals` MCP server at local scope for the new path. The session memory has already been copied to the new path.
3. Optionally rename the `.env` keys `COMPILARR_WORKER_*` → `WONDARR_WORKER_*` (both work).

**Next:** unchanged — Phase 2, starting with validating the Soulseek account on `ch01` (see the Phase 1 entry above and `docs/build/NEXT_SESSION_PROMPT.md`).

### 2026-09-29 — Build session 3: Phase 2 (orchestrator on Opus 5.5, workers on DeepSeek V4.1 Flash)

**Outcome.** Phase 2 is complete and its gate passes: in CI on every push (FakeSlskd scenario in the built image) and live on `ch01` with the real Soulseek account and AcoustID — 100 songs pasted into an empty instance, 92 % imported at or above cutoff, 5 wrong files caught and replaced without user action, zero wrong recordings in a 30-file audit, the search budget never exceeded (≤ 29 per 240 s, ≥ 5 s apart), and the share toggle following slskd (186 → 0 → 186 folders) after a fix the live run forced (P2-19). Worker spend USD 4.46 (key 3.16 → 7.62 of its USD 10 limit). Details: `docs/build/PROGRESS.md` "Phase 2 gate".

**What exists now.**
- Soulseek source: slskd search API + a process-wide search budget (≤ 30 per rolling 240 s — a submission exactly 240 s old still counts —, ≤ 2 outstanding, ≥ 5 s apart; FIFO, monotonic clock), a search runner that always deletes its search and is bounded by a 30 s wall clock; query strategy (artist+title, folded, title-only, artist+album); Soulseek filename/path parser and quality inference golden-tested on real results; transfers through the batch endpoint into a per-grab folder `wondarr/<guid>`; `DownloadFileComplete` webhook (own token, loopback only) that only wakes the tracker.
- Decision engine v1 (hard rejections + 0–1000 score with a stored breakdown), search runs and candidates persisted, `SongSearch`/`MissingSearch` (scheduled, per-song backoff, one active download per song enforced by a partial unique index, a download-slot limit), interactive search and manual grab.
- Import pipeline: ffprobe probe + decode check, measured quality, fpcalc (+ a middle-window retry through ffmpeg), AcoustID lookups (3 req/s, bounded `Retry-After`, outages defer), verification rules (the wanted MBID, a same-title MusicBrainz duplicate, MBID learning for Deezer-only songs), Picard-mapped tags for MP3/FLAC/M4A/Opus written to a temp copy and read back, the naming-template engine (Lidarr syntax, presets), file placement (move/copy/hardlink, recycle bin with a marker, remote path mappings), `song_file` + history; a rejected file is blocklisted for its song and the next candidate grabbed (≤ 4 attempts); a queue tracker (start / remote-queue / stall timeouts, crash recovery) with live SignalR queue events.
- slskd management: login problems and duplicate-login kicks read from slskd's own log, sharing and download-folder health, no host crash on an unwritable `/data`; the Soulseek settings API writes `config.yml` (env-set fields read-only) and the supervisor restarts slskd as needed.
- API: queue (Lidarr shape), interactive search/grab, naming preview, Soulseek settings/status. UI: Activity → Queue with live progress and remove/blocklist/retry, Interactive search modal with score breakdowns and rejection reasons, Search buttons on Wanted/Library, Settings → Soulseek, naming preview in Settings → Library.
- Gate tooling: `tools/FakeSlskd` (a scenario-driven slskd + AcoustID stand-in that produces real encoded audio in the image), `scripts/phase2-scenario.py`, `scripts/phase2-gate.py`, the Phase 2 stage of `scripts/smoke-test.sh` — green in CI on every push.

**Worker lessons (Phase 2).**
1. *Concurrency and lifetimes remain the blind spot* — and this phase was full of them. The reviewer agent returned FIX-FIRST on 8 of the 9 risky reviews (P2-01, P2-09, P2-15, P2-05, P2-12, P2-15b, P2-13a, P2-13b; P2-10 and P2-19 were MERGE): a lost wake-up that could stall the Soulseek budget for good, a check-then-insert race allowing two downloads per song, deletes not confined to the item's own folder, a "stalled" timeout that killed every download leaving a peer's queue, slow progress never saved, the config binder *appending* bound lists to their defaults. None of these were caught by the workers' own tests; all were fixed with regression tests (mutation-checked where it mattered). Keep the reviewer on everything that touches processes, files, time or the database.
2. *The real network finds what no fixture does:* the first live run found year-only album dates failing the tag read-back (every pseudo-album has one — and the rejection then blocklisted every good file), ATL writing the Vorbis `ORIGINALDATE ` with a trailing space, and Soulseek returning nothing for "the beatles"; the 100-song run found that slskd never shares files added after its first start (a restart restores the share cache instead of scanning) — which the fake had hidden by counting live. Make fakes stateful the way the real service is, and run a 5-song live smoke before any bigger run.
3. *Workers stop early in new ways:* two ended while their own background test run was still going (work left uncommitted), one provider interruption after 47 turns, one out of time a single compile error short. Checking the tree and committing myself was always cheaper than a continuation.
4. *Merges need a script, not discipline:* I merged red once (a golden file vs another task's new default); since then `safe_merge.sh` refuses a red tree before and after, and a stale `--no-build` once hid two test projects. Semantic conflicts between parallel tasks (a new renderer parameter, a moved default) are the common failure — build `main` after every merge.
5. *Frontend flakes came from animations:* menu/modal tests raced Mantine's transitions under load; the tests now render with Mantine's `env="test"`.
6. *Budget:* the key's lifetime limit (not the phase budget) was the constraint; orchestrator-side fixes saved several continuation runs.

**Open items / follow-ups.**
- Verification false negatives (backlog 2026-09-29-19): a non-version subtitle in the AcoustID title ("MALAMENTE (Cap.1: Augurio)") and a same-titled duplicate held against the song's length rather than the file's ("Little Lion Man" 238 vs 247 s) refuse every candidate. Relax only with golden cases proving no wrong recording gets through.
- Interactive search wording and the load-sensitive `SlskdSearchRunner` wall-clock test (backlog 2026-09-29-14…18).
- Soulseek returns nothing for four popular songs (HUMBLE., Purple Rain, Bad Romance, Summer) — query strategy worth a look once the YouTube source (Phase 4) is the fallback.
- A periodic share rescan (e.g. daily, or slskd's `shares.cache.retention`) for files that reach the library outside imports — needed once Phase 3 reference libraries are shared; verify `retention` semantics first.
- The OpenRouter key's lifetime limit was raised to USD 15 by the owner (2026-09-29): about USD 7.4 left for Phase 3.
- The live test instance `wondarr-test` on `ch01` stays up for ongoing testing (owner): http://ch01.ad.wulff.com.au:1077, image `wondarr:p2-19` built on ch01 from `main`, data under `/tmp/wondarr-test` (94 imported songs, real Soulseek account, env in `~/wondarr-test/live.env`). Redeploy it from `ghcr.io/wulfftech/wondarr:develop` or a local build keeping the volumes; `/tmp` does not survive a reboot of ch01.

**Next: the first three Phase 3 tasks to spec** (write `docs/build/PHASE_3_TASKS.md` first, from `PHASES.md` Phase 3):
1. **P3-01 Reference-library scan** — `reference_library` / `reference_file` / `match_candidate` tables (ARCHITECTURE §5.4) and a scan command: walk a folder (flat or layered), probe each audio file, read its tags (ATL: MBIDs, ISRC, artist/title/album, duration), and store `reference_file` rows with `probe` JSON; incremental by size + mtime; a scheduled daily scan + "scan now". No identification yet.
2. **P3-02 Identification pipeline and the Match queue API** — per file: tag MBID → MusicBrainz lookup; ISRC → the P1 ISRC bridge; else fpcalc + AcoustID (reuse `IDownloadVerifier`'s pieces); else title/artist/duration search (P1-06 resolver); confidence tiers (auto-accept above a threshold, owner decision round 2 #7), `match_candidate` rows for the rest, `GET /api/v1/matchqueue`, `POST /api/v1/matchqueue/{id}/resolve`; identified files mark their songs as owned (never downloaded again).
3. **P3-03 Plexamp preset specifics** — `cover.jpg` in each album folder and cover resizing to the configured max edge (the TODO left in `CoverImage.PrepareFrontCover`), Plex-safe tag checks (one album id / date / album artist per folder — assert across the folder on import), and the "Prefer local metadata" notice for flat/artist layouts; then Plex connection (PIN flow) and partial scan after import as P3-04.

### 2026-09-30 — Build session 4: Phase 3 (orchestrator on Opus 5.5, workers on DeepSeek V4.1 Flash)

**Outcome.** Phase 3's gate-critical path is built and its gate passes in CI (the Phase 3 stage of `scripts/smoke-test.sh`: a 34-file reference folder, 94 % identified automatically, the rest resolved through the Match queue API, every new song adopted, the originals byte-for-byte unchanged) and live on `ch01` with real LRCLIB lyrics. A fresh Plex Music library on a throwaway Plex Media Server 1.43.4 grouped the adopted library correctly — no split albums, one deliberate "Singles" per artist, 45/47 tracks with a local lyrics stream (every sidecar written). Not done: Settings → Plex (P3-12a), Compact library (P3-09a/b), notifications (P3-10a/b, P3-12b), the 500-file live folder and Plexamp's own lyrics display. Worker spend USD 2.15 (key 7.62 → 9.77 of its USD 10 limit — the planned raise to USD 15 has not happened). Details: `docs/build/PROGRESS.md` "Phase 3".

**What exists now.**
- Reference libraries: tables `reference_library` / `reference_file` / `match_candidate`; an incremental read-only scanner (size + mtime, linked folders skipped, an empty or unlistable root refuses to mark anything missing, failed probes retried every scan); identification in four tiers (tag MBID 1.0 → ISRC 0.95 → AcoustID capped at 0.89 unless the tags *and* the length agree → text search 0.90 with a known, agreeing length), auto-accept ≥ 0.90, ranked candidates otherwise; identified files become the song's own `song_file` (source `reference`, never searched or replaced); AcoustID outages defer; adoption copies identified files into the target library through the import's `LibraryOrganizer` (source `adopted`) and publishes the import event; a daily `ReferenceLibraryScan` and a `ReferenceAdopt` command.
- Plexamp preset: covers bounded with ffmpeg, `cover.jpg` per album folder (never replaced), the folder's album fields aligned from its first file, sidecar options per library; LRCLIB lyrics (spaced, no retries, bounded) in the tag and as `.lrc`/`.txt` (never replacing either).
- Plex: plex.tv PIN flow, resources, identity, sections, `refresh?path=`, `emptyTrash`; the token only in `X-Plex-Token` (never logged, returned or redirected); library → Plex path mapping; a debounced per-album-folder partial scan after imports and adoption, with a health warning.
- API: `/api/v1/referencelibrary` (CRUD, scan, counts), `/api/v1/matchqueue` (paged; resolve by candidate / MBID / Deezer id / skip; bulk accept), `/api/v1/plex/*`. UI: Settings → Reference libraries, the Match queue page (candidates, search, skip, bulk accept, a nav badge).
- Gate tooling: `scripts/phase3-gate.py`, the Phase 3 stage of the smoke test (`SMOKE_PHASE3`, and `SMOKE_LYRICS=live` for real lyrics), `scripts/safe-merge.sh` (refuses a red tree or an unknown branch), `worker.py` runs from a worktree (`WONDARR_WORKER_BASE`) without AI commit trailers.

**Worker lessons (Phase 3).**
1. *The reviewer earned its keep again:* FIX-FIRST on 8 of 9 reviews (P3-08 was MERGE). The one that mattered most: P3-02 accepted an AcoustID match with no length check, so an extended mix or a live take sharing the album version's opening would have been silently owned as the wrong song. Others: a trailing-slash root mapping a sibling folder into Plex, a Plex client identifier generated twice, retries that never ended, one folder's success hiding another's failure, one bad file stopping a whole library.
2. *Fixing review findings myself beat continuations* on a tight key: the one continuation (P3-06a, 8 findings) cost about half the task again; the other fix rounds were done by the orchestrator in minutes.
3. *A worker that hits the turn cap leaves good work uncommitted* (P3-03, three tests short of green). Always check and finish.
4. *Generated files go stale across parallel branches:* P3-03's OpenAPI snapshot would have dropped P3-06b's Plex endpoints. Regenerate after merging the base into the branch, never before.
5. *Workers find contract bugs:* P3-11's worker noticed that `POST /api/v1/command` hands the whole body to the handler — which exposed a Phase 2 bug (the search button nested `songId` as a string, so every search it started failed) that three tests had pinned.
6. *Live runs still find what fixtures do not:* LRCLIB records wrapped in blank lines broke every such FLAC import (ATL trims on read); Plex's online agent renames pseudo-albums unless "Prefer local metadata" is on. Both came from the first real run.

**Open items / follow-ups.**
- **Budget:** the OpenRouter key's lifetime limit is USD 10 (not the planned 15): USD 0.23 left. Raising it unblocks the rest of Phase 3 (≈ USD 1.2 at this phase's rates: P3-12a ≈ 0.2, P3-09a/b ≈ 0.4, P3-10a/b ≈ 0.35, P3-12b ≈ 0.25).
- **Live checks left for the owner's own data:** a real folder of ~500 mixed files on `ch01` as a reference library (the live instance runs Phase 3 now), and Plexamp showing local lyrics (needs a Plex Pass client).
- The adopter duplicates `ImportService.LoadSongAsync` (backlog 2026-09-30-02); one load-sensitive API test flake (2026-09-30-01).
- A "turn on Prefer local metadata" action for a linked Plex section (verified: `PUT /library/sections/{key}/prefs?respectTags=1`) — explicit, never automatic (`DECISIONS.md` build session 4 #9).
- ATL prints "DummyTag.Remove not implemented" stack traces to stdout when tagging ffmpeg-made MP3s (harmless, the write succeeds) — worth silencing.
- The live test instance `wondarr-test` on `ch01` runs `ghcr.io/wulfftech/wondarr:develop` (`0d90e6e`) with its old volumes (`/tmp/wondarr-test`, database backup `config/wondarr.db.pre-phase3.bak`; the Phase 2 container is kept stopped as `wondarr-test-p2` for a rollback).

**Next: finish Phase 3, then the first three Phase 4 tasks to spec.**
- Phase 3 first: P3-12a (spec committed), then P3-09a/b, P3-10a/b, P3-12b (`docs/build/PHASE_3_TASKS.md`), then the 500-file live check.
1. **P4-01 YouTube Music search client** — InnerTube `search` with the `songs` and `videos` filters, an ISRC query first, results mapped to the existing `Candidate` contract with ATV/OMV/UGC typing; recorded responses as fixtures (verify the current InnerTube request shape with the researcher agent first).
2. **P4-02 yt-dlp runner** — YoutubeDLSharp (versions pinned), `bestaudio` Opus, the cookies file and optional bgutil PO-token URL, Deno detection, concurrency 1 and pacing, and the error taxonomy (bot check, 429/402, geo, age gate, unavailable) → retry-later vs blocklist; a health probe.
3. **P4-03 Output policy per library** — codec (AAC / MP3 / keep Opus), bitrate/VBR, container, sample rate, defaults AAC-256 `.m4a`, the quality recorded as `OPUS-160`, "lossless from lossy" refused (ADR-0006/0008); the transcode step between download and verification.

### 2026-09-30 — Build session 4 (continued): Phase 3 finished

**Outcome.** Every Phase 3 task is merged, after the owner raised the OpenRouter key's limit to USD 20: Settings → Plex (P3-12a), the Compact library planner and dry run (P3-09a), its executor and command (P3-09b), notifications with Webhook (P3-10a), Discord and Apprise (P3-10b), and their UI (P3-12b). Worker spend for these six runs USD 1.30; Phase 3 in total **USD 3.45** (key 7.62 → 11.07 of 20). Live on `ch01`: the owner's 96-file wedding folder as an adopt-mode reference library, a real compaction against a throwaway Plex Media Server (Plex's own log shows the refresh → empty-trash → place → refresh sequence, and the albums came out right), a Webhook delivery for a real import, and the new settings pages in a browser. Details: `docs/build/PROGRESS.md` "Phase 3".

**What the live runs found.**
1. *Real-world tags defeat identification:* every file of the owner's folder was tagged title `"001"`…, artist `"Artist - Title"` — 0 of 96 identified. A number-only title is now read as no title and the artist tag split (`DECISIONS.md` build session 4 #10) → 14 identified and adopted, the rest in the Match queue. That folder is a DJ set of **edits** (6–143 s shorter than every recording), which the length rule refuses to own automatically on purpose, so it is not a test of the gate's 90 % — a folder of unedited files still is.
2. *A merge landed red:* `scripts/safe-merge.sh` ran its checks as an `if` condition, where bash ignores `set -e`, so a failing `dotnet test` was masked by a green frontend build after it (P3-09a merged with two planner tests failing on Windows). Fixed (#14); the recurring `SlskdHostTests` CI flake fixed in its harness too.
3. *Plex behaves as designed:* with "Prefer local metadata" on, the partial scan of the emptied folder plus `emptyTrash` made Plex drop the moved tracks from "Singles", and the scan of the new folder built the real album — no stale entries.

**Worker lessons (continued).**
1. *A background job's own time limit is not the worker's:* two workers were killed by the orchestrator's 30-minute background default mid-run (both had finished or nearly); start workers with a timeout above the worker's own 60-minute cap.
2. *A fake clock needs moving:* P3-09b's worker spent its last half hour on a test that waited forever for a `TimeProvider` timer; the pattern is a helper that advances the fake clock while the awaited call runs.
3. *The reviewer found real data-safety holes again* (FIX-FIRST on P3-09b: a failed move could block every later compaction, a crash between moving and recording could orphan a file; on P3-10b: Discord refusing whole messages over its length limits). Fixing them by hand stayed cheaper than continuations.

**Open items / follow-ups.**
- **The 500-file live check** with a folder of unedited album or single files (the owner's folder was edits); `scripts/phase3-gate.py`'s flow works against any running instance.
- **The Match queue on `ch01`** holds 80 of the wedding files; about 13 of their top candidates are wrong (see `PROGRESS.md`), so they need a human pass in the UI — a good real-world test of the Match queue page.
- Plexamp's own lyrics display (needs a Plex Pass client); a "turn on Prefer local metadata" action for a linked section (verified API, explicit only — #9).
- Compaction and an import touching the same song at once are not serialised (an upgrade landing while that song's file waits in staging could be overwritten by the move back); the window is the Plex wait (≤ ~10 min) — worth a per-song guard in Phase 5's upgrade work.
- A stored notification secret can be replaced from the UI but not cleared; custom webhook header values are shown in full (the field says so).
- Backlog: 2026-09-30-01 (an API test flake under load), -02 (adoption duplicates `ImportService.LoadSongAsync`); -03 (the slskd flake) is fixed.

**Next: Phase 4 — YouTube source (`docs/build/PHASES.md`).** Start with a P4-00 research pass by the orchestrator (the researcher agent): the current InnerTube `search` request shape and `params` for the songs/videos filters, the current yt-dlp release and its PO-token / bgutil story, and YouTube's bot-check responses — then record fixtures. Then:
1. **P4-01 YouTube Music search client** — InnerTube `search` (songs, then videos), an ISRC query first, results mapped to the existing `Candidate` contract with ATV/OMV/UGC typing and the duration gate that rejects official videos with intros; recorded InnerTube responses as fixtures; no network in tests.
2. **P4-02 yt-dlp runner** — YoutubeDLSharp (versions pinned), `bestaudio` Opus, cookies file, optional bgutil PO-token URL, Deno detection, concurrency 1 and pacing, the error taxonomy (bot check, 429/402, geo, age gate, unavailable) → retry-later vs blocklist, a health probe; a simulated bot check must back off, never loop (the phase's gate item).
3. **P4-03 Output policy per library** — codec (AAC / MP3 / keep Opus), CBR/VBR, container, sample rate; defaults AAC-256 `.m4a`; the quality recorded as `OPUS-160` so the song stays upgradeable; lossless-from-lossy and `.webm` refused (ADR-0006/0008); the transcode step between download and verification, with its own migration for the per-library policy.

### 2026-10-06 — Build session 5: Phase 4, the YouTube source (orchestrator on OpenRouter, workers on GLM)

**Outcome.** Phase 4 is code-complete and merged: the InnerTube search client and response parser (P4-01), the yt-dlp runner with its error taxonomy and health probe (P4-02), the per-library output policy and the transcode step (P4-03), the YouTube source provider with the source-tier ordering and the bot-check backoff (P4-04), the settings API and page with the one-time ToS disclaimer (P4-05), and the Phase 4 gate tooling (P4-06: FakeYT, the fake InnerTube in FakeSlskd, the AcoustID registration endpoint, `scripts/phase4-gate.py` + `phase4-scenario.py`, the `SMOKE_PHASE4` smoke stage). Worker spend for the phase **USD 1.83** (P4-01 0.21, P4-02 0.25, P4-03 0.68, P4-04 0.53, P4-05 0.16; key 11.07 → 13.23 of 20, reviewers included). The CI gate for Phase 4 is written but not yet run end-to-end in CI (the image build with the new stage is the follow-up); the live ch01 run is the follow-up session's first item.

**What the wave taught.**
1. *The 100-turn cap is the norm, not the exception:* P4-01, P4-04 and P4-05 all stopped at the cap with their work uncommitted (P4-05's sandbox could not run npm on Windows at all — `WinError 193`). The standing pattern held: the orchestrator finishes, tests and commits the remainder itself; the frontend half of P4-05 was written by the orchestrator outright.
2. *Two semantic conflicts the reviewers and the orchestrator caught:* the candidate mapper's `Extension = null` made the engine reject every YouTube candidate `notAudio` (the runner always lands `.opus` — the mapper now says so), and `ArtistOverlap` matched path tokens, but a YouTube candidate's `RemotePath` is a bare video id — the rule now matches `Parsed.Artist` when the candidate carries one (DECISIONS.md build session 5 #7).
3. *The bot-check taxonomy is pinned by tests, not hope:* `YtDlpGrabFailureTests` map every kind through the real `YtDlpException` (retry-later kinds never blocklist; geo/age-gate/private always do), and `BotCheckBackoffTests` pin the two-run behaviour — one grab per run, the song's backoff owns the retry.
4. *The gate's fakes follow FakeSlskd's spirit:* FakeYT is a stand-in yt-dlp answering the exact surface the runner uses (with the taxonomy's exact stderr strings), the InnerTube stub serves the recorded fixtures keyed by query+filter (an ISRC-shaped query — 2 country letters, 3 registrant, 2 year digits, 5 designator — gets the card fixture), and the AcoustID stub's new loopback-only `/v2/register` lets FakeYT's generated Opus files verify.

**Worker lessons (continued).**
1. *`dotnet test --no-build` after writing a NEW test file is worthless* — the stale binaries do not contain it; a missing using only surfaced in safe-merge's build. Always build after adding test files.
2. *Orphaned test processes starve later runs:* a backgrounded wrong-directory `dotnet test` and its testhosts kept sockets busy and aborted a merge attempt (FakeSlskdTests connection refused + TESTRUNABORT). Kill stale testhost processes before big runs; check command lines first (keep MSBuild nodes, the NuGet MCP server, csdevkit).
3. *Three concurrent suites can deadlock on a loaded machine:* the merged-tree check froze with all three testhosts at zero CPU; kill, abort the merge, retry on a quiet machine. The wsl-safe-merge wrapper now shims npm/node/npx too (npm is a batch file — the shim goes through `cmd.exe /c`).

**Open items / follow-ups.**
- **The Phase 4 gate in CI:** build the image and run `scripts/smoke-test.sh` with `SMOKE_PHASE4=on` (the default) — the stage restarts the container with the YouTube source enabled against the fakes; the first run may need scenario tuning (the gate's songs come from the Phase 2 leftovers).
- **The live ch01 run (opt-in):** enable the source in Settings on the test instance, concurrency 1, pacing, and try the four songs the Phase 2 gate found missing on Soulseek (HUMBLE., Purple Rain, Bad Romance, Summer).
- The Match queue on ch01 still holds the wedding files (a human pass in the UI); the 500-file live check is still open from Phase 3.
- Backlog: 2026-09-30-01 (an API test flake under load), -02 (adoption duplicates `ImportService.LoadSongAsync`).

**Next: Phase 5 — upgrades and the tasks page (`docs/build/PHASES.md`).** Start with:
1. **P5-01 The cutoff-unmet upgrade loop** — a scheduled UpgradeSearch that re-searches songs whose file is below their profile's cutoff (the wanted list's "Cutoff Unmet" tab), reusing the search/verify/import pipeline with the not-an-upgrade rejection already in the engine; the per-song guard so a compaction staging a file and an upgrade landing cannot race (the Phase 3 open item).
2. **P5-02 The tasks page** — the scheduled commands (MissingSearch, ReferenceLibraryScan, the new UpgradeSearch) with their last-run state, manual triggers, and the command history; a backup/download of the config and database; a log viewer over the app's own JSON log.
3. **P5-03 External slskd mode** — point Wondarr at a user's existing slskd instead of the bundled one (the URL, the API key, the shared folders stay the user's), the health checks and the settings page adapting; the bundled mode stays the default.

### 2026-10-07 — Build session 6: Phase 5, upgrades and operations (orchestrator on Opus 5.5, workers on GLM)

**Outcome.** Every Phase 5 task is merged: the upgrade loop with the identity rule and search on add (P5-01), the per-song guard between imports and compaction (P5-02), backups with a staged restore (P5-03), task run times and the log viewer API (P5-04), System → Tasks, Backup and Logs (P5-05), external-slskd mode (P5-06 backend, P5-07 settings), and the ecosystem pieces — `release/push` for autobrr, Lidarr's queue fields for Unpackerr, the Scalar API reference at `/docs` (P5-08). The Phase 4 gate ran in CI for the first time and needed nine fixes before it could pass (`PROGRESS.md` "Phase 4 gate in CI"). The Phase 5 gate passes in CI on fixtures (an MP3-320 song under a FLAC cutoff upgraded when a FLAC appears, old file recycled, history "upgraded"; a backup restored into a fresh container, identical; the unchanged Phase 2 gate against an external slskd) and was checked live on `ch01` (the real instance's backup restored into a throwaway container, identical; 12 real upgrades; three YouTube fills). Worker spend for the session **USD 4.48 (key 13.23 → 17.71 of 20)**. Details: `docs/build/PROGRESS.md` "Phase 5"; decisions: `docs/DECISIONS.md` build session 6 (#1–#8).

**What exists now.**
- *Upgrades:* `UpgradeSearch` (24 h, ≤ 50 songs, its own backoff over `Upgrade` runs) re-searches cutoff-unmet songs through the normal search/verify/import pipeline; an automatic upgrade candidate whose identity sub-score is below the held file's candidate's is rejected `worseIdentity`; adding one monitored song queues its `SongSearch`, a batch queues one `MissingSearch`. Imports and the Compact task are serialised per song (`SongFileLock`, `CompactMoveRules.IsUnfinished`): an import for a song with an unfinished move defers, a move whose file changed is dropped.
- *Operations:* zip backups (SQLite online backup + `config.yml`) weekly and on demand under `/config/backups`, list/download/delete, restore from a stored or uploaded zip — staged, applied before the database opens on the next start, `*.pre-restore` kept; `GET /api/v1/log` (paged, level/text filter, bounded scan) and the log files; real task run times; System → Tasks (Run now fixed: it sent the display name), Backup, Logs.
- *External slskd:* `soulseek.mode: external` + `soulseek.external.{url, api_key, rescan_shares}`; the same budget, runner and pipeline against the user's slskd; `SlskdExternalMonitor` (no lost wake-ups); Settings → Soulseek's Connection card with a Test button; slskd's own fields read-only in external mode.
- *Ecosystem:* `POST /api/v1/release/push` (rejects with the reason until Phase 7), Lidarr's queue record fields, `/docs` (public, like its document; DECISIONS #8), contract tests for Homepage, Unpackerr and autobrr.
- *Gate tooling:* `scripts/phase5-gate.py` (upgrade, backup-take, backup-verify) and the `SMOKE_PHASE5` stage: the upgrade on the Phase 4 container, a backup restored into a fresh container and compared, and the unchanged Phase 2 gate against FakeSlskd as an external slskd (its own container, shared network namespace and `/data`). FakeSlskd gained `POST /fake/scenario/files`, the `--fake-ytdlp` mode (the image has no Python) and a by-length AcoustID match for transcoded YouTube files. yt-dlp 2026.08.19 is now in the image.
- *Worker runner:* `npm` runs on Windows; context trimming keeps recent reads; reads default to 800 lines; an empty reply is nudged instead of ending the run.

**What this session taught.**
1. *A gate that has never run is not a gate.* The Phase 4 stage had nine independent defects, from a missing binary in the image to a stub that answered every query with one song's fixtures; none could show while CI stopped at a formatting step. Keep CI green before anything else, and read the first full run's log line by line.
2. *The runner was the biggest lever.* Three runner bugs (the trim loop, whole-file reads, empty replies taken as done) cost more turns than any task's difficulty; after the fixes the T2 run finished in 91 turns and a T3 rerun in 78.
3. *T3 runs got expensive.* GLM 5.3 runs cost USD 0.6–1.1 each this phase (6.3 M prompt tokens for P5-01), against 0.25–0.7 in Phase 4: large services re-read under trimming. Specs that name exact line ranges and the 800-line reads help; budget T3 at ~USD 1 per task until the matrix offers a cheaper agentic model.
4. *The reviewer and the orchestrator keep finding concurrency bugs workers do not:* a lost wake-up in the external monitor, a lookup-outside-the-lock in the song lock, an options callback that could throw; all fixed with tests (the lock's interleaving is closed by construction, not reproduced).
5. *Read what the UI really sends.* Run now sent the display name; download links carried no key. Both passed their tests because the tests asserted what the code did, not what the server needs.

**Open items / follow-ups.**
- **The identity rule may be too strict:** an upgrade candidate must not score a lower identity than the held file's candidate; when the held file matched near-perfectly almost nothing can beat it (live: 978 of 978 rejected `worseIdentity` for "Paint It Black", whose row still references the cover's candidate after the repair). Consider a small tolerance, or comparing only when the held file was fingerprint-confirmed; golden cases first.
- **Live data on `ch01`:** "Paint It Black" restored by hand after the wrong upgrade (`wondarr.db.pre-repair-58.bak`; set its `song_file.source_ref` to NULL so the identity rule stops applying); "Wannabe" was upgraded on probe and length only (likely right, unconfirmed); YouTube is now enabled on the test instance. Plex seeing one file after a live upgrade is still to show (no Plex linked).
- **P5-07 remainder:** writing slskd's own settings through its YAML options API in external mode (a JWT session from the web login; needs `remote_configuration: true`); the web-login fields are stored for it.
- Backlog 2026-10-07-08 (the OpenAPI document types enums as integers while the API sends strings), -09 (no Housekeeping job: the command table grows a row a minute).
- **Budget:** the OpenRouter key has USD 2.29 left of its USD 20 limit. Raise it before Phase 6 (≈ USD 3–5 at this phase's rates).
- Still open from earlier: the 500-file live check (Phase 3), the Match queue pass on ch01's wedding files, Plexamp's lyrics display, backlog 2026-09-30-01/-02.

**Next: Phase 6 — import lists and playlist sync (`docs/build/PHASES.md`).** Write `docs/build/PHASE_6_TASKS.md` first (research first: Exportify's current CSV columns, Deezer's public playlist API, ListenBrainz/Last.fm endpoints, Plex `/playlists/upload` vs rating keys). Then:
1. **P6-01 ImportList framework + CSV** — `import_list`/`import_list_item` exist since Phase 1: the provider interface, the Exportify column set (`Track Name`, `Artist Name(s)`, `Album Name`, `Track Duration (ms)`, `ISRC`, `Album Release Date`, `Explicit?`) plus a generic column mapper, resolution through the identity resolver with ISRC first, the unresolved-review state, per-list quality profile/source profile/library, a scheduled `ImportListSync`.
2. **P6-02 Deezer playlist and YouTube Music playlist providers** — no auth (Deezer's public API; InnerTube `browse` for a playlist id), recorded fixtures, the same resolution path.
3. **P6-03 Sync policies and playlist output** — add-only / add-and-unmonitor-removed / mirror; Plex playlist via `/playlists/upload` or rating keys (research which keeps order and updates in place) and `.m3u8` export; the gate's "200-track Exportify CSV → ≥ 95 % via ISRC → a Plex playlist after two search cycles".
