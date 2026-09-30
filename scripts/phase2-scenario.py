#!/usr/bin/env python3
"""Build the FakeSlskd scenario for the Phase 2 CI gate from the songs a running Wondarr has.

Run after the Phase 1 gate has added its songs. It keeps the first --keep songs (by id) and deletes
the rest, so the gate's search budget stays small, then writes a scenario (tests/gate/phase2-scenario.schema.md)
in which every kept song is offered by a fast peer as a correctly identified file, and two songs
carry a trap:

  * the first song's best-ranked file (FLAC, free slot, fast peer) is really a *live* take:
    FakeSlskd's AcoustID stub names a different recording for its fingerprint, so the download must
    be rejected after verification and the next candidate (an MP3-320 from a slower peer) imported;
  * the second song's first peer rejects the transfer, so the next candidate must be grabbed.

Songs with a MusicBrainz recording id are identified by it (fingerprint-verified import); the rest
get no identity (AcoustID "unknown": probe + duration only).

With --dropped-out, the deleted songs (id, title, artist credit, recording id, length) are written
there first: the Phase 3 gate builds its reference library from them.

usage: scripts/phase2-scenario.py --url http://localhost:1077 --api-key KEY --out /tmp/scenario.json [--keep 20]
                                  [--dropped-out /tmp/dropped.json]
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request

# A recording id no real song has: the "live take" the trap file really is.
LIVE_MBID = "00000000-0000-4000-8000-000000000001"


class Api:
    def __init__(self, base: str, key: str) -> None:
        self.base = base.rstrip("/")
        self.key = key

    def call(self, method: str, path: str) -> object:
        request = urllib.request.Request(self.base + path, method=method)
        request.add_header("X-Api-Key", self.key)
        request.add_header("Accept", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"FAIL: {method} {path} -> {error.code} {error.read()[:300]!r}") from error
        return json.loads(raw) if raw else None


def safe(text: str) -> str:
    """A path segment a Soulseek share could plausibly hold (no separators)."""
    return text.replace("/", "-").replace("\\", "-").strip() or "Unknown"


def file_entry(song: dict, index: int, *, user: str, codec: str, seed: int, free: bool, speed: int,
               queue: int, identity: dict | None, transfer: str = "ok", folder: str | None = None) -> dict:
    seconds = max(30, round((song.get("durationMs") or 180000) / 1000))
    artist = safe(song["artistCredit"].split(" feat")[0])
    title = safe(song["title"])
    ext = "flac" if codec == "flac" else "mp3"
    album = safe(folder or "Singles")
    entry = {
        "username": user,
        "path": f"@@{user}\\Music\\{artist}\\{album}\\{index:02d} - {artist} - {title}.{ext}",
        "length": seconds,
        "hasFreeUploadSlot": free,
        "uploadSpeed": speed,
        "queueLength": queue,
        "transfer": transfer,
        "audio": {"codec": codec, "durationSeconds": seconds, "seed": seed, "frequency": 220 + 7 * (seed % 90)},
    }
    # An explicit, realistic advertised size: FLAC results carry no bitrate, so the fake cannot
    # estimate one, and a size of 0 would make the decision engine reject the file (size sanity).
    if codec == "flac":
        entry.update({"sampleRate": 44100, "bitDepth": 16, "size": seconds * 900 * 125})
    else:
        entry["bitRate"] = 320
        entry["size"] = seconds * 320 * 125
        entry["audio"]["bitrateKbps"] = 320
    if identity is not None:
        entry["identity"] = identity
    return entry


def identity_of(song: dict, *, recording_id: str | None = None, title: str | None = None) -> dict | None:
    mbid = recording_id or song.get("mbRecordingId")
    if not mbid:
        return None
    return {
        "recordingId": mbid,
        "title": title or song["title"],
        "artists": [{"id": "00000000-0000-4000-8000-000000000000", "name": song["artistCredit"].split(" feat")[0]}],
        "durationSeconds": round((song.get("durationMs") or 0) / 1000),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--keep", type=int, default=20)
    parser.add_argument("--dropped-out", help="write the deleted songs here (for the Phase 3 gate)")
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    songs = sorted(api.call("GET", "/api/v1/song?page=1&pageSize=1000&sortKey=id&sortDirection=ascending")["records"],
                   key=lambda song: song["id"])
    keep, drop = songs[: args.keep], songs[args.keep:]
    if args.dropped_out:
        fields = ("id", "title", "artistCredit", "mbRecordingId", "durationMs")
        with open(args.dropped_out, "w", encoding="utf-8", newline="\n") as handle:
            json.dump([{field: song.get(field) for field in fields} for song in drop], handle, indent=2, ensure_ascii=False)
            handle.write("\n")
    for song in drop:
        api.call("DELETE", f"/api/v1/song/{song['id']}")
    if len(keep) < 3:
        raise SystemExit("FAIL: need at least 3 songs for the scenario")

    files = []
    for index, song in enumerate(keep):
        seed = 1000 + index * 10
        if index == 0:
            # The trap: the best-ranked file is a live take the fingerprint exposes.
            files.append(file_entry(song, 1, user="fastpeer", codec="flac", seed=seed, free=True, speed=5_000_000,
                                    queue=0, identity=identity_of(song, recording_id=LIVE_MBID, title=f"{song['title']} (live)")))
            files.append(file_entry(song, 1, user="slowpeer", codec="mp3", seed=seed + 1, free=False, speed=400_000,
                                    queue=5, identity=identity_of(song)))
        elif index == 1:
            files.append(file_entry(song, 2, user="rudepeer", codec="flac", seed=seed, free=True, speed=5_000_000,
                                    queue=0, identity=identity_of(song), transfer="reject"))
            files.append(file_entry(song, 2, user="slowpeer", codec="mp3", seed=seed + 1, free=False, speed=400_000,
                                    queue=5, identity=identity_of(song)))
        else:
            files.append(file_entry(song, index + 1, user="gatepeer", codec="mp3", seed=seed, free=True, speed=3_000_000,
                                    queue=0, identity=identity_of(song)))

    scenario = {"searchDelayMs": 1500, "transferDelayMs": 1000, "maxInFlight": 2, "files": files}
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(scenario, handle, indent=2, ensure_ascii=False)
        handle.write("\n")
    print(f"ok   scenario for {len(keep)} songs ({len(files)} files) written to {args.out}; deleted {len(drop)} songs")
    print(f"     trap songs: '{keep[0]['artistCredit']} - {keep[0]['title']}' (live file first), "
          f"'{keep[1]['artistCredit']} - {keep[1]['title']}' (rejected transfer first)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
