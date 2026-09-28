# Next-session prompt (Phase 0 kickoff)

Launch Claude Code in `D:\Code\compilarr` on the orchestrator model, e.g. `claude --model claude-opus-5-5` (or `/model claude-opus-5-5` once inside), then paste the prompt below as the first message. Before launching: `.env` exists with `OPENROUTER_API_KEY` filled (copy from `.env.example`); .NET 10 SDK, Node 22+, Python 3.10+, Docker Desktop and Git are installed.

---

You are the **orchestrator** for Compilarr, working in this repository on Windows. Read, in this order, before doing anything else: `CLAUDE.md`, `docs/HANDOVER.md`, `docs/build/PROGRESS.md`, `docs/build/AGENT_WORKFLOW.md`, `docs/build/PHASE_0_TASKS.md`, `docs/build/CODING_STANDARDS.md`, `docs/build/REPO_LAYOUT.md`. Skim `docs/DECISIONS.md` and `docs/adr/README.md`; consult `docs/architecture/*.md` per task. Treat those documents as binding.

**Goal of this session:** complete **Phase 0** (repository skeleton through CI) to its "done when" gate in `docs/build/PHASES.md`, using cheap workers for implementation, and leave the repo in a state the next session can continue from without you.

**Step 1 — Pre-flight (report results in one short message):**
- Toolchain: `dotnet --version` (10.x), `node --version` (22+), `python --version` (3.10+), `docker --version`, `git --version`, and that `claude` is on PATH.
- Secrets: `.env` exists and `OPENROUTER_API_KEY` is set (never print it).
- Worker runner: `python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md` prints a sane command and environment.
- If `COMPILARR_WORKER_MODEL` is empty, run the **bake-off** from `AGENT_WORKFLOW.md` §6 with a hard cap of USD 5: pick three tool-capable low-cost models from OpenRouter, run P0-01 with each (dry specs are ready), score, pin the winner in `.env`, and record the table in `docs/build/PROGRESS.md` and a dated entry in `docs/DECISIONS.md`. If the OpenRouter Anthropic endpoint returns 404s, set `OPENROUTER_ANTHROPIC_BASE_URL=https://openrouter.ai/api/v1` and retry once.

**Step 2 — Execute Phase 0 task by task** (P0-01 → P0-12, respecting the dependency column; run independent tasks in parallel worktrees where sensible):
1. Write the task spec `docs/build/tasks/<id>.md` from `WORKER_TASK_TEMPLATE.md` (P0-01 already exists) with concrete acceptance criteria and allowed paths.
2. Delegate with `python scripts/worker.py run docs/build/tasks/<id>.md`. Do not hand-write the implementation yourself except for small glue, spec fixes, or after a worker has failed twice.
3. Review the worktree diff adversarially against the spec and `CODING_STANDARDS.md`; run `dotnet build -warnaserror`, `dotnet test`, and the frontend checks yourself. Use the `reviewer` agent for P0-04 (auth), P0-09 (slskd process control) and P0-10 (Docker).
4. Fix via `--continue "…"` (max two rounds), then merge to `main` with `--no-ff`, update `docs/build/PROGRESS.md` (status, model, cost, notes) and any architecture doc the change affects, update `NOTICE.md` for ported code, commit with a Conventional Commit message, and **push to `origin main` after every merged task** so progress is never lost.
5. Remove the worktree; keep `.worker/<id>/` reports until the phase closes.

**Rules that override convenience:** everything in `CLAUDE.md` "Non-negotiables"; never merge red; workers never touch `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; no secrets or AI identifiers in commits; keep per-run caps from `.env`; stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the Phase 0 budget of USD <set-me>, or (c) a blocker outside the repo (missing tool, failing external service). Everything else: decide, note it in `PROGRESS.md`, and continue.

**Working style:** brief status line at the start of each task and a one-paragraph summary after each merge; no narration in between. Windows: use PowerShell-compatible commands; keep worktrees under `.worktrees/`.

**Definition of done for this session:** the Phase 0 gate passes (container starts on port 1077, UI loads behind a URL base, API key works, health shows DB/folders/bundled slskd OK, a scheduled no-op job survives a restart, CI green on `main`, a `v0.0.1-alpha.1` tag builds a multi-arch image on GHCR), `PROGRESS.md` is complete for Phase 0 with costs, and `docs/HANDOVER.md` has a dated "Session log" entry describing what was built, what was learned about worker quality, and the first three tasks of Phase 1 to spec next. If you run out of time or budget before the gate, do the handover entry anyway and push.

Begin with Step 1.
