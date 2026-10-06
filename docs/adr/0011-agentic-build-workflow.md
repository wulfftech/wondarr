# ADR-0011: Agentic build workflow: Opus 5.5 orchestrator, cheap OpenRouter workers, review gates

**Status:** Accepted, amended 2026-10-05 (workers) and 2026-10-06 (orchestrator reverted; see Amendments) · **Date:** 2026-09-28 · **Deciders:** owner

## Context
The owner wants the build done by AI sessions with an Opus 5.5 orchestrator and cheap worker subagents using an OpenRouter key kept in `.env`. Claude Code subagents inherit the session's API endpoint and cannot be routed per agent to another provider; Claude Code can run against OpenRouter's Anthropic-compatible endpoint via `ANTHROPIC_BASE_URL`/`ANTHROPIC_API_KEY`/`ANTHROPIC_MODEL`; headless `claude -p` supports turn, budget, tool and permission caps (`docs/build/AGENT_WORKFLOW.md` §1–2).

## Decision
The interactive session (Opus 5.5) orchestrates: specs, delegation, review, verification, docs, commits. Implementation runs in separate headless `claude -p` processes launched by `scripts/worker.py` with OpenRouter credentials and a pinned cheap model (`WONDARR_WORKER_MODEL`, chosen by a bake-off) in isolated git worktrees; fallbacks are a Haiku subagent (`.claude/agents/worker.md`) and single-shot OpenRouter API calls for text transforms. Workers get self-contained task files, cannot edit decisions/ADRs/CLAUDE.md/.claude/workflows/.env, and must produce a done-report. Nothing merges without the orchestrator's review and green build/tests; risky areas also get the reviewer agent. Costs are capped per run and tracked in `PROGRESS.md`.

## Consequences
- Opus tokens go to judgement, not boilerplate; worker quality is measured and the model is re-selected when it slips.
- Task specs and fixtures become first-class artefacts; the repo must stay navigable for small models (clear docs, small files).

## Amendments
- **2026-10-05 (owner), workers:** workers no longer run as headless `claude -p` processes. `scripts/worker.py run` drives a sandboxed tool-calling loop (`scripts/worker_agent.py`) directly against the OpenRouter API; the Haiku/Sonnet worker and reviewer subagent definitions (`.claude/agents/worker.md`, `reviewer.md`) are replaced by `worker.py run|review`. Every delegated call is routed by effort tier from `docs/build/MODEL_VALUE_MATRIX.md` (value per dollar from live OpenRouter prices; refresh with `scripts/openrouter_value.py`). Cheap isolated workers, review gates and caps are unchanged.
- **2026-10-06 (owner), orchestrator:** an interim change of 2026-10-05 (orchestrator on any OpenRouter model in another harness) is reversed. The orchestrator is Claude Code on Claude Opus 5.5 (`/model claude-opus-5-5`), as originally decided above. The matrix's "Orchestrator" row stays as information only.

## References
`docs/build/AGENT_WORKFLOW.md` · `scripts/worker.py` · `.claude/agents/`
