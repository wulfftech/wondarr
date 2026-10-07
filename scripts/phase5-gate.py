#!/usr/bin/env python3
"""Phase 5 gate checks (docs/build/PHASES.md) against running Wondarr instances.

"a song imported at MP3-320 with a FLAC cutoff is upgraded when a FLAC appears (old file recycled,
history "upgraded"); a backup restores into a fresh container; external-slskd mode passes the Phase 2
gate unchanged." (The third item is scripts/phase2-gate.py run unchanged against a container in
external mode; see scripts/smoke-test.sh.)

Subcommands:

  upgrade        On an instance whose library holds songs imported at MP3-320 from FakeSlskd (the
                 Phase 2 gate leaves such songs), pick one, give it a profile whose cutoff is FLAC,
                 make a FLAC of it appear on the fake (POST /fake/scenario/files through
                 --fake-add-cmd), run UpgradeSearch and check: the song now holds a FLAC, history
                 has an "upgraded" event naming the old path and the recycled path, the old file is
                 in the recycle bin and gone from the library (checked through --exec-cmd when given),
                 and the upgrade search grabbed nothing else.
  backup-take    Take a manual backup, download it to --zip, and write a snapshot of what a restore
                 must bring back (settings, profiles, libraries, songs, history, the API key) to
                 --snapshot.
  backup-verify  On a FRESH instance: upload --zip as a restore, wait for the restart, and compare
                 the instance with --snapshot. The fresh instance's own API key must stop working
                 and the backed-up key must work.

Standard library only.

usage: scripts/phase5-gate.py upgrade --url URL --api-key KEY --scenario /data/phase2-scenario.json
                              --fake-add-cmd "docker run --rm -i --network container:NAME curlimages/curl:8.11.1
                                              -fsS -X POST -H 'Content-Type: application/json' --data-binary @-
                                              http://127.0.0.1:5030/fake/scenario/files"
                              [--exec-cmd "docker exec NAME"] [--timeout-s 1200]
       scripts/phase5-gate.py backup-take --url URL --api-key KEY --zip backup.zip --snapshot snap.json
       scripts/phase5-gate.py backup-verify --url URL --api-key FRESH_KEY --zip backup.zip --snapshot snap.json
"""

from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module  # noqa: E402

scenario_tools = import_module("phase2-scenario")

ACTIVE_STATES = {"queued", "remotelyQueued", "downloading", "completed", "importing"}


class HttpFailure(Exception):
    def __init__(self, code: int, body: bytes) -> None:
        super().__init__(f"HTTP {code}: {body[:300]!r}")
        self.code = code


class Api:
    def __init__(self, base: str, key: str) -> None:
        self.base = base.rstrip("/")
        self.key = key

    def raw(self, method: str, path: str, data: bytes | None = None, content_type: str | None = None,
            key: str | None = None, timeout: int = 180) -> bytes:
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("X-Api-Key", self.key if key is None else key)
        if content_type:
            request.add_header("Content-Type", content_type)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            raise HttpFailure(error.code, error.read()) from error

    def call(self, method: str, path: str, body: object | None = None) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        try:
            raw = self.raw(method, path, data, "application/json" if data is not None else None)
        except HttpFailure as failure:
            raise SystemExit(f"FAIL: {method} {path} -> {failure}") from failure
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


def ok(message: str) -> None:
    log(f"ok   {message}")


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
        time.sleep(5)
    raise SystemExit(f"FAIL: the download queue did not drain within {timeout_s} s")


def history(api: Api, song_id: int) -> list[dict]:
    events = api.all_pages(f"/api/v1/history?songId={song_id}&sortKey=date&sortDirection=ascending")
    for event in events:
        if isinstance(event.get("data"), str):
            try:
                event["data"] = json.loads(event["data"])
            except json.JSONDecodeError:
                event["data"] = {}
    return events


def quality_ids(api: Api) -> dict[str, int]:
    return {quality["name"]: quality["id"] for quality in api.call("GET", "/api/v1/qualitydefinition")}


def run(command: str, stdin: str | None = None) -> str:
    return subprocess.run(command, shell=True, check=True, capture_output=True, text=True, input=stdin).stdout


def exists_in_container(exec_cmd: str, path: str) -> bool:
    return subprocess.run(f"{exec_cmd} test -e {shlex.quote(path)}", shell=True).returncode == 0


