# Agentic build workflow: value-picked orchestrator + cheap workers

## 1. Roles

| Role | Runs where | Model | Does | Never does |
|---|---|---|---|---|
| **Orchestrator** | The interactive agent session (VS Code harness) in this repo | **An OpenRouter model** picked from `MODEL_VALUE_MATRIX.md` ("Orchestrator" row), run in the VS Code harness | Reads the docs, picks tasks, writes task specs, launches workers, reviews every diff, runs verification, resolves conflicts, updates `PROGRESS.md`/docs, commits and pushes | Hand-types large volumes of routine code; skips review |
| **Worker** | A separate headless process (`scripts/worker.py`) in its own git worktree | A **cheap model via OpenRouter** (`WONDARR_WORKER_MODEL`), or Claude Haiku via `.claude/agents/worker.md` when OpenRouter is not configured | Implements exactly one task file: code + tests, runs build/tests, writes a done-report | Makes design decisions, edits docs/decisions, touches files outside the task's allowed paths, commits to `main` |
| **Reviewer** (optional) | `.claude/agents/reviewer.md` subagent or `scripts/worker.py review` | Mid-tier model (`WONDARR_REVIEWER_MODEL`) or Sonnet | Independent review of a worker diff against the task's acceptance criteria and `CODING_STANDARDS.md` | Edits code |
| **Researcher** (optional) | `.claude/agents/researcher.md` | Sonnet/Haiku with web tools | Verifies an external API/library fact before a spec is written | Writes code |

Why separate processes: Claude Code subagents inherit the session's API endpoint and cannot be routed to a different provider per agent ([sub-agents docs](https://code.claude.com/docs/en/sub-agents.md), [env vars](https://code.claude.com/docs/en/env-vars.md)). So the orchestrator runs in the VS Code harness on an OpenRouter model, and each worker is a separate process (`scripts/worker.py`, or a direct API call) launched with its own OpenRouter model in its environment. Model choice for every delegated call is by effort tier from `MODEL_VALUE_MATRIX.md`.

## 2. Mechanics

### 2.1 Headless Claude Code worker on OpenRouter (default)

`scripts/worker.py run docs/build/tasks/<id>.md` does, for one task file:

1. Loads `.env` (`OPENROUTER_API_KEY`, `WONDARR_WORKER_MODEL`, caps).
2. Creates or reuses a git worktree `.worktrees/<id>` on branch `phase<n>/<id>-<slug>` from `main`.
3. Runs `claude -p` in that worktree with **worker-only environment**:
   - `ANTHROPIC_BASE_URL=https://openrouter.ai/api` (OpenRouter's Anthropic-compatible "skin"; Claude Code's own env-vars page shows `https://openrouter.ai/api/v1` — the script defaults to the former and `OPENROUTER_ANTHROPIC_BASE_URL` overrides it if you see 404s),
   - `ANTHROPIC_API_KEY=$OPENROUTER_API_KEY` (and `ANTHROPIC_AUTH_TOKEN` cleared),
   - `ANTHROPIC_MODEL=$WONDARR_WORKER_MODEL`, plus `ANTHROPIC_DEFAULT_{HAIKU,SONNET,OPUS}_MODEL` and `CLAUDE_CODE_SUBAGENT_MODEL` set to the same id so nothing inside the worker escapes to an expensive model,
   - flags: `--output-format json --max-turns $WONDARR_WORKER_MAX_TURNS --max-budget-usd <scaled cap> --permission-mode acceptEdits --allowedTools <WORKER_TOOLS> --disallowedTools "Read(<repo>/.env*)" --append-system-prompt-file docs/build/WORKER_SYSTEM_PROMPT.md --no-session-persistence`. `WORKER_TOOLS` in the script is Read/Edit/Write/Grep/Glob plus `dotnet`, `npm`/`npx`/`node`, harmless shell helpers (`cd`, `ls`, `mkdir`, `echo`, `pwd`) and local git; Bash rules match per sub-command, and every denied call still costs a turn.
   - **Cost:** Claude Code prices model ids it does not know at Opus rates, so its own `total_cost_usd` is inflated ~30× for flash models. The script fetches the model's OpenRouter prices, scales `--max-budget-usd` so that `WONDARR_WORKER_BUDGET_USD` caps *real* spend, and computes the real cost from the token usage afterwards.
