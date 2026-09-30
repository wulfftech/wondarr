# Next-session prompt (finish Phase 3)

Launch Claude Code in `D:\Code\wondarr` on the orchestrator model (`/model claude-opus-5-5`), then paste the prompt below as the first message. (Earlier kickoff prompts are in this file's git history.)

---

You are the **orchestrator** for Wondarr (Windows, `D:\Code\wondarr`). Read first: `CLAUDE.md`, `docs/HANDOVER.md` (especially §7 Session log, the build-session-4 entry), `docs/build/PROGRESS.md` (Phase 3), `docs/build/PHASE_3_TASKS.md`, `docs/build/AGENT_WORKFLOW.md`, `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` (build session 4) and consult `docs/architecture/*.md` per task. They are binding.

**Goal:** finish **Phase 3** — P3-12a (Settings → Plex; the spec is committed), P3-09a/b (Compact library), P3-10a/b (notifications, ported from Lidarr with attribution), P3-12b (their UI) — and the live 500-file check, then leave the repo ready for Phase 4.

**Pre-flight (one short report):** toolchain; `.env` presence (never print values; read them with `worker.py`'s loader); CI green on `main` (Phase 0–3 gates); the OpenRouter key's remaining limit (it showed USD 10 with USD 9.77 used on 2026-09-30 — tell me what it shows now and whether it covers the remaining ≈ USD 1.2); the live instance on `ch01` (http://ch01.ad.wulff.com.au:1077, now on Phase 3) is up.

**Per task:** as in build session 4 — spec from `WORKER_TASK_TEMPLATE.md` → commit the spec → `WONDARR_WORKER_BASE=<your base branch> python -u scripts/worker.py run docs/build/tasks/<id>.md` in the background → review adversarially and run the builds, tests and frontend checks yourself → the reviewer agent for anything touching processes, files, time, the database, auth or external limits → fix the findings yourself when that is cheaper than a continuation → merge only through `scripts/safe-merge.sh` → update `PROGRESS.md`, docs, `NOTICE.md`, the OpenAPI snapshot and the frontend types (regenerate them *after* merging the base into a branch) → Conventional Commit, no model names, no AI trailer → push to `main`.

**Verification beyond unit tests:** a live check on `ch01` before calling anything done (the first live run of build session 4 found a lyrics bug that broke FLAC imports); the Compact library task needs a throwaway Plex Media Server on `ch01` (unclaimed, `ALLOWED_NETWORKS` set — see `PROGRESS.md` "Phase 3 gate") to prove the move-out / scan / empty-trash / move-back sequence; run the real app and a browser for UI work.

**Rules:** everything in `CLAUDE.md` "Non-negotiables"; never merge red; workers never touch `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`. Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** every Phase 3 task merged, the gate's live 500-file check run against the folder I provide, `PROGRESS.md`'s Phase 3 section complete with costs, and a dated `docs/HANDOVER.md` Session log entry with the first three Phase 4 tasks (already listed in the build-session-4 entry — refine them). If you run out of time or budget first, write the handover entry anyway and push.
