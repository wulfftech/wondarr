# Progress

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
| P1-01 | Domain model, quality seeds, default profiles | done | deepseek/deepseek-v4.1-flash | (wave A) | One run. Orchestrator: `HasDefaultValue(true)` on `monitored`/`sticky` made EF drop an explicit `false` on insert (unmonitored songs stored as monitored) → removed, migration regenerated, regression test (mutation-checked); CA1861 for generated migrations moved to `.editorconfig` |
| P1-02 | MusicBrainz client, request spacing, metadata cache | todo | | | |
| P1-03 | Version-flag parser | done | deepseek/deepseek-v4.1-flash | (wave A) | One run, 26 turns; all 75 golden cases unchanged, table-driven keyword rules. Merged as is |
| P1-04 | Cover art, Deezer and iTunes clients | todo | | | |
| P1-05 | Album-policy engine | todo | | | |
| P1-06 | Identity resolver | todo | | | |
| P1-07 | Song add service (Core) | todo | | | |
| P1-07b | Song/Artist/lookup/preview API | todo | | | |
| P1-08 | Quality/profile/library API | todo | | | |
| P1-09 | Bulk add and unresolved review | todo | | | |
| P1-10 | Wanted, History, Blocklist API | todo | | | |
| P1-11 | Frontend: songs, add, paste, unresolved | todo | | | |
| P1-12 | Frontend: Wanted, Activity, Profiles, Library | todo | | | |
| P1-13 | Phase 1 gate automation | todo | orchestrator | — | |

Phase 1 gate (from `PHASES.md`): a pasted list of 50 songs resolves ≥ 90 % to MB recordings with correct durations and cover art; the rest resolve via Deezer or land in an "unresolved" review state; every song has an album assignment under the library's policy.

## Worker bake-off (2026-09-28)

Task P0-01 for all three, same spec, 25 turns and USD 1.50 real cap per run, run in parallel worktrees; one continuation round each ("you ran out of turns — finish, build, test, commit").

| Model | Tasks | Passed w/o fixes | Passed w/ 1 fix | Failed | Cost | Decision |
|---|---|---|---|---|---|---|
| deepseek/deepseek-v4.1-flash | P0-01 | | ✔ — committed, done-report, 5 tests across 3 projects | | ~0.02 | **Pinned** |
| z-ai/glm-5.3-flash | P0-01 | | | ✔ — tree green but never committed; spent turns on NuGet lookups; left scratch files outside allowed paths | ~0.09 | |
| qwen/qwen3-coder-next | P0-01 | | | ✔ — never committed; Core/Sources test projects had no tests | ~0.20 | |

Total bake-off spend (key usage, incl. smoke tests): **USD 0.35**. Findings that changed the runner (`scripts/worker.py`): Claude Code prices unknown model ids at Opus rates, so its `total_cost_usd` was ~30× too high and `--max-budget-usd` would have cut runs off early (fixed: real cost from tokens, scaled budget flag); most wasted turns were denied Bash calls (`cd <abs> && …`, `curl` for NuGet versions) — fixed by widening the read-only allowlist and putting exact package versions into the specs.
