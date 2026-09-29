# Progress

> **Name:** the project was renamed from *Compilarr* to **Wondarr** on 2026-09-28 (Phase 1a, `docs/DECISIONS.md` build session 2 #10). Rows before Phase 1a use the old name and paths (`Compilarr.*`).

Updated by the orchestrator after every merged task. Status: `todo` · `in-progress` · `review` · `done` · `blocked`.

## Phase 0 — Repository and skeleton

**Budget:** USD 10 of worker spend (owner, 2026-09-28), excluding the bake-off. **Spent:** see the cost column. The OpenRouter key's usage counter is the ground truth (token × list-price estimates in `.worker/<id>/runs.jsonl` run ~5× low for DeepSeek because OpenRouter routes to pricier providers); per-task figures apportion the counter's deltas across tasks that ran in parallel.

| Task | Title | Status | Worker model | Cost (USD) | Notes |
|---|---|---|---|---|---|
| P0-01 | Solution skeleton and build props | done | deepseek/deepseek-v4.1-flash | 0.02 | Bake-off winner (2 runs: the first hit the 25-turn cap). Orchestrator fix: FluentAssertions 8 → 7.2.2 (8.x is commercially licensed), `global.json` 10.0.101 → 10.0.100, final newlines |
| P0-02 | Configuration loading | done | deepseek/deepseek-v4.1-flash | ~0.08 | One run, all criteria met. Orchestrator fixes: validator no longer echoes an invalid API key; unused DataAnnotations package dropped; enum values written camelCase |
| P0-03 | Persistence (EF Core + SQLite + migrations) | done | deepseek/deepseek-v4.1-flash | ~0.09 | Two runs (turn cap). Orchestrator: UTC `DateTime` converter + test; wired into `Program.cs`; shared `CompilarrAppFactory` for API tests (SQLite pool clearing on Windows) |
| P0-04 | Auth, URL base, /ping (status/health split to P0-04b) | done | deepseek/deepseek-v4.1-flash | ~0.30 | Three runs (two turn caps). Orchestrator: .NET 10 cookie auth answers 401 (not 302) for `[ApiController]` endpoints, so `/initialize.json` is 401 when unauthenticated; reviewer agent → constant-work PBKDF2 check (username-enumeration timing), `returnUrl` kept across a failed login; 13 Lidarr ports added to `NOTICE.md` + CI check |
| P0-04b | System status and health checks | done | deepseek/deepseek-v4.1-flash | ~0.08 | Two runs. Orchestrator: `HealthCheckService` was scoped, so its 60 s cache lived for one request — now a singleton resolving checks per run (+ API test); 7 more Lidarr ports in `NOTICE.md` |
| P0-05 | Logging with redaction | done | deepseek/deepseek-v4.1-flash | ~0.21 | Two runs, both hit the turn cap mid-debug. Orchestrator finished it: `preserveStaticLogger` + host-owned request logger (parallel test hosts were sharing `Log.Logger`), shared-read of the open log file, one wrong golden fixture, removed a leftover probe test. Known gap for Phase 2: a secret containing JSON-escaped characters would not be matched verbatim |
| P0-06 | Event aggregator + persisted command queue (scheduler split to P0-06b) | done | deepseek/deepseek-v4.1-flash | ~0.10 | Two runs (turn cap). Orchestrator: fixed a startup race — orphaning ran inside `ExecuteAsync`, after `StartAsync` returned, so a command queued during startup was marked orphaned and never ran (flaky tests, 0/8 after moving it into `StartAsync`); one test posted an unregistered command; P0-03 migrator tests now tolerate later migrations; OpenAPI snapshot regenerated |
| P0-06b | Quartz scheduler, job table sync, task list API | done | deepseek/deepseek-v4.1-flash | ~0.10 | Two runs, left not compiling. Orchestrator finished: Quartz 4 API (`ScheduleJobOptions` parameter, `ValueTask` returns, `using Quartz`), trigger bound with `ForJob`, a self-contradicting test, missing test logging, and a publish-after-save race in an executor test (0/5 failures after); OpenAPI snapshot + NOTICE updated |
| P0-07 | SignalR events hub | done | deepseek/deepseek-v4.1-flash | ~0.12 | Two runs; merged clean. Orchestrator: OpenAPI snapshot regenerated, NOTICE row for the multi-file SignalRMessage port fixed by hand (and the `--fix` parser no longer crosses lines) |
| P0-08a | SPA hosting behind the URL base + OpenAPI snapshot | done | deepseek/deepseek-v4.1-flash | ~0.12 | Three runs (turn caps). Orchestrator, verified on the running app: the catch-all SPA fallback also claimed asset paths (`/compilarr/assets/app.js` returned index.html) → `{*path:nonfile}`; renamed namespace `Compilarr.Api.System` → `SystemInfo` (it shadowed `System.*`); snapshot regenerated after merge |
| P0-08 | Frontend shell | done | deepseek/deepseek-v4.1-flash | ~0.25 | Two runs; lint/typecheck/15 tests/format/build green. Orchestrator, checked in a real browser under `/compilarr`: the dev fallback script contained the URL-base placeholder literally, the server rewrote it too, and it then reset `<base>` to `/` (assets loaded from the site root — broken behind a path-prefix proxy) → placeholder assembled at runtime + regression test; added a favicon |
| P0-09a | Soulseek settings, slskd.yml rendering, client, fetch script | done | deepseek/deepseek-v4.1-flash | ~0.15 | Three runs (turn caps). Orchestrator finished: two test assertion fixes; renderer now always writes LF (YamlDotNet used CRLF on Windows, breaking the golden files after checkout); fetch script marked executable |
| P0-09 | Bundled slskd supervisor (SlskdHost) | done | deepseek/deepseek-v4.1-flash | ~0.15 | Two runs. Orchestrator: health tests updated for the new slskd entry; reviewer agent (FIX-FIRST) → crash path now takes the lifecycle gate and only stops the process it watched (fixed by inspection: the exact window is not deterministically reproducible with fake time — a mutation check confirmed the new test does not pin it), and the options-monitor debounce is now exercised by a test. Real-process behaviour is verified in the image (P0-10) |
| P0-10 | Dockerfile, s6 init, compose | done | deepseek/deepseek-v4.1-flash | ~0.15 | One run (files only; the worker cannot run Docker). Orchestrator, from the first real builds: Ubuntu 24.04 base ships an `ubuntu` user on uid/gid 1000 (removed); slskd refuses to start when its directories are missing (host now creates them); init re-owns mismatched files under `/config`; EF/HTTP logging quietened. Image 772 MB (amd64, uncompressed) |
| P0-11 | CI and release workflows | done | orchestrator | — | Workers may not edit `.github/workflows/`. `ci.yml`: backend (+NOTICE check), frontend, image build + `scripts/smoke-test.sh` (the whole Phase 0 gate) + image artifact; `release.yml`: multi-arch `:develop` on main, semver on `v*`; Dependabot (FluentAssertions ≥ 8 ignored) |
| P0-12 | Contributor docs and NOTICE | done | deepseek/deepseek-v4.1-flash (single-shot) | ~0.01 | First attempt returned pseudo tool calls: the API mode now tells the model it has no tools. Orchestrator corrected CONTRIBUTING (smoke-test usage, no `dev.sh`), CHANGELOG claims that did not match the code, kept the generated NOTICE table and took only the bundled-programs section |

Phase 0 gate (from `PHASES.md`): container starts on port 1077, UI loads behind a URL base, API key works, health shows DB, folders and the bundled slskd process OK (logged out until credentials are entered), a scheduled no-op job survives a restart.

### Phase 0 gate — PASS (2026-09-28)

`scripts/smoke-test.sh` checks every gate item against the built image; it passed in CI (GitHub runner) and on the test host `ch01` with the same image:

```
ok   container healthy (HEALTHCHECK on /ping)
ok   /ping at the root and under /compilarr
ok   API key required and accepted (header, query, bearer); system/status reports Docker and the URL base
ok   UI, assets, deep links and initialize.json under /compilarr
ok   health: DatabaseHealthCheck, ConfigFolderHealthCheck, LogFolderHealthCheck, slskd all ok; slskd running, Soulseek not configured
ok   POST /api/v1/command Heartbeat completed; task last run …
ok   scheduled job state survives a restart
ok   files carry PUID:PGID (1234:2345); app runs as compilarr
ok   ffprobe, fpcalc, deno and slskd run inside the image
PHASE 0 GATE: PASS
```

The UI was also checked in a real browser under `/compilarr` (status page, health list, navigation). CI is green on `main`, and the tag `v0.0.1-alpha.1` built and pushed `ghcr.io/wulfftech/compilarr:0.0.1-alpha.1` for `linux/amd64` and `linux/arm64` (prerelease, so `:latest` is untouched). Tests: 209 backend (xUnit), 15 frontend (Vitest).

**Spend:** Phase 0 worker spend **USD 1.12** of the USD 10 budget (OpenRouter key counter), plus USD 0.35 for the bake-off. Per-task figures in the table apportion the counter across parallel runs.

Environment notes: no local Docker on the dev PC — container checks run on `ch01.ad.wulff.com.au` over SSH (`sudo docker`, container name `compilarr-test`), per the owner. Node upgraded to 24.19.0 LTS for the frontend.

## Phase 1 — Song identity and the Wanted list

**Budget:** USD 10 of worker spend (owner, 2026-09-28). Plan and dependencies: `docs/build/PHASE_1_TASKS.md`. Costs from the OpenRouter key counter, apportioned across parallel runs.

| Task | Title | Status | Worker model | Cost (USD) | Notes |
|---|---|---|---|---|---|
| P1-01 | Domain model, quality seeds, default profiles | done | deepseek/deepseek-v4.1-flash | ~0.05 | One run. Orchestrator: `HasDefaultValue(true)` on `monitored`/`sticky` made EF drop an explicit `false` on insert (unmonitored songs stored as monitored) → removed, migration regenerated, regression test (mutation-checked); CA1861 for generated migrations moved to `.editorconfig` |
| P1-02 | MusicBrainz client, request spacing, metadata cache | done | deepseek/deepseek-v4.1-flash | ~0.09 | One run (83 turns; the cap counts differently from tool calls). Reviewer agent: MERGE, no blockers. Orchestrator: migration regenerated after AddDomainModel; final newlines; live tests run once (3 requests, pass). Later found under load: `Task.Delay` can end ~15 ms early on Windows, so the gate now re-checks the clock before releasing; pipeline timing tests allow 50 ms for the handler hop |
| P1-03 | Version-flag parser | done | deepseek/deepseek-v4.1-flash | ~0.05 | One run, 26 turns; all 75 golden cases unchanged, table-driven keyword rules. Merged as is |
| P1-04 | Cover art, Deezer and iTunes clients | done | deepseek/deepseek-v4.1-flash | ~0.08 | One run, hit the turn cap while adding final newlines with a green tree; committed by the orchestrator. Live tests pass (3 requests) |
| P1-05 | Album-policy engine | done | deepseek/deepseek-v4.1-flash | ~0.05 | One run, 37 turns; all 10 golden cases unchanged plus 3 facts. Merged as is |
| P1-06 | Identity resolver | done | deepseek/deepseek-v4.1-flash | ~0.09 | One run (27 min), cap hit with the resolver tests green. Orchestrator: diacritics were never stripped — `InvariantGlobalization` makes `Normalize(FormD)` a no-op for non-ASCII, silently breaking 'Sigur Rós'/'Björk' matching here **and** in P1-04's cover-art artist match → explicit Latin fold table `TextFolding`. Live probe over the 50 gate songs: 49 MusicBrainz, 1 Deezer-only; 3 wrong recordings → P1-06b |
| P1-06b | Resolver ranking fixes from the live probe | done | deepseek/deepseek-v4.1-flash | ~0.05 | One run, merged as is (duration-bounded acceptance and search, title-only Deezer retry, skip bootleg-only winners). Orchestrator, from re-probing live: two parser rules — 'studio mix' is neutral (Beatles 'original studio mix'), an 'album version' disambiguation cancels Live (Purple Rain's album take is a 1983 live recording). Known gap: 'Robyn - Dancing On My Own' has no Deezer reference and resolves to the 218 s edit (follow-up: iTunes as a third reference) |
| P1-07 | Song add service (Core) | done | deepseek/deepseek-v4.1-flash | ~0.07 | One run (74 turns), all criteria; merged as is |
| P1-07b | Song/Artist/lookup/preview API | done | deepseek/deepseek-v4.1-flash | ~0.05 | One run at the new 100-turn cap (75 used). Accepted deviation: the 'existing song' lookup is a static query helper in the API layer (Core was outside its paths). Checked against the running app with live services: lookup ranks the ISRC-bridged album recording first, add → 201 with the Singles pseudo-album and the RAM cover, re-add → 409 with `songId`, fresh signed preview URL, moving the song onto Random Access Memories gives track 8 of 13 |
| P1-08 | Quality/profile/library API | done | deepseek/deepseek-v4.1-flash | ~0.07 | One run; hit the turn cap on its commit turn with a green tree. Orchestrator: merge conflicts (service registration, OpenAPI snapshot regenerated), final newlines |
| P1-09 | Bulk add and unresolved review | done | deepseek/deepseek-v4.1-flash | ~0.20 | One run, all 100 turns, 8 Core tests red. Orchestrator: job test hosts now register the metadata services as the app does (CommandQueue builds every handler; BulkAddSongs needs the resolver); a latent test-factory race — `WebApplicationFactory.Dispose(true)` re-enters `Dispose(true)` via `DisposeAsync`, deleting the config folder while the host still holds its log file — guarded, plus a retrying delete (10/10 clean runs) |
| P1-10 | Wanted, History, Blocklist API | done | deepseek/deepseek-v4.1-flash | ~0.06 | One run; hit the turn cap before committing, tree green — committed by the orchestrator instead of a paid continuation. Migration regenerated after AddMetadataCache; snapshot refreshed. A pre-existing test race surfaced here: test hosts called `SqliteConnection.ClearAllPools()`, disposing other parallel tests' connections (~1 in 5 runs) → each clears only its own pool |
| P1-11 | Frontend: songs, add, paste, unresolved | done | deepseek/deepseek-v4.1-flash | ~0.48 | One run, all 100 turns, 50/52 tests green (two shell tests still expected the Phase 0 placeholder). Checked in a real browser (Playwright) against the running app and live services: search/preview/add/paste/unresolved/Library/Wanted/Settings work; two bugs found and fixed with regression tests — the top ISRC-bridged search result had no Deezer id/ISRC so its preview was disabled, and the paste summary counts were read once while pending and never refreshed. Nit left: version-flag badges show wire names (`radio_edit`) |
| P1-12 | Frontend: Wanted, Activity, Profiles, Library | done | deepseek/deepseek-v4.1-flash | ~0.11 | Two runs (cap both times). Orchestrator: `ResizeObserver` stub in the test setup (Mantine needs one, jsdom has none); the shared fetch mock now records the method/headers of the `Request` openapi-fetch passes — two tests were asserting on calls they could never see |
| P1-13 | Phase 1 gate automation | done | orchestrator | — | `tests/gate/phase1-songs.txt` (50 lines) + independent iTunes duration reference, `scripts/metadata-replay.py` (record/replay, gzipped), `scripts/phase1-gate.py`, smoke-test + CI wiring, `metadata.musicbrainz_mirror_interval_ms`. The image build did not copy `.editorconfig` (analyzer severities differed from the repo build) — fixed in the Dockerfile |

**Spend so far (2026-09-28): USD 1.69** by the OpenRouter key counter (3.16 now − 1.47 at the end of Phase 0); per-task figures are apportioned estimates. Workers ran at 50 turns / 30 min until P1-07b, then at 100 turns / 60 min (owner request).

Phase 1 gate (from `PHASES.md`): a pasted list of 50 songs resolves ≥ 90 % to MB recordings with correct durations and cover art; the rest resolve via Deezer or land in an "unresolved" review state; every song has an album assignment under the library's policy.

### Phase 1 gate — PASS (2026-09-28)

Live, against MusicBrainz, Cover Art Archive, Deezer and iTunes (local app behind the recording proxy, 363 s at 1 req/s):

```
ok   BulkAddSongs completed in 363 s: 50 lines: 50 added, 0 already in the library, 0 unresolved, 0 skipped
MB with correct duration and cover: 48/50 (96%); Deezer only: 1; unresolved: 0
PHASE 1 GATE: PASS
```

The one MusicBrainz miss is "Robyn - Dancing On My Own" (the 218 s edit instead of the 287 s album version: Deezer has no reference for it); "Rosalía - Malamente" resolved via Deezer. Every song got an album assignment (with 50 different artists and `min_tracks_per_real_album` 2, each lands in its artist's Singles album, as the `fewest_albums` policy says). The recording replays in CI inside the built image, together with the Phase 0 gate (`scripts/smoke-test.sh`, run 36443741783):

```
PHASE 0 GATE: PASS (compilarr:ci)
ok   BulkAddSongs completed in 18 s: 50 lines: 50 added, 0 already in the library, 0 unresolved, 0 skipped
MB with correct duration and cover: 48/50 (96%); Deezer only: 1; unresolved: 0
ok   every metadata request was answered from the recording
PHASE 1 GATE: PASS (compilarr:ci, metadata: replay)
```

The same `scripts/smoke-test.sh` passed on the test host `ch01` against the published multi-arch image `ghcr.io/wulfftech/compilarr:develop` (pulled anonymously — the package is public), digest `sha256:f2d3b658…`.

The UI was checked in a real browser (Playwright) against the running app with live services: Add songs (search, preview, add, paste with progress), Unresolved review, Library, Wanted, Settings.

## Phase 1a — Rename Compilarr → Wondarr

Plan: `docs/build/PHASE_1A_TASKS.md`; decision: `docs/DECISIONS.md` build session 2 #10. Done by the orchestrator (no worker spend): a case-preserving script renamed 302 paths and 348 files, historical records keep the old name with a note.

| Task | Title | Status | Notes |
|---|---|---|---|
| P1a-01 | Decision record + name-collision check | done | Only prior use: a dormant single-commit 2018 repo `wondarr/wondarr` |
| P1a-02 | Positioning (README, PRODUCT, GOALS, CHANGELOG) | done | Written by the orchestrator instead of a single-shot worker (a few paragraphs) |
| P1a-03 | Mechanical rename | done | Solution, projects, namespaces, frontend package, Docker user and s6 services, workflows, image `ghcr.io/wulfftech/wondarr`, env names `WONDARR_*`, cookie `WondarrAuth`, OpenAPI title |
| P1a-04 | Upgrade path | done | `compilarr.db` (+ `-wal`/`-shm`) adopted as `wondarr.db` on first start (tests); `COMPILARR_CONFIG_DIR` still read; `worker.py` falls back to `COMPILARR_*` keys |
| P1a-05 | Outside the repo | done (owner: folder) | GitHub repo renamed `wulfftech/wondarr` (old URLs redirect), local remote updated, `ghcr.io/wulfftech/wondarr:develop` published and anonymously pullable, session memory copied for the new folder path, backlog items re-pointed. **Owner, between sessions:** rename the folder to `D:\Code\wondarr` and re-add the `claude-terminals` MCP for it |
| P1a-06 | Verification | done | Backend 600 + frontend 52 tests green; Phase 0 + Phase 1 gates PASS in CI (run 36448123692, `wondarr:ci`) and on `ch01` with `ghcr.io/wulfftech/wondarr:develop` (pulled anonymously, app runs as `wondarr`); real upgrade on `ch01`: the old `compilarr:develop` wrote a song into `compilarr.db` (+ WAL), the new image renamed all three files, logged it once and served the song |

## Worker bake-off (2026-09-28)

Task P0-01 for all three, same spec, 25 turns and USD 1.50 real cap per run, run in parallel worktrees; one continuation round each ("you ran out of turns — finish, build, test, commit").

| Model | Tasks | Passed w/o fixes | Passed w/ 1 fix | Failed | Cost | Decision |
|---|---|---|---|---|---|---|
| deepseek/deepseek-v4.1-flash | P0-01 | | ✔ — committed, done-report, 5 tests across 3 projects | | ~0.02 | **Pinned** |
| z-ai/glm-5.3-flash | P0-01 | | | ✔ — tree green but never committed; spent turns on NuGet lookups; left scratch files outside allowed paths | ~0.09 | |
| qwen/qwen3-coder-next | P0-01 | | | ✔ — never committed; Core/Sources test projects had no tests | ~0.20 | |

Total bake-off spend (key usage, incl. smoke tests): **USD 0.35**. Findings that changed the runner (`scripts/worker.py`): Claude Code prices unknown model ids at Opus rates, so its `total_cost_usd` was ~30× too high and `--max-budget-usd` would have cut runs off early (fixed: real cost from tokens, scaled budget flag); most wasted turns were denied Bash calls (`cd <abs> && …`, `curl` for NuGet versions) — fixed by widening the read-only allowlist and putting exact package versions into the specs.

## Phase 2 — Soulseek source, slskd management and the import pipeline

**Budget:** USD 10 of worker spend (owner), but the OpenRouter key's lifetime limit leaves **USD 6.84** at the start of the phase (usage 3.16). Plan and dependencies: `docs/build/PHASE_2_TASKS.md`. Costs: token × price estimates from `.worker/<id>/runs.jsonl`; the phase total is the key counter. Workers run at 100 turns / 60 min, launched as background processes (the `claude-terminals` MCP server is not registered for the renamed folder; `python scripts/worker.py watch` follows them).

| Task | Title | Status | Worker model | Cost (USD) | Notes |
|---|---|---|---|---|---|
| P2-00 | Contracts, research, fixtures, account validation | done | orchestrator | — | Soulseek account validated on `ch01` (logged in, no duplicate-login kick in a 10-minute watch; a first attempt failed only because the orchestrator read `.env` values including their inline comments). slskd 0.26.0 API facts verified against source and live; real search/state responses recorded (`tests/fixtures/slskd/live/`, peer names anonymised). AcoustID: the application key works (real lookups recorded, `tests/fixtures/acoustid/`); it looked invalid at first for the same inline-comment reason, and a user API key does not work for lookups. Media fixtures recorded in the image. Found: an unwritable `/data` stopped the whole host (→ P2-15) |
| P2-01 | slskd search client, search budget, runner | done | deepseek/deepseek-v4.1-flash | ~0.23 | One run. Reviewer agent: FIX-FIRST (lost wake-up that could stall the budget for good, no FIFO, a start that failed after the POST left the search on slskd, a hung request not bounded by the wall clock, wall-clock time). Orchestrator rewrote the budget (turnstile held across the wait, generation signal, monotonic time, a submission exactly 240 s old still counts, waits rounded up to whole ms — `Task.Delay` truncates, so a sub-ms wait spun) and fixed the runner; +5 tests, 5/5 loops green |
| P2-02 | Filename/path parser, quality inference | done | deepseek/deepseek-v4.1-flash | ~0.16 | One run; all 31 real-path and 32 quality golden cases unchanged; every recorded path parses. Three spec ambiguities resolved by the golden file (documented in the code) |
| P2-03 | Decision engine v1 | done | deepseek/deepseek-v4.1-flash | ~0.17 | One run, 56 turns; 21 golden cases unchanged + 15 component facts. Merged as is (final newlines) |
| P2-04 | Media tools: ffprobe, measured quality, fpcalc | done | deepseek/deepseek-v4.1-flash | ~0.14 | One run. Two quality ids in the spec's examples did not match the P2-02 rules for the synthetic tones (V0 tone 107 kbps → MP3-128; AAC tone → AAC-192) — the tests assert the rules. Health-check count test updated |
| P2-06 | Tag writer (ATL) | done | deepseek/deepseek-v4.1-flash | ~0.24 | Hit the turn cap uncommitted with one wrong test (expected `cpil` on a non-compilation M4A) and a debug probe left in; orchestrator fixed and committed. ATL 7.17 cannot write a `UFID` frame: the recording MBID goes to `TXXX:MusicBrainz Track Id` only |
| P2-07 | Naming template engine | done | deepseek/deepseek-v4.1-flash | ~0.17 | One run. My golden file contradicted the "file name must contain {Track Title}" rule; the worker's narrower rule (the last segment must contain a non-album token) is accepted |
| P2-08 | search_run / candidate / queue_item / soulseek_user | done | deepseek/deepseek-v4.1-flash | ~0.15 | One run. CA1711 on the generated migration name suppressed for the migrations folder (`.editorconfig`, like CA1861). Note for later tasks: updating a *detached* queue item resets `LastProgressAt` |
| P2-09 | File placement, recycle bin, remote path mappings | done | deepseek/deepseek-v4.1-flash | ~0.33 | Three runs (two review continuations). Reviewer agent FIX-FIRST twice: an unvalidated `ReplacesPath` could recycle the download itself or a file outside the library; no rollback after recycling; recycle-bin cleanup could walk out of the bin (trailing separator) or clean a directory overlapping the library → strict containment, move-back on failure, size check before deleting a source, unique `.partial` names, a `.wondarr-recycle-bin` marker without which cleanup does nothing |
| P2-15 | slskd health, login problems, no crash on an unwritable /data | done | deepseek/deepseek-v4.1-flash | ~0.30 | Two runs. Reviewer agent FIX-FIRST: other exception types still stopped the host, a failed settings write faked a crash and was never retried, log markers matched anywhere in a line (a peer's search text could raise or clear a login error), the folder probe could block → all fixed and tested; 3/3 full-suite loops green. Default shared folder is now the default library root `/data/music` (docs aligned: they said `/data/media/music`) |
