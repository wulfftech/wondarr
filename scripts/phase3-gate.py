#!/usr/bin/env python3
"""The Phase 3 gate (docs/build/PHASES.md): an existing music folder is identified and adopted.

Runs against a Wondarr that already passed the Phase 2 stage of scripts/smoke-test.sh (its songs are
in the library, and FakeSlskd answers AcoustID). It builds a reference folder of real, encoded audio
inside the container (ffmpeg, through --exec) from the songs the Phase 2 scenario deleted
(--dropped, written by scripts/phase2-scenario.py --dropped-out) plus copies of songs the library
already holds:

  * most files carry the recording MBID Picard would have written (layered Artist/Album/NN - Title),
  * some carry only a title and an artist (flat "Artist - Title"), found by the text search,
  * a few are untagged files with meaningless names, which only the Match queue can settle,
  * a few are second copies of songs the library already has (identified as duplicates).

Then it adds the folder as a reference library in adopt mode, scans it, and checks:

  * >= --min-ratio of the files are identified automatically,
  * every file left in the Match queue is resolved through the queue's API (as the user would),
  * adoption files every new song into the library (a file on disk, under the library root), and
  * the original files are byte-for-byte unchanged.

usage: scripts/phase3-gate.py --url http://localhost:1077 --api-key KEY --dropped dropped.json
                              --exec "docker exec wondarr-smoke" [--ref-root /data/reference]
"""

from __future__ import annotations

import argparse
import json
import re
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request


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


def wait_for_command(api: Api, command_id: int, timeout_s: int, label: str) -> dict:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        command = api.call("GET", f"/api/v1/command/{command_id}")
        status = str(command.get("status", "")).lower()
        if status in {"completed", "failed", "aborted", "cancelled", "orphaned"}:
            if status != "completed":
                raise SystemExit(f"FAIL: {label} ended {status}: {command.get('message')} {command.get('exception') or ''}")
            return command
        time.sleep(3)
    raise SystemExit(f"FAIL: {label} did not finish within {timeout_s} s")


def safe(text: str) -> str:
    """A file-name segment: no separators, no characters a FAT or SMB share would refuse."""
    cleaned = re.sub(r'[\\/:*?"<>|]', "-", text).strip().rstrip(".")
    return cleaned or "Unknown"


