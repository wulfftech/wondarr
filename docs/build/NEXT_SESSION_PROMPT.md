# Next-session prompt (Phase 4)

Launch Claude Code in `D:\Code\wondarr` on the orchestrator model (`/model claude-opus-5-5`), then paste the prompt below as the first message. (Earlier kickoff prompts are in this file's git history.)

---

You are the **orchestrator** for Wondarr (Windows, `D:\Code\wondarr`). Read first: `AGENTS.md`, `docs/HANDOVER.md` (especially §7 Session log, the two build-session-4 entries), `docs/build/PROGRESS.md` (Phase 3), `docs/build/PHASES.md` (Phase 4), `docs/build/AGENT_WORKFLOW.md`, `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` (build session 4) and consult `docs/architecture/*.md`, `docs/research/research_youtube.md` per task. They are binding.

**Goal:** Phase 4 — the YouTube source — to its "done when" gate: songs missing on Soulseek filled from Art Tracks within one search cycle and passing fingerprint verification; official-video candidates with intros rejected by duration; a simulated bot-check response backs off instead of looping. Start by writing `docs/build/PHASE_4_TASKS.md` (P4-00 research first — see the handover's Phase 4 list).

**Pre-flight (one short report):** toolchain; `.env` presence (never print values; read them with `worker.py`'s loader); CI green on `main`; the OpenRouter key's remaining limit (USD 8.93 left of 20 on 2026-09-30; Phase 3 cost USD 3.45); the live instance on `ch01` (http://ch01.ad.wulff.com.au:1077, `develop` image) is up.

**Per task:** spec from `WORKER_TASK_TEMPLATE.md` → commit the spec → `WONDARR_WORKER_BASE=<your base branch> python -u scripts/worker.py run docs/build/tasks/<id>.md` in the background **with a background timeout above the worker's 60-minute cap** → review adversarially and run the builds, tests and frontend checks yourself → the reviewer agent for anything touching processes, files, time, the database, auth or external limits → fix the findings yourself when that is cheaper than a continuation → merge only through `scripts/safe-merge.sh` → update `PROGRESS.md`, docs, `NOTICE.md`, the OpenAPI snapshot and the frontend types (regenerate them *after* merging the base into a branch; regenerate a branch's migration when another migration merged first) → Conventional Commit, no model names, no AI trailer → push to `main`.

**Verification beyond unit tests:** YouTube is rate-limited and bot-checked: live calls only in opt-in runs and well within YouTube's tolerance (concurrency 1, pacing), recorded fixtures everywhere else. A live check on `ch01` before calling anything done. The throwaway-Plex recipe (unclaimed server, `ALLOWED_NETWORKS`, a stand-in token in the `plex` setting) is in `PROGRESS.md` "Phase 3 gate".

**Rules:** everything in `AGENTS.md` "Non-negotiables" (never write `.webm`, never produce lossless from lossy, a transcoded YouTube file keeps quality `OPUS-160`); never merge red; workers never touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`. Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** Phase 4's tasks merged and its gate demonstrated (CI with fixtures, live on `ch01`), `PROGRESS.md`'s Phase 4 section with costs, and a dated `docs/HANDOVER.md` Session log entry with the first three Phase 5 tasks. If you run out of time or budget first, write the handover entry anyway and push.
