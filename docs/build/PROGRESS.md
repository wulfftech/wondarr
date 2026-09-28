# Progress

Updated by the orchestrator after every merged task. Status: `todo` · `in-progress` · `review` · `done` · `blocked`.

## Phase 0 — Repository and skeleton

| Task | Title | Status | Worker model | Cost (USD) | Notes |
|---|---|---|---|---|---|
| P0-01 | Solution skeleton and build props | todo | | | |
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

## Worker bake-off

| Model | Tasks | Passed w/o fixes | Passed w/ 1 fix | Failed | Cost | Decision |
|---|---|---|---|---|---|---|
| | | | | | | |
