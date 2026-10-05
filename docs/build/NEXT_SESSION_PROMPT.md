# Next-session prompt (Phase 4)

Run this in the VS Code harness from `D:\Code\wondarr` on an OpenRouter model (the "Orchestrator" row of `docs/build/MODEL_VALUE_MATRIX.md`), then paste the prompt below as the first message. (Earlier kickoff prompts are in this file's git history.)

---

You are the **orchestrator** for Wondarr (Windows, `D:\Code\wondarr`). Read first: `AGENTS.md`, `docs/HANDOVER.md` (especially §7 Session log, the two build-session-4 entries), `docs/build/PROGRESS.md` (Phase 3), `docs/build/PHASES.md` (Phase 4), `docs/build/AGENT_WORKFLOW.md`, `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` (build session 4) and consult `docs/architecture/*.md`, `docs/research/research_youtube.md` per task. They are binding.

**Goal:** Phase 4 — the YouTube source — to its "done when" gate: songs missing on Soulseek filled from Art Tracks within one search cycle and passing fingerprint verification; official-video candidates with intros rejected by duration; a simulated bot-check response backs off instead of looping. Start by writing `docs/build/PHASE_4_TASKS.md` (P4-00 research first — see the handover's Phase 4 list).

**Pre-flight (one short report):** toolchain; the orchestrator model is the matrix's current "Orchestrator" pick (if the matrix is over 7 days old, refresh it first); `.env` presence (never print values; read them with `worker.py`'s loader); CI green on `main`; the OpenRouter key's remaining limit (USD 8.93 left of 20 on 2026-09-30; Phase 3 cost USD 3.45); the live instance on `ch01` (http://ch01.ad.wulff.com.au:1077, `develop` image) is up.

**Per task:** spec from `WORKER_TASK_TEMPLATE.md` (state its tier) → commit the spec → `WONDARR_WORKER_BASE=<your base branch> python -u scripts/worker.py --model <tier pick> run docs/build/tasks/<id>.md` in the background **with a background timeout above `WONDARR_WORKER_TIMEOUT_MIN`** → review adversarially and run the builds, tests and frontend checks yourself → the reviewer (T3 pick) for anything touching processes, files, time, the database, auth or external limits → fix the findings yourself when that is cheaper than a continuation → merge only through `scripts/safe-merge.sh` → update `PROGRESS.md`, docs, `NOTICE.md`, the OpenAPI snapshot and the frontend types (regenerate them *after* merging the base into a branch; regenerate a branch's migration when another migration merged first) → Conventional Commit, no model names, no AI trailer → push to `main`.

**Verification beyond unit tests:** YouTube is rate-limited and bot-checked: live calls only in opt-in runs and well within YouTube's tolerance (concurrency 1, pacing), recorded fixtures everywhere else. A live check on `ch01` before calling anything done. The throwaway-Plex recipe (unclaimed server, `ALLOWED_NETWORKS`, a stand-in token in the `plex` setting) is in `PROGRESS.md` "Phase 3 gate".

**Rules:** everything in `AGENTS.md` "Non-negotiables" (never write `.webm`, never produce lossless from lossy, a transcoded YouTube file keeps quality `OPUS-160`); never merge red; workers never touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`. Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Model selection for every delegated call (workers, reviewers, researchers, API single-shots):** do not default to the `.env` pin. Choose per task by effort tier from `docs/build/MODEL_VALUE_MATRIX.md` ("Tier picks"). Match the tier to the task spec, take the matrix's cheapest model that clears that tier's minimum scores, and pass it with `python scripts/worker.py --model <id> run|api|review ...`:

- **T1 mechanical** (port or transform a file, boilerplate tests, doc drafts) → `worker.py api`, the T1 pick.
- **T2 standard** (ordinary feature or test work in a worktree) → T2 pick.
- **T3 hard** (touches processes, files, time, the database, auth or external limits) and **every reviewer run** → T3 pick.
- Escalate one tier after a failed continuation (bounded to 2 rounds, as in `AGENT_WORKFLOW.md`) instead of retrying the same model; take over yourself after T3 fails.
- Use the **"Cheapest at list price"** column for anything that must keep working for the whole session (a promo can expire mid-run); the "now" column is fine for short one-off tasks. If the matrix is more than 7 days old or a model id is rejected, run `python scripts/openrouter_value.py --out docs/build/MODEL_VALUE_MATRIX.md`, then re-pick (needs `openrouter.ai` reachable).
- Log the tier and model id in each task's `PROGRESS.md` line with the real cost (`.worker/<id>/runs.jsonl`), so the next session can see whether a tier's pick is under-performing and move its floor up.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** Phase 4's tasks merged and its gate demonstrated (CI with fixtures, live on `ch01`), `PROGRESS.md`'s Phase 4 section with costs, and a dated `docs/HANDOVER.md` Session log entry with the first three Phase 5 tasks. If you run out of time or budget first, write the handover entry anyway and push.
