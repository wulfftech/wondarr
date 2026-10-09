# Agentic build workflow: Opus 5.5 orchestrator + subagents

> **Since 2026-10-09 (owner; `docs/DECISIONS.md` build session 10 #1, ADR-0011 amended): OpenRouter is paused.** Workers and reviewers are Claude Code subagents started with the Agent tool (§2.0). `scripts/worker.py` (`run`, `api`, `review`) and `/openrouter-value` are not used while the pause lasts; §2.1–2.2 and §6 describe them for when it ends.

## 1. Roles

| Role | Runs where | Model | Does | Never does |
|---|---|---|---|---|
| **Orchestrator** | The interactive Claude Code session in this repo | **Claude Opus 5.5** (`/model claude-opus-5-5`) | Reads the docs, picks tasks, writes task specs, launches workers, reviews every diff, runs verification, resolves conflicts, updates `PROGRESS.md`/docs, commits and pushes | Hand-types large volumes of routine code; skips review |
| **Worker** | A Claude Code subagent (Agent tool) in its own git worktree (`isolation: worktree`), in the background | `sonnet` for implementation; `haiku` for mechanical work (porting one file, boilerplate tests, doc drafts, fixtures); `Explore` on `haiku` for read-only searches | Implements exactly one task file: code + tests, runs build/tests, commits on its branch, writes a done-report | Makes design decisions, edits docs/decisions, touches files outside the task's allowed paths, merges, pushes or opens a PR |
| **Reviewer** | A `general-purpose` subagent, no worktree | `sonnet` | Adversarial review of `git diff main...<branch>` against the spec and `CODING_STANDARDS.md`; findings ranked by severity with file and line. Required for anything touching processes, files, time, the database, auth or external limits | Edits code |
| **Researcher** (optional) | `.claude/agents/researcher.md` | Sonnet/Haiku with web tools | Verifies an external API/library fact before a spec is written | Writes code |

Why separate processes: Claude Code subagents inherit the session's API endpoint and cannot be routed to a different provider per agent ([sub-agents docs](https://code.claude.com/docs/en/sub-agents.md), [env vars](https://code.claude.com/docs/en/env-vars.md)). So the orchestrator stays on Anthropic (Claude Code), and each worker is a separate process, `scripts/worker.py run`, that calls OpenRouter's chat-completions API directly with six sandboxed tools (`read_file`, `write_file`, `edit_file`, `list_files`, `grep`, `run`), with its own model, budget and sandbox. The worker, reviewer and single-shot modes need only `OPENROUTER_API_KEY`; the orchestrator needs only your Claude login. Model choice for every *delegated* call is by effort tier from `MODEL_VALUE_MATRIX.md` (refresh it with the `/openrouter-value` command); the matrix's "Orchestrator" row is informational (what a cheaper orchestrator would cost), not a setting.

## 2. Mechanics

### 2.0 Claude Code subagents (current)

1. Write the spec `docs/build/tasks/<id>.md` from `WORKER_TASK_TEMPLATE.md` and **commit it on `main`** (the worktree is created from `HEAD`).
2. Start the subagent with the Agent tool: `subagent_type: general-purpose`, `model: sonnet`, `isolation: worktree`, `run_in_background: true` (a `haiku` model for mechanical work). It starts cold, so its prompt is the spec plus: the branch to create (`phase<n>/<id>-<slug>`), the allowed paths, "commit your work on that branch with Conventional Commits, no model names, no AI trailer; do not touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; do not merge, push or open a PR", the exact commands that must pass (`dotnet build -warnaserror`, the relevant `dotnet test` projects; for frontend work `npm run lint && npm run typecheck && npm run format && npm test && npm run build`), and "finish with a done-report: what you changed, what you ran and its result, what you did not finish".
3. When it reports, run the builds and tests yourself in its worktree; for risky areas start a review subagent (`sonnet`, no worktree) on `git diff main...<branch>`; verify every finding before acting on it and record the rejected ones with the reason in `PROGRESS.md`.
4. Fix the findings yourself when that is cheaper than a continuation (`SendMessage` to the same subagent; at most two), then merge only through `scripts/safe-merge.sh <branch>`, regenerate `docs/api/openapi.json` (`WONDARR_UPDATE_OPENAPI=1` on the snapshot test) and the frontend types (`npm run gen:api`) after merging the base into the branch — subagents do not.
5. One task per subagent; at most three at once; never a merge while a subagent runs its tests (memory is the limit on the dev PC).

### 2.1 Tool-calling worker on OpenRouter (paused since 2026-10-09)

`scripts/worker.py [--model <id>] run docs/build/tasks/<id>.md` does, for one task file:

1. Loads `.env` (`OPENROUTER_API_KEY`, `WONDARR_WORKER_MODEL`, caps).
2. Creates or reuses a git worktree `.worktrees/<id>` on branch `phase<n>/<id>-<slug>` from the base branch (`WONDARR_WORKER_BASE`, default `main`).
3. Runs the agent loop (`scripts/worker_agent.py`) with `docs/build/WORKER_SYSTEM_PROMPT.md` as the system prompt and a short kickoff message. Each turn is one chat-completions call with the tool schema; the model's tool calls are executed in the sandbox and their output returned. The loop ends when the model replies without tool calls (its text is the done-report), or at a cap.
   - **Sandbox (enforced in code):** paths must resolve inside the worktree (reads may also use `.worker/ref`); `.env*` is never readable; protected paths (`AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`, `LICENSE`) and `.git` are never writable; `run` allows only `dotnet`, `npm`, `npx`, `node`, local `git` (`status`, `diff`, `log`, `show`, `add`, `rm`, `mv`, `commit`, `update-index --chmod`), `sh -n` and the builtins `cd`, `pwd`, `ls`, `mkdir`, `echo`, chained with `&&` only (no pipes, redirects, `;` or subshells; no shell is invoked); child processes do not inherit `OPENROUTER_API_KEY`.
   - **Caps:** `WONDARR_WORKER_MAX_TURNS` (on the last turn the model is told to stop and write a PARTIAL done-report), `WONDARR_WORKER_BUDGET_USD` (real USD, from OpenRouter's per-response `usage.cost`, falling back to token usage x the model's price list), `WONDARR_WORKER_TIMEOUT_MIN`, `WONDARR_WORKER_CMD_TIMEOUT_S` per build/test command, `WONDARR_WORKER_MAX_TOKENS` per response. Old tool output is trimmed when the context grows large.
4. Writes `.worker/<id>/transcript[.n].json` (full message log) and the done-report to `.worker/<id>/report[.n].md`, appends a line per run to `.worker/<id>/runs.jsonl` (model, stop reason, turns, tokens, real cost, key-usage delta), and prints a summary, any uncommitted leftovers, and a warning if a protected path was touched. Exit code: 0 done, 3 out of turns, 4 budget, 5 API error, 124 timeout.
5. Progress streams live: each worker appends one readable line per step (what it says, every tool call, tool errors) to `.worker/<id>/live.log`. Follow all running workers from any terminal with `python scripts/worker.py watch` (`--replay` to print what is already there).
6. The task file must be **committed on the base branch** before the run: the worktree is created from it, so an uncommitted spec is invisible to the worker.

The model must support tool calling (the tier picks in `MODEL_VALUE_MATRIX.md` already filter for it). `python scripts/test_worker_agent.py` runs the offline tests (sandbox rules and the loop against a scripted fake endpoint); `WONDARR_OPENROUTER_BASE_URL` points the runner at such a fake.

The orchestrator then reviews with `git -C .worktrees/<id> diff <base>...HEAD`, runs the verification commands itself, asks the worker to fix (`worker.py run … --continue "fix: …"`) or merges (`git merge --no-ff phase<n>/<id>-…`), and removes the worktree.

### 2.2 Single-shot API worker (paused since 2026-10-09)

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
