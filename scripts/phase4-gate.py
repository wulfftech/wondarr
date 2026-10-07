#!/usr/bin/env python3
"""Phase 4 gate check (docs/build/PHASES.md) against a running Wondarr.

"songs missing on Soulseek are filled from Art Tracks within one search cycle and pass fingerprint
verification; official-video candidates with intros are rejected by duration; a simulated bot-check
response backs off instead of looping."

What this script does, against any running instance (CI with FakeSlskd + the fake yt-dlp, or ch01 live):
  1. reads the gate scenario (scripts/phase4-scenario.py: the songs, their videos and behaviours);
  2. runs MissingSearch once and waits for the download queue to drain;
  3. the Art-Track fills: each such song now has a file, imported in that one cycle, with an AcoustID
     and a fingerprint score in its "imported" history event;
  4. the OMV case: the song has no file, and an interactive search shows its official video (20 s of
     intro) rejected by duration;
  5. the bot check: exactly one failed queue item for the song, failed with the bot-check reason, and
     no file; a second MissingSearch round right after leaves it alone (the song's backoff owns the
     retry) — one grab, never a loop.

Standard library only.

usage: scripts/phase4-gate.py --url http://localhost:1077 --api-key KEY
       --scenario tests/gate/phase4-scenario.json
       [--fake-log-cmd "docker run --rm --network container:wondarr-smoke curlimages/curl:8.11.1
                        -fsS http://127.0.0.1:5030/fake/log"]
       [--rounds 2] [--round-timeout-s 1200] [--queue-timeout-s 1200]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

ACTIVE_STATES = {"queued", "remotelyQueued", "downloading", "completed", "importing"}


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
            with urllib.request.urlopen(request, timeout=180) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
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


def wait_for_queue(api: Api, timeout_s: int) -> None:
    deadline = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < deadline:
        active = [item for item in api.all_pages("/api/v1/queue") if item["state"] in ACTIVE_STATES]
        summary: dict[str, int] = {}
        for item in active:
            summary[item["state"]] = summary.get(item["state"], 0) + 1
        if summary != last:
            log(f"     queue: {summary or 'empty'}")
            last = summary
        if not active:
            return
        time.sleep(10)
    raise SystemExit(f"FAIL: the download queue did not drain within {timeout_s} s")


def fake_log(command: str) -> dict:
    return json.loads(subprocess.run(command, shell=True, check=True, capture_output=True, text=True).stdout)


def history_events(api: Api, song_id: int) -> list[dict]:
    events = api.all_pages(f"/api/v1/history?songId={song_id}&sortKey=date&sortDirection=ascending")
    for event in events:
        data = event.get("data")
        if isinstance(data, str):
            try:
                event["data"] = json.loads(data)
            except json.JSONDecodeError:
                event["data"] = {}
    return events


def song_now(api: Api, song_id: int) -> dict:
    return api.call("GET", f"/api/v1/song/{song_id}")


def queue_items(api: Api, song_id: int) -> list[dict]:
    items = api.call("GET", "/api/v1/queue?includeFinished=true&page=1&pageSize=1000")
    records = items["records"] if isinstance(items, dict) else items
    return [item for item in records if item.get("songId") == song_id]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--scenario", required=True, help="the gate scenario (JSON)")
    parser.add_argument("--fake-log-cmd", help="prints the fake's /fake/log (JSON), for the InnerTube search count")
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--round-timeout-s", type=int, default=1200)
    parser.add_argument("--queue-timeout-s", type=int, default=1200)
    args = parser.parse_args()

    with open(args.scenario, encoding="utf-8") as handle:
        scenario = json.load(handle)

    api = Api(args.url, args.api_key)
    songs = {song["title"]: song for song in api.all_pages("/api/v1/song?sortKey=id&sortDirection=ascending")}
    absent = [title for title in scenario["songs"] if title not in songs]
    if absent:
        raise SystemExit(f"FAIL: the gate's songs are not in the library: {absent}")
    ids = {title: songs[title]["id"] for title in scenario["songs"]}
    with_file = [title for title in scenario["songs"] if songs[title].get("hasFile")]
    if with_file:
        raise SystemExit(f"FAIL: the gate's songs must start without a file: {with_file}")

    # --- Round 1: one search cycle ---------------------------------------------------------------
    started = time.monotonic()
    queued = api.call("POST", "/api/v1/command", {"name": "MissingSearch"})
    wait_for_command(api, queued["id"], args.round_timeout_s, "MissingSearch round 1")
    wait_for_queue(api, args.queue_timeout_s)
    log(f"ok   MissingSearch round 1 finished in {time.monotonic() - started:.0f} s")

    for title, entry in scenario["songs"].items():
        song = song_now(api, ids[title])
        events = history_events(api, song["id"])
        kinds = [str(event.get("eventType", "")).lower() for event in events]

        if entry["expect"] == "art-track":
            # Filled from the Art Track within this one cycle, verified by fingerprint.
            imported = next((event for event in events if str(event.get("eventType", "")).lower() == "imported"), None)
            if imported is None or not song.get("hasFile"):
                raise SystemExit(f"FAIL: {title} was not filled from its Art Track (events: {kinds})")
            data = imported.get("data") or {}
            if not data.get("acoustId") or data.get("fingerprintScore") is None:
                raise SystemExit(f"FAIL: {title} was imported without fingerprint verification: {data}")
            log(f"ok   {title}: filled from an Art Track in one cycle, fingerprint verified (score {data['fingerprintScore']})")

        elif entry["expect"] == "omv-rejected":
            # Never imported, and the official video is rejected for its length: an interactive search
            # shows the decision (the automatic one keeps its candidates out of history).
            if song.get("hasFile"):
                raise SystemExit(f"FAIL: {title} was imported, but its only candidate is the long official video")
            search = api.call("GET", f"/api/v1/release?songId={song['id']}")
            omv = [release for release in search.get("releases", [])
                   if entry["omvVideoId"] in str(release.get("remotePath", ""))]
            if not omv:
                raise SystemExit(f"FAIL: {title}: the official video was not offered as a candidate "
                                 f"(releases: {[r.get('remotePath') for r in search.get('releases', [])]})")
            reasons = json.dumps(omv[0].get("rejections", [])).lower()
            if omv[0].get("accepted") or "duration" not in reasons:
                raise SystemExit(f"FAIL: {title}: the official video was not rejected by duration: {omv[0].get('rejections')}")
            log(f"ok   {title}: the official video with an intro was rejected by duration")

        elif entry["expect"] == "bot-check":
            failed = [item for item in queue_items(api, song["id"]) if str(item.get("state", "")).lower() == "failed"]
            if len(failed) != 1 or "bot" not in str(failed[0].get("message", "")).lower():
                raise SystemExit(f"FAIL: {title}: expected exactly one item failed by the bot check, got "
                                 f"{[(item.get('state'), item.get('message')) for item in queue_items(api, song['id'])]}")
            if song.get("hasFile"):
                raise SystemExit(f"FAIL: {title} has a file although its only download answers the bot check")
            log(f"ok   {title}: the bot check failed its one grab with the reason, no retry in the same run")

    if args.fake_log_cmd:
        searches = fake_log(args.fake_log_cmd).get("innertubeSearches", [])
        log(f"     the fake InnerTube saw {len(searches)} searches")

    # --- Round 2: the bot check backs off, it does not loop ------------------------------------------
    if args.rounds > 1:
        queued = api.call("POST", "/api/v1/command", {"name": "MissingSearch"})
        wait_for_command(api, queued["id"], args.round_timeout_s, "MissingSearch round 2")
        wait_for_queue(api, args.queue_timeout_s)
        for title, entry in scenario["songs"].items():
            if entry["expect"] != "bot-check":
                continue
            failed = [item for item in queue_items(api, ids[title]) if str(item.get("state", "")).lower() == "failed"]
            if len(failed) != 1:
                raise SystemExit(f"FAIL: {title}: a second round grabbed again inside the backoff ({len(failed)} failed items)")
            log(f"ok   {title}: round 2 left it alone — the song's backoff owns the retry, nothing loops")

    log("PHASE 4 GATE: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
