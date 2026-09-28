#!/usr/bin/env python3
"""Phase 1 gate check (docs/build/PHASES.md) against a running Wondarr.

"A pasted list of 50 songs resolves >= 90 % to MB recordings with correct durations and cover art;
the rest resolve via Deezer or land in an 'unresolved' review state; every song has an album
assignment under the library's policy."

It pastes tests/gate/phase1-songs.txt through POST /api/v1/song/bulk, waits for the command, and
checks every line. A duration is "correct" when it is within --tolerance-ms of the independent
iTunes reference in tests/gate/phase1-reference.json (lines without a reference only need a
duration). Standard library only.

usage: scripts/phase1-gate.py --url http://localhost:1077/wondarr --api-key KEY
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class Api:
    def __init__(self, base: str, key: str) -> None:
        self.base = base.rstrip("/")
        self.key = key

    def call(self, method: str, path: str, body: object | None = None) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("X-Api-Key", self.key)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"FAIL: {method} {path} -> {error.code} {error.read()[:300]!r}") from error
        return json.loads(raw) if raw else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True, help="Wondarr base URL including any URL base")
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--songs", type=Path, default=ROOT / "tests/gate/phase1-songs.txt")
    parser.add_argument("--reference", type=Path, default=ROOT / "tests/gate/phase1-reference.json")
    parser.add_argument("--tolerance-ms", type=int, default=5000)
    parser.add_argument("--timeout-s", type=int, default=900, help="how long the bulk command may take")
    parser.add_argument("--min-mb-ratio", type=float, default=0.90)
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    lines = [line.strip() for line in args.songs.read_text(encoding="utf-8").splitlines() if line.strip()]
    entries = json.loads(args.reference.read_text(encoding="utf-8"))
    reference = {entry["line"]: (entry.get("reference") or {}).get("durationMs") for entry in entries}
    # A line may widen the tolerance when two masterings of one recording differ (see its "note").
    tolerance = {entry["line"]: entry.get("toleranceMs", args.tolerance_ms) for entry in entries}

    started = time.monotonic()
    queued = api.call("POST", "/api/v1/song/bulk", {"text": "\n".join(lines)})
    list_id, command_id = queued["importListId"], queued["commandId"]
    status, message = "queued", ""
    while time.monotonic() - started < args.timeout_s:
        command = api.call("GET", f"/api/v1/command/{command_id}")
        status, message = command["status"], command.get("message") or ""
        if status in ("completed", "failed", "aborted", "cancelled", "orphaned"):
            break
        time.sleep(3)
    elapsed = time.monotonic() - started
    if status != "completed":
        print(f"FAIL: BulkAddSongs ended as '{status}' after {elapsed:.0f} s: {message}")
        return 1
    print(f"ok   BulkAddSongs completed in {elapsed:.0f} s: {message}")

    items = api.call("GET", f"/api/v1/importlistitem?importListId={list_id}&pageSize=1000&sortKey=line")["records"]
    songs: dict[int, dict] = {}
    for item in items:
        if item.get("songId"):
            songs[item["songId"]] = api.call("GET", f"/api/v1/song/{item['songId']}")

    failures: list[str] = []
    mb_good = deezer = unresolved = 0
    print(f"{'line':<58} {'result':<11} {'dur (ref)':<16} album")
    for item in sorted(items, key=lambda x: x["line"]):
        text = item["text"]
        song = songs.get(item.get("songId") or -1)
        ref = reference.get(text)
        if item["state"] == "unresolved":
            unresolved += 1
            print(f"{text[:57]:<58} {'unresolved':<11} {'':<16} {item.get('reason') or ''}")
            continue
        if song is None:
            failures.append(f"line {item['line']} '{text}': state {item['state']} without a song")
            continue
        context = song.get("albumContext")
        if context is None:
            failures.append(f"'{text}': no album assignment")
        duration = song.get("durationMs")
        duration_ok = duration is not None and (ref is None or abs(duration - ref) <= tolerance.get(text, args.tolerance_ms))
        cover_ok = bool(context and context.get("coverUrl"))
        kind = "MB" if song.get("mbRecordingId") else "Deezer"
        if kind == "MB" and duration_ok and cover_ok:
            mb_good += 1
        elif kind == "Deezer":
            deezer += 1
        dur = f"{(duration or 0) / 1000:.0f}s ({(ref or 0) / 1000:.0f}s)" if ref else f"{(duration or 0) / 1000:.0f}s"
        flag = "" if duration_ok and cover_ok else (" DURATION" if not duration_ok else "") + (" NO-COVER" if not cover_ok else "")
        album = f"{context['kind']}: {context['albumTitle']}" if context else "-"
        print(f"{text[:57]:<58} {kind + flag:<11} {dur:<16} {album}")

    total = len(lines)
    ratio = mb_good / total if total else 0
    print(f"\nMB with correct duration and cover: {mb_good}/{total} ({ratio:.0%}); Deezer only: {deezer}; unresolved: {unresolved}")
    if len(items) != total:
        failures.append(f"{len(items)} import items for {total} lines")
    if ratio < args.min_mb_ratio:
        failures.append(f"only {ratio:.0%} resolved to MusicBrainz with correct duration and cover (need {args.min_mb_ratio:.0%})")
    for failure in failures:
        print(f"FAIL: {failure}")
    if failures:
        return 1
    print("PHASE 1 GATE: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