# ----------------------------------------------------------------------------------------- upgrade
def upgrade(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    qualities = quality_ids(api)
    mp3_320, flac = qualities.get("MP3-320"), qualities.get("FLAC")
    if mp3_320 is None or flac is None:
        raise SystemExit(f"FAIL: the quality ladder has no MP3-320/FLAC: {sorted(qualities)}")

    with open(args.scenario, encoding="utf-8") as handle:
        scenario = json.load(handle)
    songs = api.call("GET", "/api/v1/song?page=1&pageSize=1000&sortKey=id&sortDirection=ascending")["records"]

    # A song held at MP3-320 whose scenario entry is a plain MP3 from the gate's ordinary peer.
    def entry_for(song: dict) -> dict | None:
        title = scenario_tools.safe(song["title"])
        for entry in scenario["files"]:
            if entry["username"] == "gatepeer" and entry["path"].endswith(f" - {title}.mp3"):
                return entry
        return None

    target = next((song for song in songs if song.get("hasFile") and song.get("qualityId") == mp3_320
                   and song.get("mbRecordingId") and entry_for(song)), None)
    if target is None:
        raise SystemExit("FAIL: no song held at MP3-320 from the Phase 2 scenario to upgrade")
    song_id = target["id"]
    before = history(api, song_id)
    ok(f"song {song_id} '{target['artistCredit']} - {target['title']}' is held at MP3-320")

    # A profile like the default one with its cutoff at FLAC.
    profiles = api.call("GET", "/api/v1/qualityprofile")
    template = next(profile for profile in profiles if profile["id"] == target["qualityProfileId"])
    name = "Phase 5 gate (FLAC cutoff)"
    profile = next((p for p in profiles if p["name"] == name), None)
    if profile is None:
        body = {key: value for key, value in template.items() if key != "id"}
        body.update({"name": name, "cutoff": flac, "upgradeAllowed": True})
        profile = api.call("POST", "/api/v1/qualityprofile", body)
    api.call("PUT", f"/api/v1/song/{song_id}", {"qualityProfileId": profile["id"]})
    cutoff = api.all_pages("/api/v1/wanted/cutoff")
    if not any(song["id"] == song_id for song in cutoff):
        raise SystemExit("FAIL: the song is not listed under Wanted → Cutoff Unmet after the profile change")
    ok("the song is in Cutoff Unmet under a FLAC-cutoff profile")

    # The FLAC appears: the same recording, from another peer, as a FLAC.
    mp3 = entry_for(target)
    seconds = mp3["length"]
    flac_entry = {
        "username": "flacpeer",
        "path": mp3["path"].replace("@@gatepeer", "@@flacpeer").rsplit(".", 1)[0] + ".flac",
        "length": seconds,
        "hasFreeUploadSlot": True,
        "uploadSpeed": 4_000_000,
        "queueLength": 0,
        "transfer": "ok",
        "sampleRate": 44100,
        "bitDepth": 16,
        "size": seconds * 900 * 125,
        "audio": {"codec": "flac", "durationSeconds": mp3["audio"]["durationSeconds"],
                  "seed": mp3["audio"]["seed"] + 500, "frequency": mp3["audio"]["frequency"]},
        "identity": mp3["identity"],
    }
    added = json.loads(run(args.fake_add_cmd, json.dumps([flac_entry])))
    ok(f"a FLAC of it appeared on the fake network ({added.get('files')} scenario files)")

    queued_before = {item["id"] for item in api.all_pages("/api/v1/queue")}
    command = api.call("POST", "/api/v1/command", {"name": "UpgradeSearch"})
    finished = wait_for_command(api, command["id"], args.timeout_s, "UpgradeSearch")
    log(f"     UpgradeSearch: {finished.get('message') or finished.get('result') or 'done'}")
    wait_for_queue(api, args.timeout_s)

    after = api.call("GET", f"/api/v1/song/{song_id}")
    if after.get("qualityId") != flac:
        events = [event["eventType"] for event in history(api, song_id)[len(before):]]
        raise SystemExit(f"FAIL: the song holds quality {after.get('qualityId')}, not FLAC; new history: {events}")
    ok("the song now holds a FLAC")

    new_events = history(api, song_id)[len(before):]
    upgraded = [event for event in new_events if str(event["eventType"]).lower() == "upgraded"]
    if len(upgraded) != 1:
        raise SystemExit(f"FAIL: expected one 'upgraded' history event, got {[e['eventType'] for e in new_events]}")
    data = upgraded[0]["data"]
    previous, recycled, placed = data.get("previousPath"), data.get("recycledPath"), data.get("path") or data.get("finalPath")
    if not previous or not recycled:
        raise SystemExit(f"FAIL: the 'upgraded' event does not name the old and the recycled file: {data}")
    ok(f"history says 'upgraded': {previous} → recycle bin {recycled}")

    if args.exec_cmd:
        if not exists_in_container(args.exec_cmd, recycled):
            raise SystemExit(f"FAIL: the recycled file is not in the recycle bin: {recycled}")
        if previous != placed and exists_in_container(args.exec_cmd, previous):
            raise SystemExit(f"FAIL: the old file is still in the library: {previous}")
        if placed and not exists_in_container(args.exec_cmd, placed):
            raise SystemExit(f"FAIL: the new file is not where history says: {placed}")
        ok("the old file is in the recycle bin and gone from the library; the FLAC is in place")

    others = [item for item in api.all_pages("/api/v1/queue")
              if item["id"] not in queued_before and item["songId"] != song_id]
    if others:
        raise SystemExit(f"FAIL: the upgrade search grabbed other songs too: {[item['songId'] for item in others]}")
    ok("nothing else was grabbed")
    log("PHASE 5 GATE (upgrade): PASS")


# ------------------------------------------------------------------------------------------ backup
def snapshot(api: Api) -> dict:
    songs = api.call("GET", "/api/v1/song?page=1&pageSize=1000&sortKey=id&sortDirection=ascending")["records"]
    events = api.all_pages("/api/v1/history?sortKey=date&sortDirection=ascending")
    return {
        "apiKey": api.key,
        "soulseek": {key: value for key, value in api.call("GET", "/api/v1/soulseek/settings").items()
                     if key not in {"readOnlyFields"}},
        "profiles": sorted(api.call("GET", "/api/v1/qualityprofile"), key=lambda profile: profile["id"]),
        "libraries": api.call("GET", "/api/v1/library"),
        "songs": [{key: song.get(key) for key in ("id", "title", "artistCredit", "mbRecordingId", "hasFile",
                                                   "qualityId", "qualityProfileId", "monitored")} for song in songs],
        "history": [{key: event.get(key) for key in ("id", "songId", "eventType", "qualityId")} for event in events],
    }


def backup_take(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    created = api.call("POST", "/api/v1/system/backup")
    listed = api.call("GET", "/api/v1/system/backup")
    if not any(str(item["id"]) == str(created["id"]) for item in listed):
        raise SystemExit(f"FAIL: the new backup is not listed: {created}")
    zip_bytes = api.raw("GET", f"/api/v1/system/backup/{created['id']}/download")
    if zip_bytes[:2] != b"PK":
        raise SystemExit("FAIL: the backup download is not a zip")
    with open(args.zip, "wb") as handle:
        handle.write(zip_bytes)
    state = snapshot(api)
    with open(args.snapshot, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(state, handle, indent=2, ensure_ascii=False)
        handle.write("\n")
    ok(f"backup '{created['name']}' taken and downloaded ({len(zip_bytes)} bytes); snapshot of "
       f"{len(state['songs'])} songs, {len(state['history'])} history events")


def multipart(field: str, filename: str, payload: bytes) -> tuple[bytes, str]:
    boundary = uuid.uuid4().hex
    head = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{filename}\"\r\n"
            "Content-Type: application/zip\r\n\r\n").encode("utf-8")
    return head + payload + f"\r\n--{boundary}--\r\n".encode("utf-8"), f"multipart/form-data; boundary={boundary}"


def backup_verify(args: argparse.Namespace) -> None:
    with open(args.snapshot, encoding="utf-8") as handle:
        expected = json.load(handle)
    fresh = Api(args.url, args.api_key)
    if fresh.call("GET", "/api/v1/song?page=1&pageSize=1")["totalRecords"] != 0:
        raise SystemExit("FAIL: the target instance is not fresh (it already has songs)")
    with open(args.zip, "rb") as handle:
        body, content_type = multipart("file", os.path.basename(args.zip), handle.read())
    answer = json.loads(fresh.raw("POST", "/api/v1/system/backup/restore/upload", body, content_type))
    if not answer.get("restartRequired"):
        raise SystemExit(f"FAIL: the restore did not ask for a restart: {answer}")
    ok("restore staged; waiting for the restart")

    # The app stops and s6 starts it again; then the backed-up key is the key.
    restored = Api(args.url, expected["apiKey"])
    deadline = time.monotonic() + args.timeout_s
    time.sleep(5)
    while True:
        try:
            restored.raw("GET", "/api/v1/system/status", timeout=10)
            break
        except (HttpFailure, urllib.error.URLError, ConnectionError, TimeoutError):
            if time.monotonic() > deadline:
                raise SystemExit(f"FAIL: the instance did not come back with the backed-up key within {args.timeout_s} s")
            time.sleep(3)
    try:
        fresh.raw("GET", "/api/v1/system/status", timeout=10)
        raise SystemExit("FAIL: the fresh instance's own API key still works after the restore")
    except HttpFailure as failure:
        if failure.code != 401:
            raise SystemExit(f"FAIL: the old key got {failure.code}, expected 401") from failure
    ok("the instance is back; the backed-up API key works and the fresh one is refused (401)")

    actual = snapshot(restored)
    for section in ("soulseek", "profiles", "libraries", "songs", "history"):
        if actual[section] != expected[section]:
            raise SystemExit(f"FAIL: '{section}' differs after the restore:\n  expected {json.dumps(expected[section])[:600]}"
                             f"\n  actual   {json.dumps(actual[section])[:600]}")
        ok(f"{section}: identical ({len(actual[section]) if isinstance(actual[section], list) else 'settings'})")
    log("PHASE 5 GATE (backup → fresh container): PASS")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("upgrade", "backup-take", "backup-verify"):
        p = sub.add_parser(name)
        p.add_argument("--url", required=True)
        p.add_argument("--api-key", required=True)
        p.add_argument("--timeout-s", type=int, default=1200)
        if name == "upgrade":
            p.add_argument("--scenario", required=True)
            p.add_argument("--fake-add-cmd", required=True)
            p.add_argument("--exec-cmd")
        else:
            p.add_argument("--zip", required=True)
            p.add_argument("--snapshot", required=True)
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")  # the Windows console code page cannot print the arrows
    {"upgrade": upgrade, "backup-take": backup_take, "backup-verify": backup_verify}[args.command](args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
