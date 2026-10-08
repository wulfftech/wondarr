#!/usr/bin/env python3
"""Phase 7 gate checks (docs/build/PHASES.md) against a running Wondarr instance.

"a song only available inside an album torrent is imported by downloading just that file; an NZB album
downloads, unpacks, and only the wanted track(s) are imported; two wanted songs from the same album are
satisfied by one grab."

The torrent side and the usenet side are served by FakeSlskd's container stub (tools/FakeSlskd,
ContainerStubApp: a Torznab and a Newznab indexer, qBittorrent's Web API, SABnzbd's API) on the app
container's loopback, or by real ones (--indexer-url, --qbit-*, --sab-*) for the live check. Nothing is
on Soulseek and YouTube is off, so the indexers are the only source.

Subcommands (run in this order on one instance; each prints "ok ..." lines and exits non-zero on a
failed check):

  setup     Add the download clients and the indexers, and test each.
  torrent   Add --album unmonitored, monitor two of its tracks (Lossless profile), register the album as
            one torrent on the fake (every track, the identities of the songs), run MissingSearch, and
            check: both songs imported from ONE torrent grab, and the fake downloaded exactly those two
            files (every other file at priority 0, never rendered).
  usenet    Disable the torrent side, monitor a third track, register a three-track post of the album
            on the fake, run MissingSearch, and check: the song imported, the two other tracks were
            trimmed before the download (never rendered, never imported), and the job was deleted from
            SABnzbd with its files after the import.

Standard library only.

usage: scripts/phase7-gate.py setup   --url URL --api-key KEY [--containers http://127.0.0.1:5034]
       scripts/phase7-gate.py torrent --url URL --api-key KEY --fake-cmd "docker run ... curl -fsS" [--album "Queen - A Night at the Opera"]
       scripts/phase7-gate.py usenet  --url URL --api-key KEY --fake-cmd "..."
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
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module  # noqa: E402

scenario_tools = import_module("phase2-scenario")

ACTIVE_STATES = {"queued", "remotelyQueued", "downloading", "completed", "importing"}
TORRENT_TRACKS = ("Bohemian Rhapsody", "Love of My Life")
USENET_TRACK = "You're My Best Friend"


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


def fake_get(fake_cmd: str, containers: str) -> dict:
    return json.loads(run(f"{fake_cmd} {shlex.quote(containers + '/fake/containers')}"))


def fake_register(fake_cmd: str, containers: str, release: dict) -> dict:
    command = f"{fake_cmd} -X POST -H 'Content-Type: application/json' --data-binary @- {shlex.quote(containers + '/fake/containers')}"
    return json.loads(run(command, json.dumps(release)))


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


def wait_for_queue(api: Api, timeout_s: int) -> list[dict]:
    deadline = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < deadline:
        items = api.all_pages("/api/v1/queue")
        active = [item for item in items if item["state"] in ACTIVE_STATES]
        summary: dict[str, int] = {}
        for item in active:
            summary[item["state"]] = summary.get(item["state"], 0) + 1
        if summary != last:
            log(f"     queue: {summary or 'empty'}")
            last = summary
        if not active:
            return items
        time.sleep(5)
    fail(f"the download queue did not drain within {timeout_s} s")
    return []


def song(api: Api, song_id: int) -> dict:
    return api.call("GET", f"/api/v1/song/{song_id}")


def history(api: Api, song_id: int) -> list[dict]:
    events = api.all_pages(f"/api/v1/history?songId={song_id}&sortKey=date&sortDirection=ascending")
    for event in events:
        if isinstance(event.get("data"), str):
            try:
                event["data"] = json.loads(event["data"])
            except json.JSONDecodeError:
                event["data"] = {}
        event["data"] = event.get("data") or {}
    return events


def has_file(record: dict) -> bool:
    return isinstance(record.get("file"), dict)


def album_songs(api: Api, album: str, timeout_s: int) -> tuple[str, list[dict]]:
    """The album's default release, added (unmonitored) when it is not in the library yet."""
    results = api.call("GET", "/api/v1/album/lookup?term=" + urllib.parse.quote(album))
    groups = [result for result in results if result["source"] == "musicbrainz"]
    if not groups:
        fail(f"no MusicBrainz album for {album!r}")
    releases = api.call("GET", f"/api/v1/album/releasegroup/{groups[0]['id']}/releases")
    default = [release for release in releases if release.get("isDefault")]
    if len(default) != 1:
        fail(f"expected one default release of {album}, got {len(default)}")
    release = default[0]
    tracks = api.call("GET", f"/api/v1/album/musicbrainz/{release['id']}/tracks")
    if not all(track.get("owned") for track in tracks):
        accepted = api.call("POST", "/api/v1/album/add", {"source": "musicbrainz", "id": release["id"], "monitored": False})
        wait_for_command(api, accepted["commandId"], timeout_s, "AddAlbum")
        tracks = api.call("GET", f"/api/v1/album/musicbrainz/{release['id']}/tracks")
    return release["id"], [song(api, track["songId"]) for track in tracks if track.get("songId")]


