# Progress

Updated by the orchestrator after every merged task. Status: `todo` · `in-progress` · `review` · `done` · `blocked`.

## Phase 0 — Repository and skeleton

**Budget:** USD 10 of worker spend (owner, 2026-09-28), excluding the bake-off. **Spent:** see the cost column; costs are real OpenRouter USD from token usage × OpenRouter prices (`.worker/<id>/runs.jsonl`), cross-checked against the key's usage counter.

| Task | Title | Status | Worker model | Cost (USD) | Notes |
|---|---|---|---|---|---|
| P0-01 | Solution skeleton and build props | done | deepseek/deepseek-v4.1-flash | 0.02 | Bake-off winner (2 runs: the first hit the 25-turn cap). Orchestrator fix: FluentAssertions 8 → 7.2.2 (8.x is commercially licensed), `global.json` 10.0.101 → 10.0.100, final newlines |
| P0-02 | Configuration loading | todo | | | |
| P0-03 | Persistence (EF Core + SQLite + migrations) | todo | | | |
| P0-04 | Auth, URL base, ping/status/health | todo | | | |
| P0-05 | Logging with redaction | todo | | | |
| P0-06 | Command queue, scheduler, job table | todo | | | |
| P0-07 | SignalR events hub | todo | | | |
| P0-08 | Frontend shell | todo | | | |
| P0-09 | Bundled slskd supervisor (SlskdHost) | todo | | | |
| P0-10 | Dockerfile, s6 init, compose | todo | | | |
| P0-11 | CI and release workflows | todo | | | |
| P0-12 | Contributor docs and NOTICE | todo | | | |

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
