# ADR-0011: Agentic build workflow: Opus 5.5 orchestrator, cheap OpenRouter workers, review gates

**Status:** Accepted, amended 2026-10-05 (orchestrator model; see Amendment) · **Date:** 2026-09-28 · **Deciders:** owner

## Context
The owner wants the build done by AI sessions with an Opus 5.5 orchestrator and cheap worker subagents using an OpenRouter key kept in `.env`. Claude Code subagents inherit the session's API endpoint and cannot be routed per agent to another provider; Claude Code can run against OpenRouter's Anthropic-compatible endpoint via `ANTHROPIC_BASE_URL`/`ANTHROPIC_API_KEY`/`ANTHROPIC_MODEL`; headless `claude -p` supports turn, budget, tool and permission caps (`docs/build/AGENT_WORKFLOW.md` §1–2).

## Decision
The interactive session (Opus 5.5) orchestrates: specs, delegation, review, verification, docs, commits. Implementation runs in separate headless `claude -p` processes launched by `scripts/worker.py` with OpenRouter credentials and a pinned cheap model (`WONDARR_WORKER_MODEL`, chosen by a bake-off) in isolated git worktrees; fallbacks are a Haiku subagent (`.claude/agents/worker.md`) and single-shot OpenRouter API calls for text transforms. Workers get self-contained task files, cannot edit decisions/ADRs/CLAUDE.md/.claude/workflows/.env, and must produce a done-report. Nothing merges without the orchestrator's review and green build/tests; risky areas also get the reviewer agent. Costs are capped per run and tracked in `PROGRESS.md`.

## Consequences
- Opus tokens go to judgement, not boilerplate; worker quality is measured and the model is re-selected when it slips.
- Task specs and fixtures become first-class artefacts; the repo must stay navigable for small models (clear docs, small files).

## Amendment (2026-10-05, owner)
The orchestrator is no longer tied to Anthropic or to Opus 5.5. It is any tool-calling model on any provider, chosen from the "Orchestrator" row of `docs/build/MODEL_VALUE_MATRIX.md` (value per dollar from live OpenRouter prices) and run in the VS Code harness (OpenRouter models only). All delegated calls are routed by effort tier from the same matrix. The rest of this decision (cheap isolated workers, review gates, caps) is unchanged.

## References
`docs/build/AGENT_WORKFLOW.md` · `scripts/worker.py` · `.claude/agents/`
