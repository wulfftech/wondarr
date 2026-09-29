#!/usr/bin/env python3
"""
Wondarr cheap-worker runner (standard library only, Python 3.10+, Windows/macOS/Linux).

Modes
  run   <task.md>   Run a headless `claude -p` worker against OpenRouter (or Anthropic) in a git worktree.
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
  OPENROUTER_ANTHROPIC_BASE_URL default https://openrouter.ai/api  (Claude Code docs show https://openrouter.ai/api/v1; switch if you get 404s)
  WONDARR_WORKER_PROVIDER     "openrouter" (default) or "anthropic" (uses your normal Claude Code auth; model via WONDARR_WORKER_MODEL, e.g. "haiku")
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import re
import shutil
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
PROTECTED = ("CLAUDE.md", "docs/DECISIONS.md", "docs/adr/", ".claude/", ".github/workflows/", ".env", "LICENSE")
# Bash patterns are matched per sub-command, so `cd x && dotnet build` needs both `cd` and `dotnet` allowed.
# Read-only helpers are included because denied calls still burn a turn (see the P0-01 bake-off).
WORKER_TOOLS = (
    "Read", "Edit", "Write", "Grep", "Glob",
    "Bash(dotnet *)", "Bash(npm *)", "Bash(npx *)", "Bash(node *)",
    "Bash(cd *)", "Bash(pwd)", "Bash(ls*)", "Bash(mkdir *)", "Bash(echo *)",
    "Bash(git status*)", "Bash(git diff*)", "Bash(git log*)", "Bash(git show*)", "Bash(git add *)",
    "Bash(git rm *)", "Bash(git mv *)", "Bash(git commit *)", "Bash(git update-index --chmod*)",
    "Bash(sh -n *)",
)


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
# Claude Code prices model ids it does not recognise at Opus rates (USD per token), so its
# total_cost_usd and --max-budget-usd are inflated for OpenRouter models. We compute real cost
# from token usage and OpenRouter's price list, and scale the budget flag so the .env cap means real USD.
CLAUDE_UNKNOWN_RATES = {"prompt": 5e-6, "completion": 25e-6, "input_cache_read": 0.5e-6, "input_cache_write": 6.25e-6}


def openrouter_pricing(model: str) -> dict[str, float] | None:
    try:
        with urllib.request.urlopen("https://openrouter.ai/api/v1/models", timeout=60) as resp:
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


def budget_scale(model: str, pricing: dict[str, float] | None) -> float:
    """Factor by which Claude Code over-counts this model, so real spend stays within the cap."""
    if pricing is None or "claude" in model or "anthropic" in model:
        return 1.0
    ratios = [CLAUDE_UNKNOWN_RATES[k] / pricing[k] for k in CLAUDE_UNKNOWN_RATES if pricing.get(k, 0) > 0]
    return max(1.0, min(ratios)) if ratios else 1.0


def real_cost(model_usage: dict, pricing: dict[str, float] | None) -> float | None:
    if pricing is None or not model_usage:
        return None
    total = 0.0
    for usage in model_usage.values():
        write_rate = pricing.get("input_cache_write") or pricing.get("prompt", 0)
        total += usage.get("inputTokens", 0) * pricing.get("prompt", 0)
        total += usage.get("outputTokens", 0) * pricing.get("completion", 0)
        total += usage.get("cacheReadInputTokens", 0) * (pricing.get("input_cache_read") or pricing.get("prompt", 0))
        total += usage.get("cacheCreationInputTokens", 0) * write_rate
    return round(total, 4)


def openrouter_key_usage() -> float | None:
    key = env("OPENROUTER_API_KEY")
    if not key:
        return None
    req = urllib.request.Request("https://openrouter.ai/api/v1/key", headers={"Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return float(json.loads(resp.read().decode("utf-8"))["data"]["usage"])
    except (urllib.error.URLError, TimeoutError, ValueError, KeyError):
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
        print(f"[worker] would create worktree {path} on branch {branch} from main")
        return path
    WORKTREES.mkdir(exist_ok=True)
    existing = git("branch", "--list", branch)
    if existing:
        git("worktree", "add", str(path), branch)
    else:
        git("worktree", "add", "-b", branch, str(path), "main")
    print(f"[worker] created worktree {path} on branch {branch}")
    return path


# ----------------------------------------------------------------------------- claude worker
def find_claude() -> str | None:
    for candidate in ("claude", "claude.cmd", "claude.exe"):
        found = shutil.which(candidate)
        if found:
            return found
    return None


def worker_env(provider: str, model: str) -> dict[str, str]:
    e = dict(os.environ)
    for key in ("ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL",
                "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
                "ANTHROPIC_DEFAULT_OPUS_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL"):
        e.pop(key, None)
    if provider == "openrouter":
        key = env("OPENROUTER_API_KEY")
        if not key:
            raise SystemExit("OPENROUTER_API_KEY is not set (put it in .env)")
        e["ANTHROPIC_BASE_URL"] = env("OPENROUTER_ANTHROPIC_BASE_URL", "https://openrouter.ai/api") or ""
        e["ANTHROPIC_API_KEY"] = key
        e["ANTHROPIC_MODEL"] = model
        # keep any nested subagent inside the worker on the same cheap model
        for fam in ("HAIKU", "SONNET", "OPUS"):
            e[f"ANTHROPIC_DEFAULT_{fam}_MODEL"] = model
        e["CLAUDE_CODE_SUBAGENT_MODEL"] = model
    e["WONDARR_WORKER"] = "1"
    return e


def build_prompt(task_file: Path, continue_note: str | None) -> str:
    task_rel = task_file.resolve().relative_to(REPO) if task_file.resolve().is_relative_to(REPO) else task_file
    parts = [
        f"Implement the task in `{task_rel}`. Work only inside this worktree.",
        f"Before writing code, read the task file, `{STANDARDS.relative_to(REPO)}`, and the docs it references.",
        "Protected paths you must not modify: " + ", ".join(PROTECTED) + ".",
        "Your turn budget is small. Your working directory is already the worktree root: use relative paths and "
        "never `cd` to an absolute path. Issue independent tool calls (reads, writes) in parallel in one turn. "
        "You have no network tools: use the package versions the task gives; do not look versions up.",
        "Commit as soon as the build and tests are green (a partial commit beats none), then print the done-report.",
    ]
    if continue_note:
        parts.insert(0, f"CONTINUATION of a previous run on this task. Address exactly this feedback first:\n{continue_note}\n")
    return "\n".join(parts)


def run_worker(args: argparse.Namespace) -> int:
    task_file = Path(args.task).resolve()
    if not task_file.exists():
        raise SystemExit(f"task file not found: {task_file}")
    task_id = args.id or task_id_from(task_file)
    provider = (env("WONDARR_WORKER_PROVIDER", "openrouter") or "openrouter").lower()
    model = args.model or env("WONDARR_WORKER_MODEL") or ("haiku" if provider == "anthropic" else None)
    if not model:
        raise SystemExit("WONDARR_WORKER_MODEL is not set (pin it in .env after the bake-off)")
    max_turns = args.max_turns or int(env("WONDARR_WORKER_MAX_TURNS", "25") or 25)
    timeout_min = int(env("WONDARR_WORKER_TIMEOUT_MIN", "20") or 20)
    budget = float(env("WONDARR_WORKER_BUDGET_USD", "2.00") or 2.0)
    pricing = openrouter_pricing(model) if provider == "openrouter" else None
    scale = budget_scale(model, pricing)
    worktree = ensure_worktree(task_id, task_file, args.dry_run)
    claude = find_claude()
    cmd = [
        claude or "claude", "-p", build_prompt(task_file, args.continue_note),
        # stream-json so progress can be followed live (`worker.py watch`); the final "result"
        # event is the same object `--output-format json` would print
        "--output-format", "stream-json", "--verbose",
        "--max-turns", str(max_turns),
        "--max-budget-usd", f"{budget * scale:.2f}",
        "--permission-mode", "acceptEdits",
        "--allowedTools", ",".join(WORKER_TOOLS),
        # the worktree has no .env (git-ignored); keep the main checkout's secrets out of reach too
        "--disallowedTools", f"Read(//{REPO.drive[:1].lower()}{REPO.as_posix()[len(REPO.drive):]}/.env*)",
        "--append-system-prompt-file", str(SYSTEM_PROMPT),
        "--no-session-persistence",
    ]
    if REFS.is_dir():
        # upstream sources checked out for porting (git-ignored); readable, not writable, by the worker
        cmd += ["--add-dir", str(REFS)]
    if provider == "anthropic":
        cmd += ["--model", model]
    e = worker_env(provider, model)
    print(f"[worker] task={task_id} provider={provider} model={model} turns={max_turns} budget=${budget:.2f} "
          f"(claude flag x{scale:.1f}) timeout={timeout_min}m")
    print(f"[worker] base_url={e.get('ANTHROPIC_BASE_URL', '<claude default>')} key={redact(e.get('ANTHROPIC_API_KEY'))}")
    if args.dry_run:
        print("[worker] dry run - command:")
        print("  " + " ".join(repr(c) if " " in c else c for c in cmd))
        return 0
    if not claude:
        raise SystemExit("claude CLI not found on PATH")
    REPORTS.mkdir(exist_ok=True)
    out_dir = REPORTS / task_id
    out_dir.mkdir(exist_ok=True)
    started = _dt.datetime.now()
    usage_before = openrouter_key_usage() if provider == "openrouter" else None
    run_no = len(list(out_dir.glob("stdout*.json"))) + 1
    suffix = "" if run_no == 1 else f".{run_no}"
    live = out_dir / "live.log"
    returncode, result_json, stderr_text, timed_out = stream_worker(cmd, worktree, e, timeout_min * 60, live,
                                                                    f"{task_id} run {run_no}")
    if timed_out:
        (out_dir / "report.md").write_text(f"TIMEOUT after {timeout_min} minutes\n", encoding="utf-8")
        print(f"[worker] TIMEOUT after {timeout_min} minutes; worktree kept at {worktree}")
        return 124
    result = subprocess.CompletedProcess(cmd, returncode, result_json, stderr_text)
    (out_dir / f"stdout{suffix}.json").write_text(result.stdout, encoding="utf-8")
    (out_dir / f"stderr{suffix}.txt").write_text(result.stderr, encoding="utf-8")
    report_text, cost, turns, model_usage = extract_report(result.stdout)
    (out_dir / f"report{suffix}.md").write_text(report_text, encoding="utf-8")
    elapsed = (_dt.datetime.now() - started).total_seconds() / 60
    usd = real_cost(model_usage, pricing) if provider == "openrouter" else (float(cost) if _is_number(cost) else None)
    usage_after = openrouter_key_usage() if provider == "openrouter" else None
    key_delta = round(usage_after - usage_before, 4) if usage_before is not None and usage_after is not None else None
    summary = {"task": task_id, "run": run_no, "model": model, "exit": result.returncode, "turns": turns,
               "elapsed_min": round(elapsed, 1), "cost_usd": usd, "claude_reported_cost": cost,
               "key_usage_delta_usd": key_delta, "continue": bool(args.continue_note)}
    with (out_dir / "runs.jsonl").open("a", encoding="utf-8") as fh:
        fh.write(json.dumps(summary) + "\n")
    changed = git("diff", "--name-only", "main...HEAD", cwd=worktree, check=False) or "(no committed changes yet)"
    print(f"[worker] exit={result.returncode} cost=${usd} (claude-reported ${cost}; key delta ${key_delta}, "
          f"includes any parallel runs) turns={turns} elapsed={elapsed:.1f}m")
    print(f"[worker] files changed vs main:\n{changed}")
    print(f"[worker] report: {out_dir / 'report.md'}")
    protected_hits = [p for p in changed.splitlines() if any(p.startswith(x.rstrip('/')) for x in PROTECTED)]
    if protected_hits:
        print(f"[worker] WARNING protected paths touched: {protected_hits}")
    return result.returncode


def stream_worker(cmd: list[str], cwd: Path, env_vars: dict[str, str], timeout_s: int, live: Path,
                  label: str) -> tuple[int, str, str, bool]:
    """Run the worker, echoing a readable line per event to stdout and `live`; return the result event."""
    import threading

    proc = subprocess.Popen(cmd, cwd=str(cwd), env=env_vars, text=True, encoding="utf-8", errors="replace",
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    timed_out = threading.Event()

    def on_timeout() -> None:
        timed_out.set()
        proc.kill()

    timer = threading.Timer(timeout_s, on_timeout)
    timer.start()
    stderr_chunks: list[str] = []
    drain = threading.Thread(target=lambda: stderr_chunks.append(proc.stderr.read() if proc.stderr else ""))
    drain.start()
    result_json = ""
    turn = 0
    with live.open("a", encoding="utf-8") as log:
        def emit(text: str) -> None:
            line = f"{_dt.datetime.now():%H:%M:%S} [{label}] {text}"
            print(line, flush=True)
            log.write(line + "\n")
            log.flush()

        emit("started")
        assert proc.stdout is not None
        for raw in proc.stdout:
            try:
                event = json.loads(raw)
            except json.JSONDecodeError:
                continue
            kind = event.get("type")
            if kind == "assistant":
                turn += 1
                for block in event.get("message", {}).get("content", []):
                    if block.get("type") == "text" and block.get("text", "").strip():
                        emit(f"t{turn} says: {one_line(block['text'], 160)}")
                    elif block.get("type") == "tool_use":
                        emit(f"t{turn} {block.get('name')}: {describe_tool(block.get('input') or {})}")
            elif kind == "user":
                for block in event.get("message", {}).get("content", []) or []:
                    if isinstance(block, dict) and block.get("type") == "tool_result" and block.get("is_error"):
                        emit(f"   ! tool error: {one_line(str(block.get('content')), 160)}")
            elif kind == "result":
                result_json = raw
                emit(f"finished: {event.get('subtype')} after {event.get('num_turns')} turns")
    proc.wait()
    timer.cancel()
    drain.join(timeout=5)
    return proc.returncode, result_json, "".join(stderr_chunks), timed_out.is_set()


def one_line(text: str, limit: int) -> str:
    text = " ".join(str(text).split())
    return text if len(text) <= limit else text[: limit - 3] + "..."


def describe_tool(tool_input: dict) -> str:
    for key in ("command", "file_path", "pattern", "path"):
        if tool_input.get(key):
            return one_line(tool_input[key], 140)
    return one_line(json.dumps(tool_input), 140)


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


def extract_report(stdout: str) -> tuple[str, str, str, dict]:
    """Best-effort extraction from `claude -p --output-format json` output."""
    try:
        data = json.loads(stdout)
    except json.JSONDecodeError:
        return stdout, "?", "?", {}
    if not isinstance(data, dict):
        return json.dumps(data, indent=2), "?", "?", {}
    text = data.get("result")
    cost = str(data.get("total_cost_usd", data.get("cost_usd", "?")))
    turns = str(data.get("num_turns", "?"))
    return (text or json.dumps(data, indent=2)), cost, turns, data.get("modelUsage") or {}


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
        "https://openrouter.ai/api/v1/chat/completions",
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
    diff = git("diff", f"main...{args.branch}")
    if not diff:
        print("[worker] empty diff; nothing to review")
        return 0
    task_text = Path(args.task).read_text(encoding="utf-8") if args.task else "(no task file given)"
    system = ("You are a strict code reviewer for Wondarr. Judge the diff against the task's acceptance criteria and "
              + STANDARDS.read_text(encoding="utf-8") +
              "\nReport findings ranked by severity with file:line, then a verdict line: MERGE or FIX-FIRST.")
    user = f"# TASK\n{task_text}\n\n# DIFF (main...{args.branch})\n```diff\n{diff[:200000]}\n```"
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
    load_dotenv(REPO / ".env")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="print what would run; touch nothing")
    parser.add_argument("--model", help="override the model id for this run")
    sub = parser.add_subparsers(dest="mode", required=True)
    p_run = sub.add_parser("run", help="headless claude worker in a worktree")
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