class Folder:
    """Runs commands inside the container, where the reference folder lives."""

    def __init__(self, exec_prefix: str, root: str) -> None:
        self.prefix = shlex.split(exec_prefix)
        self.root = root.rstrip("/")

    def run(self, *args: str, check: bool = True) -> str:
        result = subprocess.run([*self.prefix, *args], capture_output=True, text=True, encoding="utf-8")
        if check and result.returncode != 0:
            raise SystemExit(f"FAIL: {' '.join(args[:3])} ... -> {result.returncode}: {result.stderr[-500:]}")
        return result.stdout

    def make(self, relative: str, seconds: int, frequency: int, codec: str, tags: dict[str, str]) -> None:
        target = f"{self.root}/{relative}"
        self.run("mkdir", "-p", target.rsplit("/", 1)[0])
        args = ["ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", f"sine=frequency={frequency}:duration={seconds}", "-ac", "2", "-ar", "44100"]
        if codec == "mp3":
            args += ["-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "4"]
        else:
            args += ["-c:a", "flac", "-sample_fmt", "s16"]
        args += ["-map_metadata", "-1"]
        for key, value in tags.items():
            args += ["-metadata", f"{key}={value}"]
        self.run(*args, target)

    def hashes(self) -> dict[str, str]:
        out = self.run("sh", "-c", f"cd '{self.root}' && find . -type f -exec sha256sum {{}} + | sort -k 2")
        return {line[66:]: line[:64] for line in out.splitlines() if line.strip()}


def build(folder: Folder, dropped: list[dict], kept: list[dict], junk_every: int, text_every: int) -> dict[str, dict]:
    """Writes the reference folder; returns relative path -> what the file really is."""
    plan: dict[str, dict] = {}
    usable = [song for song in dropped if song.get("mbRecordingId") and song.get("durationMs")]
    if len(usable) < 10:
        raise SystemExit(f"FAIL: only {len(usable)} dropped songs with an MBID and a length; need 10")

    for index, song in enumerate(usable):
        seconds = max(30, round(song["durationMs"] / 1000))
        frequency = 300 + 11 * index
        artist = song["artistCredit"].split(" feat")[0].strip()
        if index % junk_every == junk_every - 1:
            relative = f"Unsorted/track {index + 1:02d}.mp3"
            folder.make(relative, seconds, frequency, "mp3", {})
            kind = "junk"
        elif index % text_every == text_every - 1:
            relative = f"{safe(artist)} - {safe(song['title'])}.flac"
            folder.make(relative, seconds, frequency, "flac", {"title": song["title"], "artist": artist})
            kind = "text"
        else:
            relative = f"{safe(artist)}/Collection/{index + 1:02d} - {safe(song['title'])}.mp3"
            folder.make(relative, seconds, frequency, "mp3", {
                "title": song["title"],
                "artist": artist,
                "album": "Collection",
                "track": str(index + 1),
                "MusicBrainz Track Id": song["mbRecordingId"],
            })
            kind = "mbid"
        plan[relative] = {"song": song, "kind": kind}

    for index, song in enumerate(kept):
        seconds = max(30, round((song.get("durationMs") or 180000) / 1000))
        artist = song["artistCredit"].split(" feat")[0].strip()
        relative = f"Old Rips/{safe(artist)} - {safe(song['title'])}.mp3"
        folder.make(relative, seconds, 2000 + 13 * index, "mp3", {
            "title": song["title"], "artist": artist, "MusicBrainz Track Id": song["mbRecordingId"],
        })
        plan[relative] = {"song": song, "kind": "duplicate"}

    return plan


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--dropped", required=True, help="the songs scripts/phase2-scenario.py deleted (JSON)")
    parser.add_argument("--exec", dest="exec_prefix", required=True, help='e.g. "sudo docker exec wondarr-smoke"')
    parser.add_argument("--ref-root", default="/data/reference", help="the reference folder inside the container")
    parser.add_argument("--duplicates", type=int, default=5, help="copies of songs the library already has")
    parser.add_argument("--min-ratio", type=float, default=0.90)
    parser.add_argument("--timeout-s", type=int, default=1800)
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    with open(args.dropped, encoding="utf-8") as handle:
        dropped = json.load(handle)
    library_songs = api.all_pages("/api/v1/song?sortKey=id&sortDirection=ascending")
    kept = [song for song in library_songs if song.get("hasFile") and song.get("mbRecordingId")][: args.duplicates]
    libraries = api.call("GET", "/api/v1/library")
    target = next(library for library in libraries if library.get("isDefault"))

    folder = Folder(args.exec_prefix, args.ref_root)
    folder.run("rm", "-rf", args.ref_root)
    started = time.monotonic()
    plan = build(folder, dropped, kept, junk_every=10, text_every=5)
    before = folder.hashes()
    kinds: dict[str, int] = {}
    for entry in plan.values():
        kinds[entry["kind"]] = kinds.get(entry["kind"], 0) + 1
    log(f"ok   built {len(plan)} reference files in {args.ref_root} ({kinds}) in {time.monotonic() - started:.0f} s")

    reference = api.call("POST", "/api/v1/referencelibrary", {
        "name": "Gate folder",
        "rootPath": args.ref_root,
        "mode": "adopt",
        "libraryId": target["id"],
        "enabled": True,
    })
    queued = api.call("POST", f"/api/v1/referencelibrary/{reference['id']}/scan")
    started = time.monotonic()
    wait_for_command(api, queued["id"], args.timeout_s, "ReferenceLibraryScan")
    reference = api.call("GET", f"/api/v1/referencelibrary/{reference['id']}")
    counts = reference["counts"]
    log(f"     scan: {reference.get('lastScanMessage')} in {time.monotonic() - started:.0f} s; counts {counts}")

    if counts["total"] != len(plan):
        raise SystemExit(f"FAIL: the scan found {counts['total']} files, the folder holds {len(plan)}")
    automatic = counts["identified"] + counts["adopted"]
    ratio = automatic / len(plan)
    if ratio < args.min_ratio:
        raise SystemExit(f"FAIL: {automatic}/{len(plan)} identified automatically ({ratio:.0%}); need {args.min_ratio:.0%}")
    log(f"ok   {automatic}/{len(plan)} identified automatically ({ratio:.0%})")

    queue = api.all_pages(f"/api/v1/matchqueue?referenceLibraryId={reference['id']}")
    for item in queue:
        entry = plan.get(item["relativePath"])
        if entry is None:
            raise SystemExit(f"FAIL: the Match queue holds a file the gate did not make: {item['relativePath']}")
        result = api.call("POST", f"/api/v1/matchqueue/{item['id']}/resolve", {"mbRecordingId": entry["song"]["mbRecordingId"]})
        if str(result.get("state", "")).lower() != "identified":
            raise SystemExit(f"FAIL: resolving {item['relativePath']} left it {result}")
    left = api.call("GET", f"/api/v1/matchqueue?referenceLibraryId={reference['id']}&page=1&pageSize=1")["totalRecords"]
    if left:
        raise SystemExit(f"FAIL: {left} files still in the Match queue after resolving every one")
    log(f"ok   resolved the remaining {len(queue)} files in the Match queue")

    # The command endpoint hands the whole body to the handler, so the library id sits beside the name.
    queued = api.call("POST", "/api/v1/command", {"name": "ReferenceAdopt", "referenceLibraryId": reference["id"]})
    wait_for_command(api, queued["id"], args.timeout_s, "ReferenceAdopt")
    reference = api.call("GET", f"/api/v1/referencelibrary/{reference['id']}")
    counts = reference["counts"]
    new_songs = sum(1 for entry in plan.values() if entry["kind"] != "duplicate")
    if counts["adopted"] < new_songs:
        raise SystemExit(f"FAIL: {counts['adopted']} files adopted, expected {new_songs}; counts {counts}")
    log(f"ok   {counts['adopted']} files adopted ({counts['identified']} duplicates left identified)")

    songs = {song["mbRecordingId"]: song for song in api.all_pages("/api/v1/song?sortKey=id&sortDirection=ascending")
             if song.get("mbRecordingId")}
    missing = [entry["song"]["title"] for entry in plan.values()
               if entry["kind"] != "duplicate" and not songs.get(entry["song"]["mbRecordingId"], {}).get("hasFile")]
    if missing:
        raise SystemExit(f"FAIL: adopted songs without a file: {missing[:5]}")
    placed = folder.run("sh", "-c", f"find '{target['rootPath']}' -type f -newer '{args.ref_root}' | wc -l").strip()
    if int(placed or 0) < new_songs:
        raise SystemExit(f"FAIL: only {placed} new files under {target['rootPath']}, expected {new_songs}")
    log(f"ok   every adopted song has a file; {placed} new files under {target['rootPath']}")

    if folder.hashes() != before:
        raise SystemExit("FAIL: the reference folder changed; adoption must leave the originals untouched")
    log("ok   the reference folder is byte-for-byte unchanged")
    return 0


if __name__ == "__main__":
    sys.exit(main())
