#!/usr/bin/env python3
"""
Tool-calling agent loop for Wondarr workers (standard library only, Python 3.10+, Windows/macOS/Linux).

Talks to OpenRouter's OpenAI-compatible chat-completions API directly (no CLI, no Anthropic endpoint) and gives
the model a small sandboxed toolset inside one git worktree:

  read_file, write_file, edit_file, list_files, grep, run

Sandbox rules (enforced here, not just requested in the prompt):
  - paths must resolve inside the worktree (reads may also use the read-only refs directory);
  - `.env*` is never readable; protected paths (AGENTS.md, docs/adr/, .github/workflows/, ...) are never writable;
  - `run` accepts an allowlist only (dotnet, npm, npx, node, local git, `sh -n`, and the builtins cd/pwd/ls/mkdir/echo),
    chained with `&&` only: no pipes, redirects, `;` or subshells; no shell is ever invoked;
  - child processes do not inherit OPENROUTER_API_KEY.

Caps: turns, real USD (OpenRouter-reported cost, falling back to token usage x price list), wall-clock.
"""
from __future__ import annotations

import json
import os
import re
import shlex
import shutil
import subprocess
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

SKIP_DIRS = {".git", "node_modules", "bin", "obj", "dist", ".vs", ".worktrees", ".worker"}
MAX_TOOL_OUTPUT = 12000
READ_DEFAULT_LINES = 2000
COMPACT_AFTER_CHARS = 300_000
KEEP_FULL_TOOL_RESULTS = 6

EXTERNAL_COMMANDS = {"dotnet", "npm", "npx", "node"}
GIT_SUBCOMMANDS = {"status", "diff", "log", "show", "add", "rm", "mv", "commit", "update-index"}


# ----------------------------------------------------------------------------- sandbox
class ToolError(Exception):
    """A problem the model should see and can correct."""


@dataclass
class Sandbox:
    root: Path
    protected: tuple[str, ...]
    refs: Path | None = None
    command_timeout_s: int = 900
    deadline: float | None = None  # monotonic seconds; commands never outlive the run
    root_resolved: Path = field(init=False)

    def __post_init__(self) -> None:
        self.root_resolved = self.root.resolve()

    def _inside(self, path: Path, base: Path) -> bool:
        try:
            path.relative_to(base)
            return True
        except ValueError:
            return False

    def resolve(self, raw: str, *, write: bool = False, cwd: Path | None = None) -> Path:
        if not raw or "\x00" in raw:
            raise ToolError("path is empty")
        base = cwd or self.root_resolved
        p = Path(raw)
        p = (p if p.is_absolute() else base / p).resolve()
        in_root = self._inside(p, self.root_resolved)
        in_refs = bool(self.refs) and self._inside(p, self.refs.resolve())  # type: ignore[union-attr]
        if not (in_root or (in_refs and not write)):
            raise ToolError(f"path is outside the worktree: {raw} (use paths relative to the worktree root)")
        rel_parts = p.relative_to(self.root_resolved).parts if in_root else p.parts
        if any(part.startswith(".env") for part in rel_parts):
            raise ToolError("`.env*` files are off limits")
        if write:
            rel = p.relative_to(self.root_resolved).as_posix()
            if rel == ".git" or rel.startswith(".git/"):
                raise ToolError("writing inside .git is not allowed (use git commands)")
            for prot in self.protected:
                stem = prot.rstrip("/")
                if rel.startswith(stem):
                    raise ToolError(f"protected path, not writable by workers: {prot}")
        return p

    def rel(self, p: Path) -> str:
        try:
            return p.relative_to(self.root_resolved).as_posix()
        except ValueError:
            return str(p)


def clip(text: str, limit: int = MAX_TOOL_OUTPUT) -> str:
    if len(text) <= limit:
        return text
    head = limit // 4
    return text[:head] + f"\n... [{len(text) - limit} characters omitted] ...\n" + text[-(limit - head):]


