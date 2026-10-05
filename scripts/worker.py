#!/usr/bin/env python3
"""
Wondarr cheap-worker runner (standard library only, Python 3.10+, Windows/macOS/Linux).

Modes
  run   <task.md>   Run a tool-calling worker (direct OpenRouter API, sandboxed tools) in a git worktree.
  api   <task.md>   Single-shot OpenRouter chat-completions call (no tools); optional --files to inline.
  review <branch>   Ask the reviewer model to review `git diff main...<branch>` (single-shot, no tools).

Examples
  python scripts/worker.py run docs/build/tasks/P0-01.md
  python scripts/worker.py --dry-run run docs/build/tasks/P0-01.md
  python scripts/worker.py run docs/build/tasks/P0-01.md --continue "fix: tests in FooTests fail on Windows paths"
  python scripts/worker.py api docs/build/tasks/P0-07.md --files src/Wondarr.Core/Foo.cs

Environment (.env in the repo root is loaded automatically; existing env vars win)
  OPENROUTER_API_KEY            required for OpenRouter modes
  WONDARR_WORKER_MODEL        e.g. "z-ai/glm-5.3-flash" (pin after the bake-off; see docs/build/AGENT_WORKFLOW.md §6)
  WONDARR_REVIEWER_MODEL      optional mid-tier model for `review`
  WONDARR_WORKER_MAX_TURNS    default 25
  WONDARR_WORKER_TIMEOUT_MIN  default 20
  WONDARR_WORKER_BUDGET_USD   default 2.00 per run
  WONDARR_OPENROUTER_BASE_URL default https://openrouter.ai/api/v1 (override for tests)
  WONDARR_WORKER_MAX_TOKENS   default 16384 (per model response)
  WONDARR_WORKER_CMD_TIMEOUT_S default 900 (per build/test command)
  WONDARR_WORKER_BASE         the branch worker worktrees start from and are diffed against (default "main"); set it
                              when the orchestrator itself works in a linked worktree on its own branch
Run from a linked worktree, the script reads the main checkout's .env when the worktree has none (it is git-ignored).
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
WORKTREES = REPO / ".worktrees"
REPORTS = REPO / ".worker"
REFS = REPORTS / "ref"
SYSTEM_PROMPT = REPO / "docs" / "build" / "WORKER_SYSTEM_PROMPT.md"
STANDARDS = REPO / "docs" / "build" / "CODING_STANDARDS.md"
PROTECTED = ("AGENTS.md", "CLAUDE.md", "docs/DECISIONS.md", "docs/adr/", ".claude/", ".github/workflows/", ".env", "LICENSE")


# ----------------------------------------------------------------------------- env
def load_dotenv(path: Path) -> None:
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        # An inline comment starts at whitespace followed by "#" (a space or a tab).
        value = re.split(r"\s#", value, maxsplit=1)[0].strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


def main_checkout() -> Path:
    """The main working tree, which holds the git-ignored .env even when this script runs from a linked worktree."""
    result = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=str(REPO),
                            text=True, capture_output=True)
    common = Path(result.stdout.strip()) if result.returncode == 0 and result.stdout.strip() else REPO / ".git"
    return common.parent if common.name == ".git" else REPO


def load_env_files() -> None:
    load_dotenv(REPO / ".env")
    if not (REPO / ".env").exists():
        load_dotenv(main_checkout() / ".env")


def base_branch() -> str:
    return env("WONDARR_WORKER_BASE", "main") or "main"


def env(name: str, default: str | None = None) -> str | None:
    value = os.environ.get(name)
    if value in (None, "") and name.startswith("WONDARR_"):
        # .env files written before the rename (2026-09-28) still say COMPILARR_*.
        value = os.environ.get("COMPILARR_" + name[len("WONDARR_"):])
    return value if value not in (None, "") else default


def redact(value: str | None) -> str:
    if not value:
        return "<unset>"
    return value[:6] + "..." + value[-4:] if len(value) > 12 else "***"


# ----------------------------------------------------------------------------- pricing
# Real cost comes from OpenRouter's per-response `usage.cost` (falling back to token usage x the model's price
# list), so WONDARR_WORKER_BUDGET_USD is a cap on real USD.
OPENROUTER_BASE = "https://openrouter.ai/api/v1"


def openrouter_base() -> str:
    return env("WONDARR_OPENROUTER_BASE_URL", OPENROUTER_BASE) or OPENROUTER_BASE


def openrouter_pricing(model: str) -> dict[str, float] | None:
    try:
        with urllib.request.urlopen(openrouter_base().rstrip("/") + "/models", timeout=60) as resp:
            models = json.loads(resp.read().decode("utf-8"))["data"]
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError):
        return None
    for m in models:
        if m.get("id") == model:
            return {k: float(v) for k, v in (m.get("pricing") or {}).items() if _is_number(v)}
    return None


def _is_number(value: object) -> bool:
    try:
        float(value)  # type: ignore[arg-type]
        return True
    except (TypeError, ValueError):
        return False


def openrouter_key_usage() -> float | None:
    key = env("OPENROUTER_API_KEY")
    if not key:
        return None
    req = urllib.request.Request(openrouter_base().rstrip("/") + "/key", headers={"Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return float(json.loads(resp.read().decode("utf-8"))["data"]["usage"])
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError, TypeError):
        return None


# ----------------------------------------------------------------------------- git
def git(*args: str, cwd: Path = REPO, check: bool = True) -> str:
    result = subprocess.run(["git", *args], cwd=str(cwd), text=True, encoding="utf-8", errors="replace", capture_output=True)
    if check and result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed:\n{result.stderr.strip()}")
    return result.stdout.strip()


def task_id_from(task_file: Path) -> str:
    return re.sub(r"[^A-Za-z0-9_-]", "-", task_file.stem)


def slug_from(task_file: Path) -> str:
    first = task_file.read_text(encoding="utf-8").splitlines()[0] if task_file.exists() else ""
    title = re.sub(r"^#\s*Task\s+\S+:\s*", "", first).strip() or task_file.stem
    return re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")[:40] or "task"


def phase_from(task_id: str) -> str:
    match = re.match(r"P(\d+)", task_id, re.IGNORECASE)
    return match.group(1) if match else "x"


def ensure_worktree(task_id: str, task_file: Path, dry_run: bool) -> Path:
    path = WORKTREES / task_id
    branch = f"phase{phase_from(task_id)}/{task_id.lower()}-{slug_from(task_file)}"
    if path.exists():
        print(f"[worker] reusing worktree {path}")
        return path
    if dry_run:
        print(f"[worker] would create worktree {path} on branch {branch} from {base_branch()}")
        return path
    WORKTREES.mkdir(exist_ok=True)
    existing = git("branch", "--list", branch)
    if existing:
        git("worktree", "add", str(path), branch)
    else:
        git("worktree", "add", "-b", branch, str(path), base_branch())
    print(f"[worker] created worktree {path} on branch {branch}")
    return path


# ----------------------------------------------------------------------------- agent worker
def build_prompt(task_file: Path, continue_note: str | None) -> str:
    task_rel = task_file.resolve().relative_to(REPO) if task_file.resolve().is_relative_to(REPO) else task_file
    parts = [
        f"Implement the task in `{task_rel}`. Work only inside this worktree.",
        f"Before writing code, read the task file, `{STANDARDS.relative_to(REPO)}`, and the docs it references.",
        "Protected paths you must not modify: " + ", ".join(PROTECTED) + ".",
        "Your turn budget is small. Paths are relative to the worktree root; never use absolute paths. "
        "Issue independent tool calls (reads, writes) in parallel in one turn. "
        "You have no network tools: use the package versions the task gives; do not look versions up.",
        "Commit as soon as the build and tests are green (a partial commit beats none), then print the done-report.",
    ]
    if continue_note:
        parts.insert(0, f"CONTINUATION of a previous run on this task. Address exactly this feedback first:\n{continue_note}\n")
    return "\n".join(parts)


def run_worker(args: argparse.Namespace) -> int:
    import worker_agent

    task_file = Path(args.task).resolve()
    if not task_file.exists():
        raise SystemExit(f"task file not found: {task_file}")
    task_id = args.id or task_id_from(task_file)
    model = args.model or env("WONDARR_WORKER_MODEL")
    if not model:
        raise SystemExit("WONDARR_WORKER_MODEL is not set (pin it in .env, or pass --model from the tier picks in docs/build/MODEL_VALUE_MATRIX.md)")
    key = env("OPENROUTER_API_KEY")
    if not key and not args.dry_run:
        raise SystemExit("OPENROUTER_API_KEY is not set (put it in .env)")
    max_turns = args.max_turns or int(env("WONDARR_WORKER_MAX_TURNS", "25") or 25)
    timeout_min = int(env("WONDARR_WORKER_TIMEOUT_MIN", "20") or 20)
    budget = float(env("WONDARR_WORKER_BUDGET_USD", "2.00") or 2.0)
    max_tokens = int(env("WONDARR_WORKER_MAX_TOKENS", "16384") or 16384)
    worktree = ensure_worktree(task_id, task_file, args.dry_run)
    system = SYSTEM_PROMPT.read_text(encoding="utf-8")
    prompt = build_prompt(task_file, args.continue_note)
    print(f"[worker] task={task_id} model={model} turns={max_turns} budget=${budget:.2f} timeout={timeout_min}m")
    print(f"[worker] base_url={openrouter_base()} key={redact(key)} tools={[t['function']['name'] for t in worker_agent.TOOLS]}")
    if args.dry_run:
        print(f"[worker] dry run - system prompt {len(system)} chars; first message:\n{prompt}")
        return 0
    pricing = openrouter_pricing(model)
    sandbox = worker_agent.Sandbox(root=worktree, protected=PROTECTED, refs=REFS if REFS.is_dir() else None,
                                   command_timeout_s=int(env("WONDARR_WORKER_CMD_TIMEOUT_S", "900") or 900))
    REPORTS.mkdir(exist_ok=True)
    out_dir = REPORTS / task_id
    out_dir.mkdir(exist_ok=True)
    started = _dt.datetime.now()
    usage_before = openrouter_key_usage()
    run_no = len(list(out_dir.glob("transcript*.json"))) + 1
    suffix = "" if run_no == 1 else f".{run_no}"
    label = f"{task_id} run {run_no}"
    with (out_dir / "live.log").open("a", encoding="utf-8") as log:
        def emit(text: str) -> None:
            line = f"{_dt.datetime.now():%H:%M:%S} [{label}] {text}"
            print(line, flush=True)
            log.write(line + "\n")
            log.flush()

        result = worker_agent.run_agent(base_url=openrouter_base(), key=key or "", model=model, system=system, user=prompt,
                                        sandbox=sandbox, max_turns=max_turns, budget_usd=budget,
                                        timeout_s=timeout_min * 60, pricing=pricing, emit=emit, max_tokens=max_tokens)
    (out_dir / f"transcript{suffix}.json").write_text(json.dumps(result.messages, indent=1), encoding="utf-8")
    report = result.text or f"(no done-report; run stopped: {result.stop})"
    if result.stop in ("timeout", "budget", "error"):
        report = f"STOPPED: {result.stop}\n\n{report}"
    (out_dir / f"report{suffix}.md").write_text(report, encoding="utf-8")
    if suffix:
        (out_dir / "report.md").write_text(report, encoding="utf-8")
    elapsed = (_dt.datetime.now() - started).total_seconds() / 60
    usage_after = openrouter_key_usage()
    key_delta = round(usage_after - usage_before, 4) if usage_before is not None and usage_after is not None else None
    cost = round(result.cost_usd, 4)
    code = {"done": 0, "max_turns": 3, "budget": 4, "error": 5, "timeout": 124}[result.stop]
    summary = {"task": task_id, "run": run_no, "model": model, "exit": code, "stop": result.stop, "turns": result.turns,
               "elapsed_min": round(elapsed, 1), "cost_usd": cost, "prompt_tokens": result.prompt_tokens,
               "completion_tokens": result.completion_tokens, "key_usage_delta_usd": key_delta,
               "continue": bool(args.continue_note)}
    with (out_dir / "runs.jsonl").open("a", encoding="utf-8") as fh:
        fh.write(json.dumps(summary) + "\n")
    changed = git("diff", "--name-only", f"{base_branch()}...HEAD", cwd=worktree, check=False) or "(no committed changes yet)"
    dirty = git("status", "--porcelain", cwd=worktree, check=False)
    print(f"[worker] stop={result.stop} exit={code} cost=${cost} (key delta ${key_delta}, includes any parallel runs) "
          f"turns={result.turns} elapsed={elapsed:.1f}m")
    print(f"[worker] files changed vs {base_branch()}:\n{changed}")
    if dirty:
        print(f"[worker] WARNING uncommitted changes left in the worktree:\n{dirty}")
    print(f"[worker] report: {out_dir / f'report{suffix}.md'}")
    protected_hits = [p for p in changed.splitlines() if any(p.startswith(x.rstrip('/')) for x in PROTECTED)]
    if protected_hits:
        print(f"[worker] WARNING protected paths touched: {protected_hits}")
    return code


def run_watch(args: argparse.Namespace) -> int:
    """Follow every worker's live.log (new lines only), like `tail -f` across tasks."""
    import time

    offsets: dict[Path, int] = {}
    for existing in REPORTS.glob("*/live.log"):
        offsets[existing] = existing.stat().st_size if not args.replay else 0
    print(f"[watch] following {REPORTS}/*/live.log - Ctrl+C to stop", flush=True)
    try:
        while True:
            for log in sorted(REPORTS.glob("*/live.log")):
                size = log.stat().st_size
                start = offsets.get(log, 0)
                if size > start:
                    with log.open("r", encoding="utf-8", errors="replace") as fh:
                        fh.seek(start)
                        sys.stdout.write(fh.read())
                        sys.stdout.flush()
                offsets[log] = size
            time.sleep(1)
    except KeyboardInterrupt:
        return 0


