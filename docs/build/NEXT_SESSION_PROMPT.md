# Next-session prompt (Phase 2 kickoff)

Launch Claude Code in `D:\Code\wondarr` on the orchestrator model (`/model claude-opus-5-5`), make sure the `claude-terminals` MCP server is connected (`/mcp`), then paste the prompt below as the first message. (Earlier kickoff prompts are in this file's git history.)

---

You are the **orchestrator** for Wondarr (Windows, `D:\Code\wondarr`). Read first: `CLAUDE.md`, `docs/HANDOVER.md` (especially §7 Session log, the two build-session-2 entries), `docs/build/PROGRESS.md`, `docs/build/AGENT_WORKFLOW.md`, `docs/build/PHASES.md` (Phase 2), `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` and consult `docs/architecture/*.md` (especially `MATCHING_ENGINE.md` and `LIBRARY_OUTPUT.md`) per task. They are binding.

**Goal:** complete **Phase 2 — the Soulseek source, slskd management and the import pipeline** to its "done when" gate in `PHASES.md`, with cheap workers doing the implementation, and leave the repo ready for Phase 3.

**Pre-flight (one short report):** toolchain; `.env` has `OPENROUTER_API_KEY`, `WONDARR_WORKER_MODEL` (or the old `COMPILARR_*` names), `ACOUSTID_CLIENT_KEY`, `PLEX_TOKEN`, `SOULSEEK_USERNAME/PASSWORD` set (presence only, never print values); CI green on `main`; `ghcr.io/wulfftech/wondarr` is public; `worker.py --dry-run` shows 100 turns / 60 min; the OpenRouter key's remaining limit.

**First:** validate the Soulseek account on `ch01` (HANDOVER §7, P2-01): start `ghcr.io/wulfftech/wondarr:develop` with `APP__SOULSEEK__USERNAME/PASSWORD`, confirm "logged in as …" in `/api/v1/health`, watch for duplicate-login kicks; if they happen, stop and ask me for a dedicated account.

**Plan:** write `docs/build/PHASE_2_TASKS.md` (dependency table, tasks ≤ ~10 files), starting from the three tasks in HANDOVER §7. Use the researcher agent to verify slskd 0.26 API facts (search, hub, transfers, options, webhooks) and AcoustID/fpcalc behaviour before specifying them; record real responses as fixtures. Show me the task table, then delegate without waiting.

**Per task:** as in Phase 1 — spec from `WORKER_TASK_TEMPLATE.md` with exact versions and changed signatures → commit the spec → launch in its own VS Code tab via `claude-terminals` (tabs run cmd.exe: `set "WONDARR_WORKER_MAX_TURNS=100" && python -u scripts/worker.py run docs/build/tasks/<id>.md`) → review adversarially, run the builds, tests and frontend checks yourself, loop the **whole** suite several times → reviewer agent for concurrency, process control, file moves, auth, caches or external rate limits → fix via a continuation (note in the worker's worktree as `CONTINUE.md`) or finish it yourself → merge `--no-ff`, update `PROGRESS.md` (status, cost from the key counter, notes), docs, `NOTICE.md`, OpenAPI snapshot → Conventional Commit, no model names → push.

**Verification beyond unit tests:** run the real app (and a browser for UI work); extend `scripts/smoke-test.sh` with the Phase 2 gate so CI proves what can be proven without the Soulseek network, and run the real downloads on `ch01`; live calls only in opt-in tests (`WONDARR_LIVE_TESTS=1`) and within every service's limits (Soulseek ≤ 30 searches / 4 min, ≤ 2 outstanding; MusicBrainz 1 req/s; AcoustID 3 req/s).

**Rules:** everything in `CLAUDE.md` "Non-negotiables"; never merge red; workers never touch `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; no secrets or AI identifiers in commits. Phase 2 worker budget: **USD 10** (Phase 1 used 1.69; the key's lifetime limit is USD 10 — tell me when it gets close). Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** the Phase 2 gate passes (automated in CI as far as possible, the rest demonstrated on `ch01`), `PROGRESS.md` has a complete Phase 2 section with costs, and `docs/HANDOVER.md` gets a dated Session log entry: what was built, worker lessons, and the first three Phase 3 tasks to spec. If you run out of time or budget first, write the handover entry anyway and push.
