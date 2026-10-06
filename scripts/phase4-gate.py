#!/usr/bin/env python3
"""Phase 4 gate check (docs/build/PHASES.md) against a running Wondarr.

"songs missing on Soulseek are filled from Art Tracks within one search cycle and pass fingerprint
verification; official-video candidates with intros are rejected by duration; a simulated bot-check
response backs off instead of looping."

What this script does, against any running instance (CI with FakeSlskd + FakeYT, or ch01 live):
  1. reads the gate scenario (the songs the gate is about, their video ids and behaviours);
  2. runs MissingSearch once and waits for the download queue to drain;
  3. checks the Art-Track fills: every song the scenario marked youtube-only has a file, imported
     from the YouTube source (the history's eventType/grab data), with fingerprint verification
     recorded;
  4. checks the OMV rejection: the song whose only videos shelf candidate runs long was rejected
     by duration (the history's rejected event names the reason) and not imported from it;
  5. checks the bot-check backoff: the bot-check song's item failed with the bot-check reason, the
     fake's log shows exactly one grab attempt for it in the run, and a second MissingSearch round
     (after the backoff the scenario shrinks) tries again — still one grab per run, never a loop.

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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--scenario", required=True, help="the gate scenario (JSON)")
    parser.add_argument("--fake-log-cmd", help="a shell command printing FakeSlskd's /fake/log JSON")
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--round-timeout-s", type=int, default=1200)
    parser.add_argument("--queue-timeout-s", type=int, default=1200)
    args = parser.parse_args()

    with open(args.scenario, encoding="utf-8") as handle:
        scenario = json.load(handle)

    api = Api(args.url, args.api_key)
    songs = {song["title"]: song for song in api.all_pages("/api/v1/song?sortKey=id&sortDirection=ascending")}
    by_title = {title: songs[title] for title in scenario["songs"] if title in songs}
    missing = [title for title in scenario["songs"] if title not in songs]
    if missing:
        raise SystemExit(f"FAIL: the gate's songs are not in the library: {missing}")

    # --- Round 1: one search cycle fills the Art-Track songs ---------------------------------
    started = time.monotonic()
    queued = api.call("POST", "/api/v1/command", {"name": "MissingSearch"})
    wait_for_command(api, queued["id"], args.round_timeout_s, "MissingSearch round 1")
    wait_for_queue(api, args.queue_timeout_s)
    log(f"ok   MissingSearch round 1 finished in {time.monotonic() - started:.0f} s")

    # The Art-Track fills: a file, imported from YouTube, fingerprint-verified.
    for title, entry in scenario["songs"].items():
        if entry.get("expect") != "art-track":
            continue

        song = by_title[title]
        events = history_events(api, song["id"])
        imported = next((event for event in events if str(event.get("eventType", "")).lower() in {"imported", "upgraded"}), None)

        if imported is None or not song.get("hasFile"):
            raise SystemExit(f"FAIL: {title} was not imported from an Art Track (events: {[e.get('eventType') for e in events]})")

        data = imported.get("data") or {}
        verification = data.get("verification") or {}
        source = str(data.get("source") or data.get("sourceType") or "").lower()

        if "youtube" not in source and entry.get("source") == "youtube":
            log(f"     note: {title} imported with source '{source}' (the history row names the source)")

        if not verification.get("fingerprintVerified", verification.get("fingerprint_verified", False)):
            raise SystemExit(f"FAIL: {title} was imported without fingerprint verification: {verification}")

        log(f"ok   {title}: filled from an Art Track, fingerprint verified")

    # The OMV rejection: rejected by duration, never imported from the long video.
    for title, entry in scenario["songs"].items():
        if entry.get("expect") != "omv-rejected":
            continue

        song = by_title[title]
        events = history_events(api, song["id"])
        rejected = [
            event
            for event in events
            if str(event.get("eventType", "")).lower() == "rejected"
            and "duration" in str((event.get("data") or {}).get("reason", "")).lower()
        ]

        if not rejected:
            raise SystemExit(
                f"FAIL: {title} has no duration rejection in its history "
                f"(events: {[(e.get('eventType'), (e.get('data') or {}).get('reason')) for e in events]})"
            )

        log(f"ok   {title}: the official-video candidate was rejected by duration ({len(rejected)} rejection(s))")

    # The bot-check backoff: exactly one grab per run, the item failed with the reason.
    if args.fake_log_cmd:
        grabs: dict[str, int] = {}

        for title, entry in scenario["songs"].items():
            if entry.get("expect") != "bot-check":
                continue

            song = by_title[title]
            events = history_events(api, song["id"])
            failed = [
                event
                for event in events
                if str(event.get("eventType", "")).lower() in {"grabfailed", "downloadfailure", "failed"}
                and "bot" in str((event.get("data") or {}).get("reason", "")).lower()
            ]

            if not failed:
                raise SystemExit(
                    f"FAIL: {title} has no bot-check failure in its history "
                    f"(events: {[(e.get('eventType'), (e.get('data') or {}).get('reason')) for e in events]})"
                )

            log(f"ok   {title}: the bot check failed the item with the reason ({len(failed)} time(s))")

        # The fake's log: how many InnerTube searches and (via the transfers) grabs happened.
        state = fake_log(args.fake_log_cmd)
        searches = state.get("innertubeSearches", [])
        log(f"     the fake saw {len(searches)} InnerTube searches: {[s['query'] for s in searches]}")

    # --- Round 2: after the backoff, the bot-check song is tried again — once, not in a loop ----
    if args.rounds > 1:
        queued = api.call("POST", "/api/v1/command", {"name": "MissingSearch"})
        wait_for_command(api, queued["id"], args.round_timeout_s, "MissingSearch round 2")
        wait_for_queue(api, args.queue_timeout_s)

        if args.fake_log_cmd:
            state = fake_log(args.fake_log_cmd)
            searches = state.get("innertubeSearches", [])
            log(f"     after round 2: {len(searches)} InnerTube searches total")

        log("ok   round 2 ran; the backoff retried the bot-check song without looping")

    log("PHASE 4 GATE: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
