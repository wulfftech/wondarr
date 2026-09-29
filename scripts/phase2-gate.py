#!/usr/bin/env python3
"""Phase 2 gate check (docs/build/PHASES.md) against a running Wondarr.

"100 wanted songs -> >= 80 % imported automatically at or above cutoff with zero wrong-recording
imports in a manual audit of 30 files; a deliberately wrong file (live version) is caught and the
next candidate tried without user action; the search budget is never exceeded; toggling 'Share my
library' changes slskd's shares."

What this script does, against any running instance (CI with FakeSlskd, or ch01 with real Soulseek):
  1. optionally pastes a song list (POST /api/v1/song/bulk) and waits for it;
  2. runs MissingSearch until no wanted song is left untried (or --rounds is reached) and waits for
     the download queue to drain;
  3. reports the share of songs imported at or above their profile's cutoff (Wanted -> Missing and
     Cutoff Unmet are what is left);
  4. lists every song that had a download rejected after verification and was then imported from
     another candidate (the "wrong file caught, next candidate tried" evidence);
  5. checks the Soulseek search budget from a list of search submission times — FakeSlskd's
     /fake/log in CI (--fake-log), or the app's own log lines on a real instance (--app-log);
  6. toggles "Share my library" off and on through /api/v1/soulseek/settings and checks that the
     shares slskd reports follow (--share-toggle);
  7. writes an audit sample (--audit N random imported files with path, quality, AcoustID and the
     verification reason) for the manual audit.

Standard library only.

usage: scripts/phase2-gate.py --url http://localhost:1077 --api-key KEY [--songs tests/gate/phase2-songs.txt]
       [--fake-log http://127.0.0.1:5030/fake/log | --app-log wondarr.log] [--share-toggle] [--audit 30]
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import random
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ACTIVE_STATES = {"queued", "remotelyQueued", "downloading", "completed", "importing"}


class Api:
    def __init__(self, base: str, key: str) -> None:
        self.base = base.rstrip("/")
        self.key = key

    def call(self, method: str, path: str, body: object | None = None, allow: tuple[int, ...] = ()) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("X-Api-Key", self.key)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            if error.code in allow:
                return None
            raise SystemExit(f"FAIL: {method} {path} -> {error.code} {error.read()[:300]!r}") from error
        return json.loads(raw) if raw else None

    def all_pages(self, path: str) -> list[dict]:
        records: list[dict] = []
        page = 1
        separator = "&" if "?" in path else "?"
        while True:
            result = self.call("GET", f"{path}{separator}page={page}&pageSize=250")
            batch = result["records"]
            records.extend(batch)
            if len(records) >= result["totalRecords"] or not batch:
                return records
            page += 1


def log(message: str) -> None:
    print(message, flush=True)


def wait_for_command(api: Api, command_id: int, timeout_s: int, label: str) -> dict:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        command = api.call("GET", f"/api/v1/command/{command_id}")
        status = str(command.get("status", "")).lower()
        if status in {"completed", "failed", "aborted", "cancelled", "orphaned"}:
            if status != "completed":
                raise SystemExit(f"FAIL: {label} ended {status}: {command.get('message')}")
            return command
        time.sleep(3)
    raise SystemExit(f"FAIL: {label} did not finish within {timeout_s} s")


def active_queue(api: Api) -> list[dict]:
    return [item for item in api.all_pages("/api/v1/queue") if item["state"] in ACTIVE_STATES]


def wait_for_queue(api: Api, timeout_s: int) -> None:
    deadline = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < deadline:
        active = active_queue(api)
        summary = {}
        for item in active:
            summary[item["state"]] = summary.get(item["state"], 0) + 1
        if summary != last:
            log(f"     queue: {summary or 'empty'}")
            last = summary
        if not active:
            return
        time.sleep(10)
    raise SystemExit(f"FAIL: the download queue did not drain within {timeout_s} s")


def paste(api: Api, songs: Path, timeout_s: int) -> None:
    lines = [line.strip() for line in songs.read_text(encoding="utf-8").splitlines() if line.strip()]
    queued = api.call("POST", "/api/v1/song/bulk", {"text": "\n".join(lines)})
    wait_for_command(api, queued["commandId"], timeout_s, "BulkAddSongs")
    log(f"ok   pasted {len(lines)} songs")


def run_missing_search(api: Api, rounds: int, round_timeout_s: int, queue_timeout_s: int) -> None:
    for round_no in range(1, rounds + 1):
        missing_before = api.call("GET", "/api/v1/wanted/missing?page=1&pageSize=1")["totalRecords"]
        if missing_before == 0:
            break
        queued = api.call("POST", "/api/v1/command", {"name": "MissingSearch"})
        command = wait_for_command(api, queued["id"], round_timeout_s, f"MissingSearch round {round_no}")
        log(f"ok   MissingSearch round {round_no}: {command.get('message')}")
        wait_for_queue(api, queue_timeout_s)
        missing_after = api.call("GET", "/api/v1/wanted/missing?page=1&pageSize=1")["totalRecords"]
        if missing_after >= missing_before:
            break


def imported_ratio(api: Api, songs_total: int) -> tuple[int, int, int]:
    missing = api.call("GET", "/api/v1/wanted/missing?page=1&pageSize=1")["totalRecords"]
    cutoff = api.call("GET", "/api/v1/wanted/cutoff?page=1&pageSize=1")["totalRecords"]
    return songs_total - missing - cutoff, missing, cutoff


def caught_wrong_files(api: Api) -> list[tuple[str, str]]:
    history = api.all_pages("/api/v1/history?sortKey=date&sortDirection=ascending")
    rejected: dict[int, str] = {}
    caught = []
    for event in history:
        kind = str(event.get("eventType", "")).lower()
        data = event.get("data") or {}
        if isinstance(data, str):
            try:
                data = json.loads(data)
            except json.JSONDecodeError:
                data = {}
        if kind == "rejected":
            rejected[event["songId"]] = data.get("reason") or "?"
        elif kind in {"imported", "upgraded"} and event["songId"] in rejected:
            caught.append((str(event["songId"]), rejected.pop(event["songId"])))
    return caught


def parse_time(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def budget_from_fake(url: str | None, command: str | None) -> tuple[list[dt.datetime], int]:
    if command:
        # The fake listens on the container's loopback; the smoke test reads it through a helper container.
        import subprocess
        data = json.loads(subprocess.run(command, shell=True, check=True, capture_output=True, text=True).stdout)
    else:
        with urllib.request.urlopen(url, timeout=30) as response:
            data = json.loads(response.read())
    return sorted(parse_time(s["postedAt"]) for s in data["searches"]), int(data.get("maxInFlight", 0))


SEARCH_LINE = re.compile(r"Soulseek search .* after (?P<ms>\d+) ms")


def budget_from_app_log(path: Path) -> tuple[list[dt.datetime], int]:
    """Submission ~= the log line's timestamp minus the search's elapsed time (the runner logs once per search)."""
    times = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        match = SEARCH_LINE.search(str(entry.get("message", "")))
        if match:
            times.append(parse_time(entry["timestamp"]) - dt.timedelta(milliseconds=int(match["ms"])))
    return sorted(times), -1


def check_budget(times: list[dt.datetime], max_in_flight: int, slack_s: float) -> None:
    window = dt.timedelta(seconds=240)
    worst = 0
    for i, start in enumerate(times):
        count = sum(1 for other in times[i:] if other - start <= window)
        worst = max(worst, count)
    gaps = [(b - a).total_seconds() for a, b in zip(times, times[1:])]
    min_gap = min(gaps) if gaps else None
    log(f"     {len(times)} searches; max {worst} in any 240 s; min spacing {min_gap if min_gap is not None else '-'} s; max in flight {max_in_flight if max_in_flight >= 0 else 'n/a'}")
    if worst > 30:
        raise SystemExit("FAIL: more than 30 searches in a 240 s window")
    if min_gap is not None and min_gap < 5 - slack_s:
        raise SystemExit(f"FAIL: two searches only {min_gap:.3f} s apart")
    if max_in_flight > 2:
        raise SystemExit(f"FAIL: {max_in_flight} searches in flight at once")


def share_toggle(api: Api, timeout_s: int) -> None:
    def shared_directories() -> int | None:
        status = api.call("GET", "/api/v1/soulseek/status")
        return (status.get("sharing") or {}).get("directories")

    settings = api.call("GET", "/api/v1/soulseek/settings")
    if "shareLibrary" in settings.get("readOnlyFields", []):
        raise SystemExit("FAIL: shareLibrary is set by the environment; cannot toggle it")
    original = settings["shareLibrary"]
    for wanted in (not original, original):
        api.call("PUT", "/api/v1/soulseek/settings", {"shareLibrary": wanted})
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            status = api.call("GET", "/api/v1/soulseek/status")
            directories = (status.get("sharing") or {}).get("directories")
            logged_in = status.get("loggedIn")
            if logged_in and ((wanted and directories not in (None, 0)) or (not wanted and directories == 0)):
                log(f"ok   shareLibrary={wanted}: slskd shares {directories} folder(s)")
                break
            time.sleep(3)
        else:
            raise SystemExit(f"FAIL: slskd shares did not follow shareLibrary={wanted} within {timeout_s} s (last: {shared_directories()})")


def audit_sample(api: Api, count: int, out: Path) -> None:
    songs = [s for s in api.all_pages("/api/v1/song") if s.get("qualityId")]
    sample = random.Random(20260929).sample(songs, min(count, len(songs)))
    history = api.all_pages("/api/v1/history?eventType=imported")
    by_song: dict[int, dict] = {}
    for event in history:
        by_song[event["songId"]] = event
    rows = []
    for song in sample:
        event = by_song.get(song["id"], {})
        data = event.get("data") or {}
        if isinstance(data, str):
            data = json.loads(data or "{}")
        rows.append({
            "songId": song["id"],
            "song": f"{song.get('artistCredit')} - {song.get('title')}",
            "mbRecordingId": song.get("mbRecordingId"),
            "durationMs": song.get("durationMs"),
            "qualityId": song.get("qualityId"),
            "path": data.get("path"),
            "verification": data.get("verification"),
            "acoustId": data.get("acoustId"),
            "fingerprintScore": data.get("fingerprintScore"),
        })
    out.write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    log(f"ok   audit sample of {len(rows)} files written to {out}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--songs", type=Path, help="paste this list first (one 'Artist - Title' per line)")
    parser.add_argument("--rounds", type=int, default=3, help="MissingSearch rounds at most")
    parser.add_argument("--round-timeout-s", type=int, default=3600)
    parser.add_argument("--queue-timeout-s", type=int, default=3600)
    parser.add_argument("--min-ratio", type=float, default=0.80)
    parser.add_argument("--expect-caught", type=int, default=0, help="at least this many wrong files caught and replaced")
    parser.add_argument("--fake-log", help="FakeSlskd /fake/log URL (CI)")
    parser.add_argument("--fake-log-cmd", help="a shell command printing FakeSlskd's /fake/log JSON (when the URL is not reachable)")
    parser.add_argument("--app-log", type=Path, help="the app's JSON log file (real slskd)")
    parser.add_argument("--share-toggle", action="store_true")
    parser.add_argument("--audit", type=int, default=0)
    parser.add_argument("--audit-out", type=Path, default=Path("phase2-audit.json"))
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    if args.songs:
        paste(api, args.songs, args.round_timeout_s)

    songs_total = api.call("GET", "/api/v1/song?page=1&pageSize=1")["totalRecords"]
    started = time.monotonic()
    run_missing_search(api, args.rounds, args.round_timeout_s, args.queue_timeout_s)
    imported, missing, cutoff = imported_ratio(api, songs_total)
    ratio = imported / songs_total if songs_total else 0
    log(f"     {imported}/{songs_total} imported at or above cutoff ({ratio:.0%}); missing {missing}; below cutoff {cutoff}; {time.monotonic() - started:.0f} s")
    if ratio < args.min_ratio:
        raise SystemExit(f"FAIL: {ratio:.0%} imported at or above cutoff (< {args.min_ratio:.0%})")
    log(f"ok   {ratio:.0%} of wanted songs imported at or above cutoff")

    caught = caught_wrong_files(api)
    for song_id, reason in caught:
        log(f"     song {song_id}: rejected after download ({reason}), then imported from another candidate")
    if len(caught) < args.expect_caught:
        raise SystemExit(f"FAIL: expected at least {args.expect_caught} wrong file(s) caught, saw {len(caught)}")
    log(f"ok   {len(caught)} wrong file(s) caught and replaced without user action")

    if args.fake_log or args.fake_log_cmd or args.app_log:
        fake = bool(args.fake_log or args.fake_log_cmd)
        times, in_flight = budget_from_fake(args.fake_log, args.fake_log_cmd) if fake else budget_from_app_log(args.app_log)
        # The budget spaces submissions when it hands out the slot; the fake stamps them when the POST
        # arrives, a few ms later and with jitter, so allow 0.1 s there (0.5 s for log timestamps).
        check_budget(times, in_flight, slack_s=0.1 if fake else 0.5)
        log("ok   the Soulseek search budget held (<= 30 per 240 s, >= 5 s apart, <= 2 in flight)")

    if args.share_toggle:
        share_toggle(api, timeout_s=180)

    if args.audit:
        audit_sample(api, args.audit, args.audit_out)

    log("PHASE 2 GATE: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
