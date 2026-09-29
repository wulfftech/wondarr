# Next-session prompt (Phase 3 kickoff)

Launch Claude Code in `D:\Code\wondarr` on the orchestrator model (`/model claude-opus-5-5`), then paste the prompt below as the first message. (Earlier kickoff prompts are in this file's git history.)

---

You are the **orchestrator** for Wondarr (Windows, `D:\Code\wondarr`). Read first: `CLAUDE.md`, `docs/HANDOVER.md` (especially §7 Session log, the build-session-3 entry), `docs/build/PROGRESS.md` (Phase 2), `docs/build/AGENT_WORKFLOW.md`, `docs/build/PHASES.md` (Phase 3), `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` and consult `docs/architecture/*.md` (especially `LIBRARY_OUTPUT.md` §7.2–7.6) per task. They are binding.

**Goal:** complete **Phase 3 — reference libraries, adoption and the matching UI; the Plexamp preset** to its "done when" gate in `PHASES.md`, with cheap workers doing the implementation, and leave the repo ready for Phase 4.

**Pre-flight (one short report):** toolchain; `.env` presence (never print values; read values with the inline-comment rule — `worker.py`'s loader — never with a bare `cut -d=`); CI green on `main` (Phase 0 + 1 + 2 gates); `ghcr.io/wulfftech/wondarr:develop` pulls anonymously; `worker.py --dry-run` shows 100 turns / 60 min; the OpenRouter key's remaining limit (Phase 2 used USD 4.46 and ended at 7.62 of a USD 10 lifetime limit; the owner approved raising the spend limit to USD 15 on 2026-09-29 — tell me what the key shows and whether it covers the phase).

**Plan:** write `docs/build/PHASE_3_TASKS.md` (dependency table, tasks ≤ ~10 files), starting from the three tasks in HANDOVER §7. Verify Plex facts (PIN auth flow, `refresh?path=`, `emptyTrash`, section listing, playlist upload) and LRCLIB with the researcher agent before specifying them, and record real responses as fixtures (`PLEX_TOKEN` is in `.env`). Show me the task table, then delegate without waiting.

**Per task:** as in Phase 2 — spec from `WORKER_TASK_TEMPLATE.md` with exact versions and changed signatures → commit the spec → run the worker (`python -u scripts/worker.py run docs/build/tasks/<id>.md`; visible VS Code tabs via `claude-terminals` if that MCP server is registered for this folder) → review adversarially, run the builds, tests and frontend checks yourself, loop the whole suite → reviewer agent for anything touching processes, files, time, the database, auth or external limits (it returned FIX-FIRST on most of Phase 2's risky tasks) → fix via a continuation (`CONTINUE.md` in the worktree) or finish it yourself → merge only through a script that refuses a red tree before and after the merge (build `main` after every merge: parallel tasks conflict semantically) → update `PROGRESS.md`, docs, `NOTICE.md`, the OpenAPI snapshot and the frontend types → Conventional Commit, no model names, no `Co-Authored-By` trailer → push.

**Verification beyond unit tests:** a 5-song live smoke on `ch01` before any bigger live run (the real network found three bugs in Phase 2 that no fixture did); run the real app and a browser for UI work; extend `scripts/smoke-test.sh` with the Phase 3 gate so CI proves what it can (a synthetic reference library in the image, FakeSlskd where downloads are needed); live calls only in opt-in tests and within every service's limits.

**Rules:** everything in `CLAUDE.md` "Non-negotiables"; never merge red; workers never touch `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; no secrets or AI identifiers in commits. Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** the Phase 3 gate passes (automated in CI as far as possible, the rest demonstrated on `ch01` with a real Plex server), `PROGRESS.md` has a complete Phase 3 section with costs, and `docs/HANDOVER.md` gets a dated Session log entry: what was built, worker lessons, and the first three Phase 4 tasks to spec. If you run out of time or budget first, write the handover entry anyway and push.
