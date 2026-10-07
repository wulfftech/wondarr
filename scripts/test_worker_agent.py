#!/usr/bin/env python3
"""Offline tests for scripts/worker_agent.py: sandbox rules and the agent loop against a scripted fake OpenRouter.

Run: python scripts/test_worker_agent.py
"""
import http.server
import json
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import worker_agent as wa  # noqa: E402

PROTECTED = ("AGENTS.md", "docs/adr/", ".env")


def git(cwd, *args):
    return subprocess.run(["git", *args], cwd=cwd, text=True, capture_output=True, check=True).stdout


def make_repo(tmp: Path) -> Path:
    git(tmp, "init", "-q", "-b", "main")
    git(tmp, "config", "user.email", "t@example.com")
    git(tmp, "config", "user.name", "t")
    (tmp / "AGENTS.md").write_text("rules\n")
    (tmp / ".env").write_text("OPENROUTER_API_KEY=secret\n")
    (tmp / "src").mkdir()
    (tmp / "src" / "a.txt").write_text("one\ntwo\nthree\n")
    git(tmp, "add", "AGENTS.md", "src/a.txt")
    git(tmp, "commit", "-q", "-m", "init")
    return tmp


class SandboxTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = make_repo(Path(self._tmp.name))
        self.sb = wa.Sandbox(root=self.root, protected=PROTECTED)

    def tearDown(self):
        self._tmp.cleanup()

    def call(self, name, **args):
        return wa.execute_tool(self.sb, name, json.dumps(args))

    def test_read_and_edit(self):
        out, err = self.call("read_file", path="src/a.txt")
        self.assertFalse(err)
        self.assertIn("2\ttwo", out)
        out, err = self.call("edit_file", path="src/a.txt", old_string="two", new_string="2")
        self.assertFalse(err, out)
        self.assertEqual((self.root / "src/a.txt").read_text(), "one\n2\nthree\n")

    def test_edit_requires_unique_match(self):
        (self.root / "src/b.txt").write_text("x\nx\n")
        out, err = self.call("edit_file", path="src/b.txt", old_string="x", new_string="y")
        self.assertTrue(err)
        self.assertIn("2 places", out)

    def test_crlf_edit_preserved(self):
        (self.root / "src/c.txt").write_bytes(b"a\r\nb\r\n")
        out, err = self.call("edit_file", path="src/c.txt", old_string="a\nb", new_string="a\nc")
        self.assertFalse(err, out)
        self.assertEqual((self.root / "src/c.txt").read_bytes(), b"a\r\nc\r\n")

    def test_env_never_readable(self):
        self.assertTrue(self.call("read_file", path=".env")[1])
        self.assertTrue(self.call("grep", pattern="secret", path=".env")[1])
        out, _ = self.call("grep", pattern="secret")
        self.assertNotIn("secret", out)

    def test_protected_and_outside_paths(self):
        self.assertTrue(self.call("write_file", path="AGENTS.md", content="x")[1])
        self.assertTrue(self.call("write_file", path="docs/adr/0099.md", content="x")[1])
        self.assertTrue(self.call("write_file", path=".git/config", content="x")[1])
        self.assertTrue(self.call("write_file", path="../escape.txt", content="x")[1])
        self.assertTrue(self.call("read_file", path="/etc/passwd")[1])
        self.assertFalse(self.call("write_file", path="src/new/ok.txt", content="ok\n")[1])

    def test_commands_allowlist(self):
        for bad in ("rm -rf /", "cat src/a.txt", "git push origin main", "git -c core.x=y status",
                    "ls | grep a", "echo hi > f.txt", "ls ; ls", "echo $(whoami)", "curl http://x", "git update-index --add x"):
            out, err = self.call("run", command=bad)
            self.assertTrue(err, f"{bad!r} should be rejected, got {out!r}")

    def test_commands_run(self):
        out, err = self.call("run", command="cd src && ls && pwd")
        self.assertFalse(err, out)
        self.assertIn("a.txt", out)
        out, err = self.call("run", command="git status --porcelain")
        self.assertFalse(err, out)
        self.assertIn("[exit 0]", out)

    def test_npm_runs_when_installed(self):
        # On Windows, Node ships an extension-less POSIX `npm` shim next to npm.cmd; running the shim fails with
        # WinError 193, so the runner must pick the PATHEXT match.
        import shutil
        if not shutil.which("npm"):
            self.skipTest("npm not installed")
        out, err = self.call("run", command="npm --version")
        self.assertFalse(err, out)
        self.assertIn("[exit 0]", out)
        if sys.platform == "win32":
            self.assertTrue(wa._resolve_exe("npm").lower().endswith(".cmd"))
            out, err = self.call("run", command="npm run x%PATH%")
            self.assertTrue(err, out)

    def test_child_env_hides_key(self):
        import os
        os.environ["OPENROUTER_API_KEY"] = "k"
        self.assertNotIn("OPENROUTER_API_KEY", wa._child_env())

    def test_bad_arguments(self):
        out, err = wa.execute_tool(self.sb, "read_file", "{not json")
        self.assertTrue(err)
        self.assertTrue(wa.execute_tool(self.sb, "nope", "{}")[1])