# ----------------------------------------------------------------------------- tools
def tool_read_file(sb: Sandbox, a: dict) -> str:
    p = sb.resolve(str(a.get("path", "")))
    if not p.is_file():
        raise ToolError(f"not a file: {a.get('path')}")
    try:
        lines = p.read_text(encoding="utf-8").splitlines()
    except UnicodeDecodeError:
        raise ToolError("binary or non-UTF-8 file")
    start = max(1, int(a.get("offset") or 1))
    limit = max(1, int(a.get("limit") or READ_DEFAULT_LINES))
    chunk = lines[start - 1:start - 1 + limit]
    out = "\n".join(f"{start + i}\t{line}" for i, line in enumerate(chunk))
    if start - 1 + limit < len(lines):
        out += f"\n... [{len(lines)} lines total; continue with offset={start + limit}]"
    return out or "(empty file)"


def tool_write_file(sb: Sandbox, a: dict) -> str:
    p = sb.resolve(str(a.get("path", "")), write=True)
    content = a.get("content")
    if not isinstance(content, str):
        raise ToolError("`content` must be a string")
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8", newline="")
    return f"wrote {sb.rel(p)} ({len(content)} chars)"


def tool_edit_file(sb: Sandbox, a: dict) -> str:
    p = sb.resolve(str(a.get("path", "")), write=True)
    old, new = a.get("old_string"), a.get("new_string")
    if not isinstance(old, str) or not isinstance(new, str) or old == "":
        raise ToolError("`old_string` (non-empty) and `new_string` are required strings")
    if old == new:
        raise ToolError("old_string and new_string are identical")
    if not p.is_file():
        raise ToolError(f"not a file: {a.get('path')}")
    with p.open("r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    crlf = "\r\n" in text
    if old not in text and crlf:
        old, new = old.replace("\n", "\r\n"), new.replace("\n", "\r\n")
    count = text.count(old)
    if count == 0:
        raise ToolError("old_string not found (it must match exactly, including whitespace)")
    if count > 1 and not a.get("replace_all"):
        raise ToolError(f"old_string matches {count} places; add context to make it unique or set replace_all")
    p.write_text(text.replace(old, new) if a.get("replace_all") else text.replace(old, new, 1),
                 encoding="utf-8", newline="")
    return f"edited {sb.rel(p)} ({count if a.get('replace_all') else 1} replacement(s))"


def _walk(base: Path):
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            if not name.startswith(".env"):
                yield Path(dirpath) / name


def tool_list_files(sb: Sandbox, a: dict) -> str:
    base = sb.resolve(str(a.get("path") or "."))
    pattern = str(a.get("pattern") or "**/*")
    hits: list[str] = []
    for f in _walk(base):
        rel = f.relative_to(base).as_posix()
        if f.match(pattern) or Path(rel).match(pattern):
            hits.append(sb.rel(f))
            if len(hits) >= 300:
                hits.append("... [truncated at 300 files; narrow the pattern]")
                break
    return "\n".join(hits) or "(no matches)"


def tool_grep(sb: Sandbox, a: dict) -> str:
    base = sb.resolve(str(a.get("path") or "."))
    try:
        rx = re.compile(str(a.get("pattern", "")), re.IGNORECASE if a.get("ignore_case") else 0)
    except re.error as err:
        raise ToolError(f"bad regex: {err}")
    glob = a.get("glob")
    files = [base] if base.is_file() else _walk(base)
    out: list[str] = []
    for f in files:
        if glob and not f.match(str(glob)):
            continue
        try:
            if f.stat().st_size > 1_000_000:
                continue
            text = f.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for n, line in enumerate(text.splitlines(), 1):
            if rx.search(line):
                out.append(f"{sb.rel(f)}:{n}:{line.strip()[:200]}")
                if len(out) >= 200:
                    return "\n".join(out) + "\n... [truncated at 200 matches; narrow the search]"
    return "\n".join(out) or "(no matches)"


def _tokenize(command: str) -> list[list[str]]:
    lex = shlex.shlex(command, posix=True, punctuation_chars=True)
    lex.whitespace_split = True
    try:
        tokens = list(lex)
    except ValueError as err:
        raise ToolError(f"cannot parse command: {err}")
    segments: list[list[str]] = [[]]
    for tok in tokens:
        if tok == "&&":
            segments.append([])
        elif tok and all(c in "();<>|&" for c in tok):
            raise ToolError(f"`{tok}` is not allowed: only `&&` chaining, no pipes, redirects, `;` or subshells")
        else:
            segments[-1].append(tok)
    if any(not s for s in segments):
        raise ToolError("empty command segment")
    return segments


def _check_allowed(argv: list[str]) -> None:
    exe = argv[0]
    if exe in {"cd", "pwd", "ls", "mkdir", "echo"} or exe in EXTERNAL_COMMANDS:
        return
    if exe == "git":
        if len(argv) < 2 or argv[1] not in GIT_SUBCOMMANDS:
            raise ToolError("git is limited to: " + ", ".join(sorted(GIT_SUBCOMMANDS)) + " (subcommand must come first)")
        if argv[1] == "update-index" and not (len(argv) > 2 and argv[2].startswith("--chmod")):
            raise ToolError("git update-index is only allowed with --chmod")
        return
    if exe == "sh" and len(argv) >= 3 and argv[1] == "-n":
        return
    raise ToolError(f"command not allowed: {exe}. Allowed: dotnet, npm, npx, node, git (local), sh -n, cd, pwd, ls, mkdir, echo")


def _child_env() -> dict[str, str]:
    e = {k: v for k, v in os.environ.items() if k != "OPENROUTER_API_KEY"}
    e.update({"GIT_PAGER": "cat", "GIT_TERMINAL_PROMPT": "0", "DOTNET_NOLOGO": "1",
              "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "CI": "1", "NO_COLOR": "1"})
    return e


def tool_run(sb: Sandbox, a: dict) -> str:
    command = str(a.get("command", "")).strip()
    if not command:
        raise ToolError("`command` is required")
    segments = _tokenize(command)
    for argv in segments:
        _check_allowed(argv)
    cwd = sb.root_resolved
    outputs: list[str] = []
    for argv in segments:
        exe = argv[0]
        if exe == "cd":
            if len(argv) != 2:
                raise ToolError("cd takes exactly one path")
            target = sb.resolve(argv[1], cwd=cwd)
            if not target.is_dir():
                raise ToolError(f"cd: not a directory: {argv[1]}")
            cwd = target
        elif exe == "pwd":
            outputs.append(sb.rel(cwd) or ".")
        elif exe == "echo":
            outputs.append(" ".join(argv[1:]))
        elif exe == "ls":
            paths = [x for x in argv[1:] if not x.startswith("-")] or ["."]
            for raw in paths:
                target = sb.resolve(raw, cwd=cwd)
                names = sorted(p.name + ("/" if p.is_dir() else "") for p in target.iterdir()) if target.is_dir() else [target.name]
                outputs.append("\n".join(names))
        elif exe == "mkdir":
            for raw in [x for x in argv[1:] if not x.startswith("-")]:
                sb.resolve(raw, write=True, cwd=cwd).mkdir(parents=True, exist_ok=True)
        else:
            path = shutil.which(exe)
            if not path:
                raise ToolError(f"{exe}: not found on PATH")
            timeout = sb.command_timeout_s
            if sb.deadline is not None:
                timeout = max(5, min(timeout, int(sb.deadline - time.monotonic())))
            try:
                proc = subprocess.run([path, *argv[1:]], cwd=str(cwd), env=_child_env(), text=True, encoding="utf-8",
                                      errors="replace", capture_output=True, timeout=timeout, stdin=subprocess.DEVNULL)
            except subprocess.TimeoutExpired:
                raise ToolError(f"`{' '.join(argv)}` timed out after {timeout}s")
            text = (proc.stdout or "") + (proc.stderr or "")
            outputs.append(f"$ {' '.join(argv)}\n{clip(text.strip())}\n[exit {proc.returncode}]")
            if proc.returncode != 0:
                break  # `&&` semantics
    return clip("\n".join(o for o in outputs if o).strip() or "(no output)")


TOOL_IMPLS: dict[str, Callable[[Sandbox, dict], str]] = {
    "read_file": tool_read_file, "write_file": tool_write_file, "edit_file": tool_edit_file,
    "list_files": tool_list_files, "grep": tool_grep, "run": tool_run,
}


def _fn(name: str, description: str, properties: dict, required: list[str]) -> dict:
    return {"type": "function", "function": {"name": name, "description": description,
            "parameters": {"type": "object", "properties": properties, "required": required}}}


TOOLS = [
    _fn("read_file", "Read a text file with line numbers. Paths are relative to the worktree root.",
        {"path": {"type": "string"}, "offset": {"type": "integer", "description": "1-based first line"},
         "limit": {"type": "integer", "description": "max lines (default 2000)"}}, ["path"]),
    _fn("write_file", "Create or overwrite a file with the given full content (LF line endings, end with a newline).",
        {"path": {"type": "string"}, "content": {"type": "string"}}, ["path", "content"]),
    _fn("edit_file", "Replace old_string with new_string in a file. old_string must match exactly once unless replace_all is set.",
        {"path": {"type": "string"}, "old_string": {"type": "string"}, "new_string": {"type": "string"},
         "replace_all": {"type": "boolean"}}, ["path", "old_string", "new_string"]),
    _fn("list_files", "List files matching a glob pattern (e.g. `**/*.cs`) under a directory.",
        {"pattern": {"type": "string"}, "path": {"type": "string"}}, ["pattern"]),
    _fn("grep", "Regex search over file contents; returns path:line:text.",
        {"pattern": {"type": "string"}, "path": {"type": "string"}, "glob": {"type": "string"},
         "ignore_case": {"type": "boolean"}}, ["pattern"]),
    _fn("run", "Run build/test/git commands. Allowed: dotnet, npm, npx, node, git (status, diff, log, show, add, rm, mv, "
               "commit, update-index --chmod), `sh -n`, cd, pwd, ls, mkdir, echo. Chain with && only; no pipes, redirects or `;`. "
               "Use forward slashes. Each call starts at the worktree root.",
        {"command": {"type": "string"}}, ["command"]),
]


def execute_tool(sb: Sandbox, name: str, raw_args: str) -> tuple[str, bool]:
    impl = TOOL_IMPLS.get(name)
    if impl is None:
        return f"unknown tool: {name}", True
    try:
        args = json.loads(raw_args) if raw_args and raw_args.strip() else {}
        if not isinstance(args, dict):
            raise ValueError("arguments must be a JSON object")
    except ValueError as err:
        return f"invalid tool arguments: {err}", True
    try:
        return impl(sb, args), False
    except ToolError as err:
        return str(err), True
    except OSError as err:
        return f"{type(err).__name__}: {err}", True


def describe_call(name: str, raw_args: str) -> str:
    try:
        args = json.loads(raw_args) if raw_args else {}
    except ValueError:
        return one_line(raw_args, 140)
    for key in ("command", "path", "pattern"):
        if isinstance(args, dict) and args.get(key):
            return one_line(str(args[key]), 140)
    return one_line(raw_args, 140)


def one_line(text: str, limit: int) -> str:
    text = " ".join(str(text).split())
    return text if len(text) <= limit else text[: limit - 3] + "..."


# ----------------------------------------------------------------------------- model client
class ApiError(Exception):
    pass


def chat(base_url: str, key: str, body: dict, timeout: int = 600, attempts: int = 5) -> dict:
    req_body = json.dumps(body).encode("utf-8")
    delay = 2.0
    last: Exception | None = None
    for attempt in range(attempts):
        req = urllib.request.Request(
            base_url.rstrip("/") + "/chat/completions", data=req_body,
            headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json",
                     "HTTP-Referer": "https://github.com/wulfftech/wondarr", "X-Title": "Wondarr build worker"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
            if payload.get("choices"):
                return payload
            err = payload.get("error") or {}
            last = ApiError(f"no choices in response: {one_line(json.dumps(err or payload), 300)}")
            if err.get("code") not in (408, 429, 500, 502, 503, 504, None):
                raise last
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:400]
            last = ApiError(f"HTTP {e.code}: {detail}")
            if e.code not in (408, 429, 500, 502, 503, 504):
                raise last
        except (urllib.error.URLError, TimeoutError, ConnectionError, json.JSONDecodeError) as e:
            last = ApiError(f"{type(e).__name__}: {e}")
        if attempt < attempts - 1:
            time.sleep(delay)
            delay *= 2
    raise last or ApiError("request failed")


# ----------------------------------------------------------------------------- loop
@dataclass
class AgentResult:
    text: str = ""
    stop: str = "done"  # done | max_turns | budget | timeout | error
    turns: int = 0
    cost_usd: float = 0.0
    prompt_tokens: int = 0
    completion_tokens: int = 0
    messages: list = field(default_factory=list)


def _turn_cost(usage: dict, pricing: dict[str, float] | None) -> float:
    if isinstance(usage.get("cost"), (int, float)):
        return float(usage["cost"])
    if not pricing:
        return 0.0
    return (usage.get("prompt_tokens", 0) * pricing.get("prompt", 0)
            + usage.get("completion_tokens", 0) * pricing.get("completion", 0))


def _compact(messages: list[dict]) -> None:
    tool_idx = [i for i, m in enumerate(messages) if m.get("role") == "tool"]
    if sum(len(str(messages[i].get("content", ""))) for i in tool_idx) <= COMPACT_AFTER_CHARS:
        return
    for i in tool_idx[:-KEEP_FULL_TOOL_RESULTS]:
        content = str(messages[i].get("content", ""))
        if len(content) > 600:
            messages[i]["content"] = content[:500] + "\n... [older output trimmed to save context; re-run the tool if you need it]"


def run_agent(*, base_url: str, key: str, model: str, system: str, user: str, sandbox: Sandbox, max_turns: int,
              budget_usd: float, timeout_s: int, pricing: dict[str, float] | None, emit: Callable[[str], None],
              max_tokens: int = 16384, temperature: float = 0.2) -> AgentResult:
    started = time.monotonic()
    sandbox.deadline = started + timeout_s
    messages: list[dict] = [{"role": "system", "content": system}, {"role": "user", "content": user}]
    res = AgentResult(messages=messages)

    def call(with_tools: bool) -> dict:
        body = {"model": model, "messages": messages, "temperature": temperature, "max_tokens": max_tokens,
                "usage": {"include": True}}
        if with_tools:
            body.update(tools=TOOLS, tool_choice="auto")
        _compact(messages)
        return chat(base_url, key, body)

    def account(payload: dict) -> None:
        usage = payload.get("usage") or {}
        res.prompt_tokens += int(usage.get("prompt_tokens", 0) or 0)
        res.completion_tokens += int(usage.get("completion_tokens", 0) or 0)
        res.cost_usd += _turn_cost(usage, pricing)

    emit("started")
    while True:
        if time.monotonic() - started > timeout_s:
            res.stop = "timeout"
            break
        if res.cost_usd >= budget_usd:
            res.stop = "budget"
            break
        last_turn = res.turns >= max_turns
        if last_turn:
            messages.append({"role": "user", "content": (
                "You are out of turns. Do not call any tool. Reply now with the done-report: Result PARTIAL (or BLOCKED), "
                "what is committed, what is left, and anything you could not verify.")})
        try:
            payload = call(with_tools=not last_turn)
        except ApiError as err:
            emit(f"   ! API error: {one_line(str(err), 200)}")
            res.stop = "error"
            res.text = f"API error: {err}"
            break
        res.turns += 1
        account(payload)
        msg = payload["choices"][0].get("message") or {}
        entry = {"role": "assistant", "content": msg.get("content") or ""}
        if msg.get("tool_calls"):
            entry["tool_calls"] = msg["tool_calls"]
        if msg.get("reasoning_details"):  # some models need their reasoning passed back across tool calls
            entry["reasoning_details"] = msg["reasoning_details"]
        messages.append(entry)
        text = (msg.get("content") or "").strip()
        if text:
            emit(f"t{res.turns} says: {one_line(text, 160)}")
        calls = msg.get("tool_calls") or []
        if not calls or last_turn:
            res.text = text
            res.stop = "max_turns" if last_turn else "done"
            break
        for call_ in calls:
            fn = call_.get("function") or {}
            name, raw = fn.get("name", ""), fn.get("arguments", "")
            emit(f"t{res.turns} {name}: {describe_call(name, raw)}")
            output, is_error = execute_tool(sandbox, name, raw)
            if is_error:
                emit(f"   ! tool error: {one_line(output, 160)}")
            messages.append({"role": "tool", "tool_call_id": call_.get("id", ""), "content": output})
    emit(f"finished: {res.stop} after {res.turns} turns, ${res.cost_usd:.4f}")
    return res
