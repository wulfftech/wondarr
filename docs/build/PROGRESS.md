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
| P0-10 | Dockerfile, s6 init, compose | review | deepseek/deepseek-v4.1-flash | ~0.08 | One run (files only; the worker cannot run Docker). Orchestrator: init now re-owns mismatched files under `/config` so a PUID change covers the DB. Image build + gate smoke test run in CI and on ch01 |
| P0-11 | CI and release workflows | todo | | | |
| P0-12 | Contributor docs and NOTICE | done | deepseek/deepseek-v4.1-flash (single-shot) | ~0.01 | First attempt returned pseudo tool calls: the API mode now tells the model it has no tools. Orchestrator corrected CONTRIBUTING (smoke-test usage, no `dev.sh`), CHANGELOG claims that did not match the code, kept the generated NOTICE table and took only the bundled-programs section |

Phase 0 gate (from `PHASES.md`): container starts on port 1077, UI loads behind a URL base, API key works, health shows DB, folders and the bundled slskd process OK (logged out until credentials are entered), a scheduled no-op job survives a restart.

Environment notes: no local Docker on the dev PC — container checks run on `ch01.ad.wulff.com.au` over SSH (`sudo docker`, container name `compilarr-test`), per the owner. Node upgraded to 24.19.0 LTS for the frontend.

## Worker bake-off (2026-09-28)

Task P0-01 for all three, same spec, 25 turns and USD 1.50 real cap per run, run in parallel worktrees; one continuation round each ("you ran out of turns — finish, build, test, commit").

| Model | Tasks | Passed w/o fixes | Passed w/ 1 fix | Failed | Cost | Decision |
|---|---|---|---|---|---|---|
| deepseek/deepseek-v4.1-flash | P0-01 | | ✔ — committed, done-report, 5 tests across 3 projects | | ~0.02 | **Pinned** |
| z-ai/glm-5.3-flash | P0-01 | | | ✔ — tree green but never committed; spent turns on NuGet lookups; left scratch files outside allowed paths | ~0.09 | |
| qwen/qwen3-coder-next | P0-01 | | | ✔ — never committed; Core/Sources test projects had no tests | ~0.20 | |

Total bake-off spend (key usage, incl. smoke tests): **USD 0.35**. Findings that changed the runner (`scripts/worker.py`): Claude Code prices unknown model ids at Opus rates, so its `total_cost_usd` was ~30× too high and `--max-budget-usd` would have cut runs off early (fixed: real cost from tokens, scaled budget flag); most wasted turns were denied Bash calls (`cd <abs> && …`, `curl` for NuGet versions) — fixed by widening the read-only allowlist and putting exact package versions into the specs.