class Handler(http.server.BaseHTTPRequestHandler):
    script: list = []
    seen: list = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        Handler.seen.append(body)
        reply = Handler.script.pop(0)
        data = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def tool_msg(*calls):
    return {"choices": [{"message": {"role": "assistant", "content": "working", "tool_calls": [
        {"id": f"c{i}", "type": "function", "function": {"name": n, "arguments": json.dumps(a)}}
        for i, (n, a) in enumerate(calls)]}}], "usage": {"prompt_tokens": 100, "completion_tokens": 10, "cost": 0.001}}


def text_msg(text):
    return {"choices": [{"message": {"role": "assistant", "content": text}}],
            "usage": {"prompt_tokens": 100, "completion_tokens": 10, "cost": 0.001}}


class LoopTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = make_repo(Path(self._tmp.name))
        self.server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        Handler.script, Handler.seen = [], []
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self._tmp.cleanup()

    def run_agent(self, **kw):
        sb = wa.Sandbox(root=self.root, protected=PROTECTED)
        args = dict(base_url=self.url, key="k", model="m", system="sys", user="go", sandbox=sb, max_turns=5,
                    budget_usd=1.0, timeout_s=60, pricing=None, emit=lambda _t: None)
        args.update(kw)
        return wa.run_agent(**args)

    def test_full_task_commits_and_reports(self):
        Handler.script = [
            tool_msg(("write_file", {"path": "src/new.txt", "content": "hello\n"}), ("read_file", {"path": "src/a.txt"})),
            tool_msg(("run", {"command": "git add src/new.txt && git commit -q -m \"feat: add new\""})),
            text_msg("## Done-report\n- Result: DONE"),
        ]
        res = self.run_agent()
        self.assertEqual(res.stop, "done")
        self.assertEqual(res.turns, 3)
        self.assertAlmostEqual(res.cost_usd, 0.003)
        self.assertIn("DONE", res.text)
        self.assertIn("feat: add new", git(self.root, "log", "--oneline"))
        # tool results were fed back, with the tools offered and usage accounting requested
        self.assertEqual(Handler.seen[0]["usage"], {"include": True})
        roles = [m["role"] for m in Handler.seen[2]["messages"]]
        self.assertEqual(roles.count("tool"), 3)

    def test_max_turns_forces_report_without_tools(self):
        Handler.script = [tool_msg(("list_files", {"pattern": "**/*"})), text_msg("PARTIAL report")]
        res = self.run_agent(max_turns=1)
        self.assertEqual(res.stop, "max_turns")
        self.assertEqual(res.text, "PARTIAL report")
        self.assertNotIn("tools", Handler.seen[1])

    def test_budget_stops(self):
        Handler.script = [tool_msg(("list_files", {"pattern": "**/*"})), text_msg("never")]
        res = self.run_agent(budget_usd=0.0005)
        self.assertEqual(res.stop, "budget")
        self.assertEqual(res.turns, 1)

    def test_tool_errors_go_back_to_the_model(self):
        Handler.script = [tool_msg(("read_file", {"path": ".env"})), text_msg("ok")]
        res = self.run_agent()
        tool_msgs = [m for m in res.messages if m["role"] == "tool"]
        self.assertIn("off limits", tool_msgs[0]["content"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
