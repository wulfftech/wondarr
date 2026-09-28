# Next-session prompt (Phase 1 kickoff)

Launch Claude Code in `D:\Code\compilarr` on the orchestrator model (`/model claude-opus-5-5`), make sure the `claude-terminals` MCP server is connected (`/mcp`), then paste the prompt below as the first message. (The Phase 0 kickoff prompt is in this file's git history.)

---

You are the **orchestrator** for Compilarr (Windows, `D:\Code\compilarr`). Read first: `CLAUDE.md`, `docs/HANDOVER.md` (especially §7 Session log), `docs/build/PROGRESS.md`, `docs/build/AGENT_WORKFLOW.md`, `docs/build/PHASES.md` (Phase 1), `docs/build/CODING_STANDARDS.md`; skim `docs/DECISIONS.md` and consult `docs/architecture/*.md` per task. They are binding.

**Goal:** complete **Phase 1 — song identity and the Wanted list** to its "done when" gate in `PHASES.md`, with cheap workers doing the implementation, and leave the repo ready for Phase 2.

**Pre-flight (one short report):** toolchain as in Phase 0; `.env` has `OPENROUTER_API_KEY`, `COMPILARR_WORKER_MODEL`, `ACOUSTID_CLIENT_KEY`, `PLEX_TOKEN`, `SOULSEEK_USERNAME/PASSWORD` set (check presence only, never print values); CI green on `main`; the repo is public — check whether the GHCR package is public too and tell me if it is not; `worker.py --dry-run` shows 50 turns / 30 min.

**Plan:** write `docs/build/PHASE_1_TASKS.md` (dependency table like Phase 0, tasks sized ≤ ~10 files), starting from the three tasks in HANDOVER §7 (P1-01 domain model + quality seeds, P1-02 MusicBrainz client, P1-03 version-flag parser). Use the researcher agent to verify MusicBrainz/Cover Art Archive/Deezer/iTunes API facts before specifying them. Show me the task table before delegating.

**Per task:** spec from `WORKER_TASK_TEMPLATE.md` with exact package versions and any API signatures that changed recently → commit the spec → launch the worker in its own **VS Code terminal tab** via `claude-terminals` (`python -u scripts/worker.py run docs/build/tasks/<id>.md`; completion via `.worker/<id>/runs.jsonl`) → review the diff adversarially, run `dotnet build -warnaserror`, `dotnet test` and the frontend checks yourself → loop any timing-sensitive tests several times → reviewer agent for concurrency, caches, auth, file moves or external rate limits → fix via `--continue` (max two rounds) or finish it yourself → merge `--no-ff`, update `PROGRESS.md` (status, cost from the key counter, notes), docs, `NOTICE.md` (`python scripts/check-notice.py --fix`), OpenAPI snapshot (`COMPILARR_UPDATE_OPENAPI=1`) → commit (Conventional Commits, no model names) → push. Run independent tasks in parallel tabs.

**Verification beyond unit tests:** run the real app (and a browser for UI work) for anything user-facing; extend `scripts/smoke-test.sh` with the Phase 1 gate checks so CI proves the gate; live MusicBrainz calls only in opt-in tests (`COMPILARR_LIVE_TESTS=1`) and within 1 req/s.

**Rules:** everything in `CLAUDE.md` "Non-negotiables"; never merge red; workers never touch `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; no secrets or AI identifiers in commits. Phase 1 worker budget: **USD 10** (Phase 0 used 1.12). Stop and ask me only for (a) a decision that would change an ADR, (b) spend beyond the budget, (c) a blocker outside the repo. Otherwise decide, record it in `PROGRESS.md`/`DECISIONS.md`, and continue.

**Style:** a status line when you start each task, one paragraph after each merge, no narration in between.

**Done for this session:** the Phase 1 gate passes (and is automated in CI), `PROGRESS.md` has a complete Phase 1 section with costs, and `docs/HANDOVER.md` gets a dated Session log entry: what was built, worker lessons, and the first three Phase 2 tasks to spec (including validating the Soulseek account on `ch01`, see HANDOVER §7). If you run out of time or budget first, write the handover entry anyway and push.