def by_title(songs: list[dict], title: str) -> dict:
    for record in songs:
        if record["title"].lower() == title.lower():
            return record
    fail(f"the album has no track called {title!r}: {[record['title'] for record in songs]}")
    return {}


def want(api: Api, record: dict) -> None:
    """Monitored, under the Lossless profile: the fake's tracks are FLAC."""
    lossless = [profile for profile in api.call("GET", "/api/v1/qualityprofile") if profile["name"] == "Lossless"]
    if not lossless:
        fail("no Lossless profile")
    api.call("PUT", f"/api/v1/song/{record['id']}", {"monitored": True, "qualityProfileId": lossless[0]["id"]})


def track_file(record: dict, number: int, seed: int) -> dict:
    """One track of a fake release: a FLAC of the song's length whose fingerprint resolves to it."""
    seconds = round((record.get("durationMs") or 180_000) / 1000)
    return {
        "path": f"{number:02d} - {record['title']}.flac",
        "length": seconds,
        "audio": {"codec": "flac", "durationSeconds": seconds, "seed": seed, "frequency": 330 + number * 20},
        "identity": scenario_tools.identity_of(record),
    }


# ------------------------------------------------------------------------------------------- setup
def setup(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    host = urllib.parse.urlparse(args.containers)

    def client(name: str, type_: str, settings: dict) -> int:
        existing = [row for row in api.call("GET", "/api/v1/downloadclient") if row["name"] == name]
        body = {"name": name, "type": type_, "enabled": True, "priority": 1, "settings": settings}
        result = api.call("POST", "/api/v1/downloadclient/test", body)
        if not result["success"]:
            fail(f"the {name} test failed: {result['error']}")
        row = existing[0] if existing else api.call("POST", "/api/v1/downloadclient", body)
        ok(f"download client {name} ({type_}) tested and added")
        return int(row["id"])

    client("Gate qBittorrent", "qbittorrent", {"host": host.hostname, "port": host.port, "category": "wondarr"})
    client("Gate SABnzbd", "sabnzbd", {"host": host.hostname, "port": host.port, "urlBase": "sabnzbd", "apiKey": "gate-sab-key", "category": "wondarr"})

    def indexer(name: str, type_: str, path: str) -> None:
        existing = [row for row in api.call("GET", "/api/v1/indexer") if row["name"] == name]
        body = {"name": name, "type": type_, "protocol": None, "enabled": True, "priority": 25, "downloadClientId": None,
                "settings": {"url": f"{args.containers}/{path}", "apiPath": "/api", "apiKey": "gate-indexer-key", "categories": "3000"}}
        result = api.call("POST", "/api/v1/indexer/test", body)
        if not result["success"]:
            fail(f"the {name} test failed: {result['error']}")
        if not existing:
            api.call("POST", "/api/v1/indexer", body)
        ok(f"indexer {name} ({type_}) tested and added")

    indexer("Gate Torznab", "torznab", "torznab")
    indexer("Gate Newznab", "newznab", "newznab")


# ----------------------------------------------------------------------------------------- torrent
def torrent(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    release_id, songs = album_songs(api, args.album, args.timeout_s)
    ok(f"{args.album}: {len(songs)} tracks in the library (release {release_id})")

    wanted = [by_title(songs, title) for title in TORRENT_TRACKS]
    for record in wanted:
        want(api, record)

    files = [track_file(record, number, 7000 + number) for number, record in enumerate(songs, start=1)]
    title = f"{args.album} (1975) [FLAC]"
    registered = fake_register(args.fake_cmd, args.containers, {"protocol": "torrent", "title": title, "files": files})
    ok(f"the fake indexer offers one torrent of all {len(files)} tracks ({registered['infoHash']})")

    run_command(api, "MissingSearch", args.timeout_s)
    wait_for_queue(api, args.timeout_s)

    for record in wanted:
        if not has_file(song(api, record["id"])):
            fail(f"{record['title']} was not imported: {history(api, record['id'])}")
    ok(f"both wanted songs imported: {', '.join(TORRENT_TRACKS)}")

    state = fake_get(args.fake_cmd, args.containers)
    torrents = [entry for entry in state["torrents"] if entry["title"] == title]
    if len(torrents) != 1:
        fail(f"expected the album in qBittorrent once, found {len(torrents)}")
    downloaded = sorted(file["name"].split("/")[-1] for file in torrents[0]["files"] if file["downloaded"])
    expected = sorted(f"{songs.index(record) + 1:02d} - {record['title']}.flac" for record in wanted)
    if downloaded != expected:
        fail(f"the client downloaded {downloaded}, expected only {expected}")
    selected = sorted(file["name"].split("/")[-1] for file in torrents[0]["files"] if file["priority"] > 0)
    if selected != expected:
        fail(f"files at priority > 0: {selected}, expected only {expected}")
    ok(f"one torrent grab, {len(downloaded)} of {len(files)} files downloaded: {downloaded}")

    # Each song has its own grab on the one release; the first one says it brought the other along.
    grabs = []
    for record in wanted:
        grabbed = [event for event in history(api, record["id"]) if str(event.get("eventType", "")).lower() == "grabbed"]
        if len(grabbed) != 1:
            fail(f"{record['title']} has {len(grabbed)} grabs, expected one")
        grabs.append(grabbed[0]["data"])
    if {grab.get("release") for grab in grabs} != {title} or {grab.get("sourceType") for grab in grabs} != {"torznab"}:
        fail(f"the grabs were not both of {title!r} from the torrent source: {grabs}")
    if sorted(grab.get("bundledWith", 0) for grab in grabs) != [0, 1]:
        fail(f"expected one grab bundling the other song, got {[grab.get('bundledWith') for grab in grabs]}")
    ok("each song has its own grab of the one torrent; the first brought the second along (bundling)")


# ------------------------------------------------------------------------------------------ usenet
def usenet(args: argparse.Namespace) -> None:
    api = Api(args.url, args.api_key)
    _, songs = album_songs(api, args.album, args.timeout_s)

    for row in api.call("GET", "/api/v1/indexer"):
        if row["protocol"] == "torrent" and row["enabled"]:
            row["enabled"] = False
            api.call("PUT", f"/api/v1/indexer/{row['id']}", row)
    ok("the torrent indexers are disabled: usenet is the only source")

    target = by_title(songs, USENET_TRACK)
    number = songs.index(target) + 1
    others = [record for record in songs if record["id"] != target["id"] and not has_file(record)][:2]
    want(api, target)

    files = [track_file(target, number, 8000)] + [track_file(record, songs.index(record) + 1, 8001 + index) for index, record in enumerate(others)]
    title = f"{args.album} (1975) [FLAC] (usenet)"
    fake_register(args.fake_cmd, args.containers, {"protocol": "usenet", "title": title, "files": files})
    ok(f"the fake indexer offers a {len(files)}-track post with plain names")

    run_command(api, "MissingSearch", args.timeout_s)
    wait_for_queue(api, args.timeout_s)

    if not has_file(song(api, target["id"])):
        fail(f"{target['title']} was not imported from the post")
    imported_others = [record["title"] for record in others if has_file(song(api, record["id"]))]
    if imported_others:
        fail(f"tracks nobody wanted were imported: {imported_others}")
    ok(f"{target['title']} imported; the other {len(others)} tracks were not")

    deadline = time.monotonic() + 120
    while True:
        jobs = [job for job in fake_get(args.fake_cmd, args.containers)["jobs"] if job["title"] == title]
        if len(jobs) == 1 and jobs[0]["removed"]:
            break
        if time.monotonic() > deadline:
            fail(f"the usenet job was not deleted: {jobs}")
        time.sleep(3)
    job = jobs[0]
    if sorted(job["unpacked"]) != [files[0]["path"]]:
        fail(f"SABnzbd unpacked {job['unpacked']}, expected only {files[0]['path']}")
    if sorted(job["deleted"]) != sorted(entry["path"] for entry in files[1:]):
        fail(f"the trimmed files were {job['deleted']}")
    if not job["removedWithFiles"]:
        fail("the job was deleted without its files")
    ok(f"the post was trimmed to {files[0]['path']}, unpacked, and deleted from SABnzbd with its files after the import")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    commands = {"setup": setup, "torrent": torrent, "usenet": usenet}
    for name in commands:
        p = sub.add_parser(name)
        p.add_argument("--url", required=True)
        p.add_argument("--api-key", required=True)
        p.add_argument("--containers", default="http://127.0.0.1:5034")
        p.add_argument("--timeout-s", type=int, default=900)
        if name != "setup":
            p.add_argument("--fake-cmd", required=True, help="a curl command line that reaches the container's loopback")
            p.add_argument("--album", default="Queen - A Night at the Opera")
    args = parser.parse_args()
    commands[args.command](args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