4. Captures the JSON result to `.worker/<id>/stdout[.n].json`, extracts the done-report to `.worker/<id>/report[.n].md`, appends a line per run to `.worker/<id>/runs.jsonl` (model, turns, real cost, key-usage delta), and prints a summary. `--id <name>` overrides the task id so one spec can run in several worktrees (bake-offs).
5. Progress streams live (`--output-format stream-json`): each worker appends one readable line per step — what it says, every tool call, tool errors — to `.worker/<id>/live.log`. Follow all running workers from any terminal (e.g. VS Code's) with `python scripts/worker.py watch` (`--replay` to print what is already there).
6. The task file must be **committed on `main`** before the run: the worktree is created from `main`, so an uncommitted spec is invisible to the worker.

The orchestrator then reviews with `git -C .worktrees/<id> diff main...HEAD`, runs the verification commands itself, asks the worker to fix (`worker.py run … --continue "fix: …"`) or merges (`git merge --no-ff phase<n>/<id>-…`), and removes the worktree.

### 2.2 Claude-native worker (fallback)

`.claude/agents/worker.md` is a Haiku subagent with `isolation: worktree`, the same system prompt and a turn cap. Use it when `OPENROUTER_API_KEY` is absent or OpenRouter is down. Cost is higher than a cheap open-weight model but far below Opus.

### 2.3 Single-shot API worker (cheapest)

`scripts/worker.py api docs/build/tasks/<id>.md --files a.cs b.cs` sends the task plus the named files to OpenRouter's chat-completions API with no tools and writes the answer to `.worker/<id>/response.md`. Use for pure text transforms: port this class, write tests for this file, draft this doc section. The orchestrator applies the result.

## 3. Task specs

Every delegated task is a file `docs/build/tasks/<id>.md` created from `docs/build/WORKER_TASK_TEMPLATE.md`. A good spec is self-contained: goal, exact deliverables, allowed paths, constraints, acceptance criteria with commands, pointers to the relevant docs sections and fixtures, and the done-report format. Tasks come from `docs/build/PHASE_<n>_TASKS.md`; if a task is too large for one worker run (rule of thumb: > 8 files or > 400 lines of diff), split it first.

## 4. The orchestrator loop (per task)

1. Read the task; verify its inputs exist (fixtures, docs); write the spec file.
2. Launch the worker (§2.1). While it runs, prepare the next spec or review another worker.
3. Review the diff adversarially: correctness against the spec, `CODING_STANDARDS.md`, attribution headers for ported code, no secrets, no scope creep, tests meaningful.
4. Run verification yourself (build, tests, lint). Do not trust the worker's report alone.
5. If fixes are needed, send a precise fix list to the same worker (bounded to 2 rounds; then take over or re-spec).
6. Merge to `main` with `--no-ff`, update `docs/build/PROGRESS.md` (status, cost, notes), update docs touched by the change, commit with a Conventional Commit message.
7. Remove the worktree; keep `.worker/<id>/` reports until the phase closes.

## 5. Cost controls

- Worker runs are capped by turns, budget and time (`.env`). Typical Phase 0 task: 10–25 turns.
- Give workers **paths and line ranges**, not pasted files; the worker reads what it needs.
- Prefer many small tasks over one big one; parallelise independent tasks in separate worktrees.
- Use the single-shot API mode for text transforms.
- Use OpenRouter features that cut cost: provider routing by price, prompt caching where the model supports it, and (for offline batches such as generating fixture cases) the batch endpoint at ~50 % price.
- Track spend per task in `PROGRESS.md`; the phase budget is agreed with the owner up front.

## 6. Choosing the worker model (bake-off)

OpenRouter model availability and prices change monthly, so the model id is configuration, not code. Before Phase 0 starts, run the bake-off:

1. Pick three candidates from `openrouter.ai/models` filtered by tool-use support and price. At the time of writing (Sept 2026) OpenRouter's own rankings list **Z.ai GLM 5.3 Flash** and **DeepSeek V4.1 Flash** as the most-used low-cost models (input ≤ $0.84/M tokens for the whole top ten); Claude Haiku 4.5 is the Anthropic-native cheap option. Verify each candidate supports tool calling and ≥ 128k context.
2. Run the same three tasks with each (`P0-02`, `P0-05`, one golden-test task), same specs, same caps.
3. Score: acceptance criteria met without fixes / with one fix round / failed; wall time; cost.
4. Pin the winner in `.env` (`WONDARR_WORKER_MODEL`) and record the result in `docs/DECISIONS.md`. Re-run the bake-off when a phase starts failing on worker quality.

## 7. Quality gates (non-negotiable)

- No merge without green build, tests, lint and typecheck run by the orchestrator.
- Ported code has attribution headers and a `NOTICE.md` entry; AGPL code is never copied.
- Workers cannot modify `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, or `.env*`; the worker system prompt says so and the orchestrator checks the diff for it.
- Secrets never appear in diffs, reports or commit messages.
- Every task's done-report is kept until the phase closes; disagreements between report and diff are resolved by the diff.

## 8. Hooks (optional, enable after Phase 0 creates the solution)

Claude Code hooks can run the build automatically after edits in the orchestrator session. Example for `.claude/settings.json` (async so it does not block; adjust for PowerShell on Windows):

```json
{
  "hooks": {
    "PostToolUse": [
      { "matcher": "Write|Edit", "hooks": [ { "type": "command", "command": "dotnet", "args": ["build", "-warnaserror", "--nologo"], "timeout": 300, "async": true } ] }
    ]
  }
}
```

Useful events for this workflow: `PostToolUse` (build/lint after edits), `SubagentStop` (collect a subagent's result), `Stop` (final test run), `WorktreeCreate`/`WorktreeRemove` (bootstrap/cleanup). See the hooks reference in the Claude Code docs.

## 9. Windows notes (the next session runs from `D:\Code\wondarr`)

- `scripts/worker.py` is cross-platform (Python 3.10+, standard library only); run it from PowerShell or Git Bash.
- The `claude` CLI must be on `PATH`; the script also tries `claude.cmd`.
- Git worktrees work on Windows; keep paths short (`.worktrees/<id>`), and exclude `.worktrees/` from Windows Defender scanning for speed.
- Docker Desktop with WSL 2 for the container build; .NET 10 SDK; Node 22+.