# ----------------------------------------------------------------------------- single-shot API modes
def openrouter_chat(model: str, system: str, user: str, temperature: float = 0.2) -> str:
    key = env("OPENROUTER_API_KEY")
    if not key:
        raise SystemExit("OPENROUTER_API_KEY is not set (put it in .env)")
    body = json.dumps({
        "model": model,
        "temperature": temperature,
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
    }).encode("utf-8")
    req = urllib.request.Request(
        openrouter_base().rstrip("/") + "/chat/completions",
        data=body,
        headers={
            "Authorization": f"Bearer {key}",
            "Content-Type": "application/json",
            "HTTP-Referer": "https://github.com/wulfftech/wondarr",
            "X-Title": "Wondarr build worker",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as err:
        raise SystemExit(f"OpenRouter HTTP {err.code}: {err.read().decode('utf-8', 'replace')[:500]}")
    return payload["choices"][0]["message"]["content"]


def run_api(args: argparse.Namespace) -> int:
    task_file = Path(args.task).resolve()
    task_id = task_id_from(task_file)
    model = args.model or env("WONDARR_WORKER_MODEL")
    if not model:
        raise SystemExit("WONDARR_WORKER_MODEL is not set")
    user = "# TASK\n" + task_file.read_text(encoding="utf-8")
    for f in args.files or []:
        p = Path(f)
        user += f"\n\n# FILE: {f}\n```\n{p.read_text(encoding='utf-8')}\n```"
    user += ("\n\nReturn complete file contents for every file you create or change, each in a fenced block "
             "preceded by a line `### path/to/file`. Do not return diffs. Finish with the done-report.")
    # The shared worker prompt assumes tools; in this mode there are none, so say so up front or the
    # model answers with (unexecuted) tool calls instead of file contents.
    system = ("SINGLE-SHOT MODE: you have no tools and cannot read or run anything. Every file you need is "
              "inlined in the user message. Answer only with the complete contents of each file you create or "
              "change, then the done-report. Ignore instructions below about reading files or running "
              "commands.\n\n" + SYSTEM_PROMPT.read_text(encoding="utf-8"))
    print(f"[worker] api task={task_id} model={model} files={len(args.files or [])} key={redact(env('OPENROUTER_API_KEY'))}")
    if args.dry_run:
        print(f"[worker] dry run — would send {len(user)} chars to OpenRouter")
        return 0
    out_dir = REPORTS / task_id
    out_dir.mkdir(parents=True, exist_ok=True)
    answer = openrouter_chat(model, system, user)
    (out_dir / "response.md").write_text(answer, encoding="utf-8")
    print(f"[worker] response written to {out_dir / 'response.md'} ({len(answer)} chars)")
    return 0


def run_review(args: argparse.Namespace) -> int:
    model = args.model or env("WONDARR_REVIEWER_MODEL") or env("WONDARR_WORKER_MODEL")
    if not model:
        raise SystemExit("WONDARR_REVIEWER_MODEL / WONDARR_WORKER_MODEL not set")
    diff = git("diff", f"{base_branch()}...{args.branch}")
    if not diff:
        print("[worker] empty diff; nothing to review")
        return 0
    task_text = Path(args.task).read_text(encoding="utf-8") if args.task else "(no task file given)"
    system = ("You are a strict code reviewer for Wondarr. Judge the diff against the task's acceptance criteria and "
              + STANDARDS.read_text(encoding="utf-8") +
              "\nReport findings ranked by severity with file:line, then a verdict line: MERGE or FIX-FIRST.")
    user = f"# TASK\n{task_text}\n\n# DIFF ({base_branch()}...{args.branch})\n```diff\n{diff[:200000]}\n```"
    print(f"[worker] review branch={args.branch} model={model} diff_chars={len(diff)}")
    if args.dry_run:
        return 0
    answer = openrouter_chat(model, system, user, temperature=0.0)
    out_dir = REPORTS / re.sub(r"[^A-Za-z0-9_-]", "-", args.branch)
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "review.md").write_text(answer, encoding="utf-8")
    print(answer)
    return 0


# ----------------------------------------------------------------------------- main
def main() -> int:
    # model output is arbitrary Unicode; never let a cp1252 console crash the runner
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    load_env_files()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="print what would run; touch nothing")
    parser.add_argument("--model", help="override the model id for this run")
    sub = parser.add_subparsers(dest="mode", required=True)
    p_run = sub.add_parser("run", help="tool-calling worker in a git worktree (direct OpenRouter API)")
    p_run.add_argument("task")
    p_run.add_argument("--max-turns", type=int)
    p_run.add_argument("--continue", dest="continue_note", help="feedback for a continuation run on the same task")
    p_run.add_argument("--id", help="override the task id (worktree/report name), e.g. to run one task with several models")
    p_api = sub.add_parser("api", help="single-shot OpenRouter call, no tools")
    p_api.add_argument("task")
    p_api.add_argument("--files", nargs="*")
    p_rev = sub.add_parser("review", help="single-shot review of git diff main...<branch>")
    p_rev.add_argument("branch")
    p_rev.add_argument("--task")
    p_watch = sub.add_parser("watch", help="follow all workers' live progress (run in a terminal you can see)")
    p_watch.add_argument("--replay", action="store_true", help="print existing log content first")
    args = parser.parse_args()
    if args.mode == "watch":
        return run_watch(args)
    if args.mode == "run":
        return run_worker(args)
    if args.mode == "api":
        return run_api(args)
    return run_review(args)


if __name__ == "__main__":
    sys.exit(main())
