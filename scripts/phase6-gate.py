#!/usr/bin/env python3
"""Phase 6 gate checks (docs/build/PHASES.md) against a running Wondarr instance.

"a 200-track Exportify CSV imports, resolves via ISRC >= 95 %, downloads, and appears as a Plex
playlist after two search cycles; an album added by search arrives as its tracks under that album; a
song moved to a second library lands in that library's folder and Plex section; a library set to
convert lossless to MP3 imports a FLAC as an MP3 still ranked FLAC, and an on-demand conversion
replaces an existing file with the original in the recycle bin."

Subcommands (run in this order on one instance; each prints "ok ..." lines and exits non-zero on a
failed check):

  plex       Sign in to the fake Plex (FakeSlskd's stub, or a real server) and link library 1 to
             section --section.
  lists      Create a CSV import list from --csv (Exportify's columns) with the Plex playlist and the
             .m3u8 on, sync it, and check that >= --min-resolved of its rows became songs whose ISRCs
             include the row's ISRC. Then offer every song on the fake (--fake-add-cmd), keep only the
             first --download songs (in list order) monitored, run MissingSearch twice ("two search
             cycles"), wait for the queue, and check that they were imported. Then sync the list again
             and check the Plex playlist (through --plex-state-cmd) holds exactly the imported songs in
             the list's order, and the .m3u8 lists them (through --exec-cmd). Finally re-upload the
             CSV with its first row dropped and its last two rows swapped and check the playlist is
             updated IN PLACE (same key, new order).
  album      Look up --album ("Artist - Album"), take the default release, add its tracks, and check
             every track became a song filed under that release, pinned.
  library    Create a second library (--root2, section --section2), move one imported song there and
             check its file is under --root2, the song's library changed, and Plex was asked to scan
             section --section2.
  convert    Give library 1 a "lossless -> MP3 320" rule, offer a FLAC of one more song on the fake,
             search it, and check the song holds an MP3 still ranked FLAC; then convert another
             imported song on demand to AAC and check its file is now .m4a and the original is in the
             recycle bin (through --exec-cmd).

Standard library only.

usage: scripts/phase6-gate.py plex    --url URL --api-key KEY [--plex-url http://127.0.0.1:5033] [--section 1]
       scripts/phase6-gate.py lists   --url URL --api-key KEY --csv tests/gate/phase6-exportify.csv
                                      --fake-add-cmd "docker exec -i NAME curl ... /fake/scenario/files"
                                      --plex-state-cmd "docker exec NAME curl -fsS http://127.0.0.1:5033/fake/plex"
                                      --exec-cmd "docker exec NAME" [--download 20] [--min-resolved 0.95]
       scripts/phase6-gate.py album   --url URL --api-key KEY --album "Queen - A Night at the Opera"
       scripts/phase6-gate.py library --url URL --api-key KEY --root2 /data/music2 --section2 2
                                      --plex-state-cmd ... --exec-cmd ...
       scripts/phase6-gate.py convert --url URL --api-key KEY --fake-add-cmd ... --exec-cmd ...
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import os
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module  # noqa: E402

scenario_tools = import_module("phase2-scenario")

ACTIVE_STATES = {"queued", "remotelyQueued", "downloading", "completed", "importing"}
LIST_NAME = "Phase 6 gate"


class HttpFailure(Exception):
    def __init__(self, code: int, body: bytes) -> None:
        super().__init__(f"HTTP {code}: {body[:300]!r}")
        self.code = code


class Api:
    def __init__(self, base: str, key: str) -> None:
        self.base = base.rstrip("/")
        self.key = key

    def call(self, method: str, path: str, body: object | None = None, timeout: int = 300) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("X-Api-Key", self.key)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"FAIL: {method} {path} -> {error.code} {error.read()[:400]!r}") from error
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


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


def run(command: str, stdin: str | None = None) -> str:
    return subprocess.run(command, shell=True, check=True, capture_output=True, text=True,
                          input=stdin, encoding="utf-8").stdout


def wait_for_command(api: Api, command_id: int, timeout_s: int, label: str) -> dict:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        command = api.call("GET", f"/api/v1/command/{command_id}")
        status = str(command.get("status", "")).lower()
        if status in {"completed", "failed", "aborted", "cancelled", "orphaned"}:
            if status != "completed":
                fail(f"{label} ended {status}: {command.get('message')}")
            return command
        time.sleep(3)
    fail(f"{label} did not finish within {timeout_s} s")
    return {}


def run_command(api: Api, name: str, timeout_s: int, **body: object) -> dict:
    command = api.call("POST", "/api/v1/command", {"name": name, **body})
    finished = wait_for_command(api, command["id"], timeout_s, name)
    log(f"     {name}: {finished.get('message') or 'done'}")
    return finished


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
    fail(f"the download queue did not drain within {timeout_s} s")


def song(api: Api, song_id: int) -> dict:
    return api.call("GET", f"/api/v1/song/{song_id}")


def file_path(record: dict) -> str | None:
    file = record.get("file")
    return file.get("path") if isinstance(file, dict) else None


def history(api: Api, song_id: int) -> list[dict]:
    events = api.all_pages(f"/api/v1/history?songId={song_id}&sortKey=date&sortDirection=ascending")
    for event in events:
        if isinstance(event.get("data"), str):
            try:
                event["data"] = json.loads(event["data"])
            except json.JSONDecodeError:
                event["data"] = {}
    return events


def plex_state(command: str) -> dict:
    return json.loads(run(command))


def exists(exec_cmd: str, path: str) -> bool:
    return subprocess.run(f"{exec_cmd} test -e {shlex.quote(path)}", shell=True).returncode == 0


def offer(api: Api, fake_add_cmd: str, songs: list[dict], codec: str = "mp3", seed_base: int = 6000) -> None:
    entries = []
    for index, record in enumerate(songs):
        entries.append(scenario_tools.file_entry(
            record, index + 1, user=f"gate6-{index % 7}", codec=codec, seed=seed_base + record["id"],
            free=True, speed=5_000_000, queue=0, identity=scenario_tools.identity_of(record)))
    for start in range(0, len(entries), 50):
        run(fake_add_cmd, json.dumps(entries[start:start + 50]))


# -------------------------------------------------------------------------------------------- plex
def plex(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    api.call("PUT", "/api/v1/plex/token", {"token": "gate-plex-token"})
    api.call("PUT", "/api/v1/plex/server", {"serverUrl": args.plex_url})
    test = api.call("POST", "/api/v1/plex/test")
    if not test.get("ok"):
        fail(f"the Plex connection test failed: {test}")
    library = api.call("GET", "/api/v1/library/1")
    library["plexSectionId"] = args.section
    library["plexLibraryPath"] = None
    api.call("PUT", "/api/v1/library/1", library)
    ok(f"signed in to {args.plex_url} and linked library 1 to section {args.section}")


# ------------------------------------------------------------------------------------------- lists
def read_csv(path: str) -> list[dict]:
    with open(path, encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def write_csv(rows: list[dict]) -> str:
    buffer = io.StringIO()
    writer = csv.DictWriter(buffer, fieldnames=list(rows[0].keys()), quoting=csv.QUOTE_ALL, lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    return buffer.getvalue()


def list_items(api: Api, list_id: int) -> list[dict]:
    items = api.all_pages(f"/api/v1/importlistitem?importListId={list_id}&sortKey=line")
    return [item for item in items if not item.get("removed")]


def check_playlist(api: Api, args: argparse.Namespace, list_id: int, expected_paths: list[str], label: str) -> str:
    state = plex_state(args.plex_state_cmd)
    playlists = [playlist for playlist in state["playlists"] if playlist["title"] == LIST_NAME]
    if len(playlists) != 1:
        fail(f"{label}: expected one Plex playlist named {LIST_NAME!r}, found {len(playlists)}")
    files = [item["file"] for item in playlists[0]["items"]]
    if files != expected_paths:
        fail(f"{label}: the Plex playlist holds {len(files)} tracks {files[:5]}…, expected {len(expected_paths)} {expected_paths[:5]}…")
    ok(f"{label}: the Plex playlist {playlists[0]['key']} holds the {len(files)} imported songs in the list's order")
    return playlists[0]["key"]


def lists(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    rows = read_csv(args.csv)
    if len(rows) < 200:
        fail(f"{args.csv} has {len(rows)} rows; the gate needs a 200-track export")
    text = write_csv(rows)

    preview = api.call("POST", "/api/v1/importlist/csv/preview", {"sourceText": text})
    if preview["format"] != "exportify" or preview["rowCount"] != len(rows) or preview["problems"]:
        fail(f"the CSV preview reads {preview['format']}, {preview['rowCount']} rows, problems {preview['problems']}")
    ok(f"the CSV reads as an Exportify export of {preview['rowCount']} rows")

    created = api.call("POST", "/api/v1/importlist", {
        "type": "csv", "name": LIST_NAME, "settings": {}, "sourceText": text, "policy": "AddOnly",
        "syncIntervalHours": 0, "plexPlaylist": True, "m3uExport": True,
    })
    list_id = created["id"]
    accepted = api.call("POST", f"/api/v1/importlist/{list_id}/sync")
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "ImportListSync")
    log(f"     sync: {finished.get('message')}")

    items = list_items(api, list_id)
    by_isrc = {row["ISRC"].upper(): row for row in rows}
    via_isrc = 0
    songs_in_order: list[dict] = []
    seen_songs: set[int] = set()
    for item in items:
        if item["state"] != "added" or item.get("songId") is None:
            continue
        record = song(api, item["songId"])
        line = rows[item["line"] - 1]
        if line["ISRC"].upper() in {isrc.upper() for isrc in record.get("isrcs") or []}:
            via_isrc += 1
        if record["id"] not in seen_songs:
            seen_songs.add(record["id"])
            songs_in_order.append(record)
    share = via_isrc / len(rows)
    if share < args.min_resolved:
        fail(f"{via_isrc} of {len(rows)} rows ({share:.0%}) resolved to a song carrying the row's ISRC; the gate wants >= {args.min_resolved:.0%}")
    ok(f"{via_isrc} of {len(rows)} rows ({share:.1%}) resolved via their ISRC")
    unresolved = [item for item in items if item["state"] == "unresolved"]
    log(f"     {len(unresolved)} rows wait in the review screen")

    # Downloads: every song is on offer; the first --download (in list order) stay wanted.
    offer(api, args.fake_add_cmd, songs_in_order)
    wanted = songs_in_order[:args.download]
    for record in songs_in_order[args.download:]:
        api.call("PUT", f"/api/v1/song/{record['id']}", {"monitored": False})
    for cycle in (1, 2):
        run_command(api, "MissingSearch", args.timeout_s)
        wait_for_queue(api, args.timeout_s)
        log(f"     search cycle {cycle} done")

    imported = []
    for record in wanted:
        current = song(api, record["id"])
        if file_path(current):
            imported.append(current)
    if len(imported) < len(wanted):
        missing = [record["title"] for record in wanted if record["id"] not in {r["id"] for r in imported}]
        fail(f"{len(imported)} of {len(wanted)} wanted songs were imported after two search cycles; missing {missing}")
    ok(f"all {len(wanted)} wanted songs were downloaded and imported after two search cycles")

    accepted = api.call("POST", f"/api/v1/importlist/{list_id}/sync")
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "ImportListSync")
    log(f"     sync: {finished.get('message')}")
    expected = [file_path(record) for record in imported]
    key = check_playlist(api, args, list_id, expected, "first write")

    m3u = run(f"{args.exec_cmd} cat {shlex.quote(args.root + '/Playlists/' + LIST_NAME + '.m3u8')}")
    entries = [line for line in m3u.splitlines() if line and not line.startswith("#")]
    if len(entries) != len(expected):
        fail(f"the .m3u8 lists {len(entries)} files, expected {len(expected)}")
    ok(f"the .m3u8 lists the {len(entries)} files")

    # Re-upload: the first row dropped, the last two swapped; the playlist must be updated in place.
    changed = rows[1:-2] + [rows[-1], rows[-2]]
    api.call("PUT", f"/api/v1/importlist/{list_id}", {
        "type": "csv", "name": LIST_NAME, "settings": {}, "sourceText": write_csv(changed), "policy": "AddOnly",
        "syncIntervalHours": 0, "plexPlaylist": True, "m3uExport": True,
    })
    accepted = api.call("POST", f"/api/v1/importlist/{list_id}/sync")
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "ImportListSync")
    log(f"     sync: {finished.get('message')}")
    first_isrc = rows[0]["ISRC"].upper()
    kept = [record for record in imported if first_isrc not in {i.upper() for i in record.get("isrcs") or []}]
    key2 = check_playlist(api, args, list_id, [file_path(record) for record in kept], "after the re-upload")
    if key2 != key:
        fail(f"the playlist was made again ({key} -> {key2}) instead of updated in place")
    ok("the playlist was updated in place (same key)")
    with open(args.state_out, "w", encoding="utf-8") as handle:
        json.dump({"listId": list_id, "imported": [record["id"] for record in imported],
                   "spare": [record["id"] for record in songs_in_order[args.download:]]}, handle)


# ------------------------------------------------------------------------------------------- album
def album(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    results = api.call("GET", "/api/v1/album/lookup?term=" + urllib.parse.quote(args.album))
    groups = [result for result in results if result["source"] == "musicbrainz"]
    if not groups:
        fail(f"no MusicBrainz album for {args.album!r}: {results[:3]}")
    group = groups[0]
    releases = api.call("GET", f"/api/v1/album/releasegroup/{group['id']}/releases")
    default = [release for release in releases if release.get("isDefault")]
    if len(default) != 1:
        fail(f"expected one default release of {group['title']}, got {len(default)}")
    release = default[0]
    tracks = api.call("GET", f"/api/v1/album/musicbrainz/{release['id']}/tracks")
    ok(f"{group['title']} ({group.get('year')}): default release {release['id']} with {len(tracks)} tracks")
    accepted = api.call("POST", "/api/v1/album/add", {"source": "musicbrainz", "id": release["id"], "monitored": False})
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "AddAlbum")
    log(f"     AddAlbum: {finished.get('message')}")
    after = api.call("GET", f"/api/v1/album/musicbrainz/{release['id']}/tracks")
    missing = [track["title"] for track in after if not track.get("owned")]
    if missing:
        fail(f"{len(missing)} tracks did not become songs: {missing}")
    # A track the library held before the add (an earlier list may have brought it) keeps its own album.
    owned_before = {track["mbRecordingId"] for track in tracks if track.get("owned")}
    added = [track for track in after if track["mbRecordingId"] not in owned_before]
    pinned = 0
    for track in added:
        record = song(api, track["songId"])
        context = record.get("albumContext") or {}
        if context.get("mbReleaseId") == release["id"] and context.get("pinned"):
            pinned += 1
    if not added or pinned != len(added):
        fail(f"{pinned} of {len(added)} added songs are filed under the release and pinned")
    ok(f"{len(added)} tracks became songs filed under release {release['id']}, pinned ({len(owned_before)} were in the library already)")


# ----------------------------------------------------------------------------------------- library
def library(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    state = json.load(open(args.state_in, encoding="utf-8"))
    first = api.call("GET", "/api/v1/library/1")
    created = api.call("POST", "/api/v1/library", {
        **first, "id": 0, "name": "Second", "rootPath": args.root2, "isDefault": False,
        "plexSectionId": args.section2, "plexLibraryPath": None, "outputPolicy": None,
    })
    target = created["id"]
    ok(f"created library {target} at {args.root2} (Plex section {args.section2})")
    song_id = state["imported"][-1]
    before = file_path(song(api, song_id))
    accepted = api.call("POST", "/api/v1/song/move", {"songIds": [song_id], "libraryId": target})
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "MoveSongs")
    log(f"     MoveSongs: {finished.get('message')}")
    record = song(api, song_id)
    after = file_path(record)
    if record.get("libraryId") != target or not after or not after.startswith(args.root2.rstrip("/") + "/"):
        fail(f"song {song_id} is in library {record.get('libraryId')} at {after}")
    if exists(args.exec_cmd, before) or not exists(args.exec_cmd, after):
        fail(f"the file did not move: {before} -> {after}")
    ok(f"song {song_id} moved to library {target}: {after}")
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        refreshes = plex_state(args.plex_state_cmd)["refreshes"]
        if any(refresh["section"] == args.section2 and after.startswith(refresh["path"]) for refresh in refreshes):
            ok(f"Plex was asked to scan section {args.section2} for the new folder")
            return
        time.sleep(5)
    fail(f"Plex was never asked to scan section {args.section2}")


# ----------------------------------------------------------------------------------------- convert
def convert(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    state = json.load(open(args.state_in, encoding="utf-8"))
    qualities = {quality["name"]: quality["id"] for quality in api.call("GET", "/api/v1/qualitydefinition")}

    library_one = api.call("GET", "/api/v1/library/1")
    library_one["outputPolicy"] = {"version": 2, "lossless": {"codec": "mp3", "mode": "cbr", "bitrateKbps": 320}}
    api.call("PUT", "/api/v1/library/1", library_one)
    ok("library 1 converts lossless downloads to MP3 320")

    # A song not downloaded yet, wanted under a profile whose cutoff is FLAC, offered as a FLAC only.
    lossless = [profile for profile in api.call("GET", "/api/v1/qualityprofile") if profile["name"] == "Lossless"]
    if not lossless:
        fail("no Lossless profile")
    song_id = state["spare"][0]
    record = song(api, song_id)
    api.call("PUT", f"/api/v1/song/{song_id}", {"monitored": True, "qualityProfileId": lossless[0]["id"]})
    offer(api, args.fake_add_cmd, [record], codec="flac", seed_base=9000)
    run_command(api, "SongSearch", args.timeout_s, songId=song_id)
    wait_for_queue(api, args.timeout_s)
    record = song(api, song_id)
    file = record.get("file") or {}
    if not str(file.get("path", "")).endswith(".mp3") or file.get("codec") != "mp3":
        fail(f"song {song_id} holds {file.get('path')} ({file.get('codec')}), expected an MP3")
    if record.get("qualityId") != qualities.get("FLAC"):
        fail(f"song {song_id}'s file is ranked {record.get('qualityId')}, expected FLAC ({qualities.get('FLAC')})")
    ok(f"a FLAC download became {file['path']}, still ranked FLAC")

    # On demand: an imported MP3 to AAC 256.
    target = state["imported"][0]
    before = file_path(song(api, target))
    plan = api.call("POST", "/api/v1/song/convert/preview", {"songIds": [target], "rule": {"codec": "aac", "bitrateKbps": 256}})
    if plan["convert"] != 1:
        fail(f"the conversion preview says {plan}")
    accepted = api.call("POST", "/api/v1/song/convert", {"songIds": [target], "rule": {"codec": "aac", "bitrateKbps": 256}})
    finished = wait_for_command(api, accepted["commandId"], args.timeout_s, "ConvertFiles")
    log(f"     ConvertFiles: {finished.get('message')}")
    after = file_path(song(api, target))
    if not after or not after.endswith(".m4a") or exists(args.exec_cmd, before) or not exists(args.exec_cmd, after):
        fail(f"song {target}: {before} -> {after}")
    converted = [event for event in history(api, target) if event.get("eventType") == "converted"]
    if not converted:
        fail(f"song {target} has no 'converted' history event")
    recycled = run(f"{args.exec_cmd} find {shlex.quote(args.recycle)} -name {shlex.quote(os.path.basename(before))}").strip()
    if not recycled:
        fail(f"{os.path.basename(before)} is not in the recycle bin {args.recycle}")
    ok(f"song {target} converted on demand to {after}; the original is in the recycle bin ({recycled.splitlines()[0]})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    commands = {"plex": plex, "lists": lists, "album": album, "library": library, "convert": convert}
    for name in commands:
        p = sub.add_parser(name)
        p.add_argument("--url", required=True)
        p.add_argument("--api-key", required=True)
        p.add_argument("--timeout-s", type=int, default=1800)
        p.add_argument("--state-in", default="phase6-state.json")
        p.add_argument("--state-out", default="phase6-state.json")
        p.add_argument("--root", default="/data/music")
        p.add_argument("--recycle", default="/config/recycle")
        if name == "plex":
            p.add_argument("--plex-url", default="http://127.0.0.1:5033")
            p.add_argument("--section", default="1")
        if name in {"lists", "convert"}:
            p.add_argument("--fake-add-cmd", required=True)
        if name in {"lists", "library", "convert"}:
            p.add_argument("--exec-cmd", required=True)
        if name in {"lists", "library"}:
            p.add_argument("--plex-state-cmd", required=True)
        if name == "lists":
            p.add_argument("--csv", required=True)
            p.add_argument("--download", type=int, default=20)
            p.add_argument("--min-resolved", type=float, default=0.95)
        if name == "album":
            p.add_argument("--album", default="Queen - A Night at the Opera")
        if name == "library":
            p.add_argument("--root2", default="/data/music2")
            p.add_argument("--section2", default="2")
    args = parser.parse_args()
    commands[args.command](args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
