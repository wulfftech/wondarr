#!/usr/bin/env python3
"""Builds the Phase 4 gate scenario from the songs the Phase 2 scenario reserved.

The Phase 4 gate needs songs that Soulseek cannot fill (so the YouTube source is the only way), plus
its own OMV-with-intro and bot-check cases. scripts/phase2-scenario.py --reserve keeps such songs in the
library, unmonitored and with nothing on the fake Soulseek; this script monitors them again and writes
the scenario both fakes answer from:

  * the fake yt-dlp (tools/FakeSlskd/FakeYtDlp.cs) reads "videos": what each video id downloads as (or fails with);
  * the fake InnerTube stub (tools/FakeSlskd, FAKE_YT_SCENARIO) reads "songs": a search for a gate
    song answers that song's Art Track (songs shelf) or official video (videos shelf), with the
    song's own title, artist and length — so the decision engine judges them as it would real ones.

Cases:
  art-track     the songs shelf offers an Art Track of the right length; it downloads, verifies by
                fingerprint (the AcoustID stub learns the generated file) and is imported.
  omv-rejected  no Art Track; the videos shelf offers the official video, 20 s longer than the song
                (an intro): the duration tolerance rejects it and the song stays missing.
  bot-check     the Art Track's download answers YouTube's bot check: the item fails once with the
                reason, nothing loops, the song's backoff owns the retry.

usage: scripts/phase4-scenario.py --url http://localhost:1077 --api-key KEY
       --reserved phase2-reserved.json --out phase4-scenario.json [--fills 3]
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path


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


def seconds_of(song: dict) -> int:
    return max(30, round((song.get("durationMs") or 240000) / 1000))


def main_artist(song: dict) -> str:
    return song.get("artistCredit", "").split(" feat")[0].split(" & ")[0].split(", ")[0].strip()


def identity_of(song: dict) -> dict:
    return {
        "recordingId": song["mbRecordingId"],
        "title": song["title"],
        "artists": [{"id": "", "name": main_artist(song)}],
        "durationSeconds": seconds_of(song),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--reserved", required=True, help="the songs scripts/phase2-scenario.py reserved (JSON)")
    parser.add_argument("--out", required=True)
    parser.add_argument("--fills", type=int, default=3, help="how many Art-Track fills the scenario asks for")
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    with open(args.reserved, encoding="utf-8") as handle:
        reserved = json.load(handle)

    songs = api.call("GET", "/api/v1/song?sortKey=id&sortDirection=ascending&page=1&pageSize=1000")["records"]
    by_id = {song["id"]: song for song in songs}
    missing = [by_id[song["id"]] for song in reserved
               if song["id"] in by_id and not by_id[song["id"]].get("hasFile") and by_id[song["id"]].get("mbRecordingId")]

    needed = args.fills + 2
    if len(missing) < needed:
        raise SystemExit(f"FAIL: only {len(missing)} reserved songs are missing a file; the gate needs {needed} "
                         f"({args.fills} fills, one OMV case, one bot-check case)")

    chosen = missing[:needed]
    for song in chosen:
        api.call("PUT", f"/api/v1/song/{song['id']}", {"monitored": True})

    scenario: dict = {"version": "2026.08.19", "songs": {}, "videos": {}}

    for index, song in enumerate(chosen[: args.fills]):
        video_id = f"gateATV{index:04d}"
        scenario["songs"][song["title"]] = {
            "expect": "art-track", "videoId": video_id, "artist": main_artist(song), "durationSeconds": seconds_of(song),
        }
        scenario["videos"][video_id] = {
            "kind": "ok", "durationSeconds": seconds_of(song), "seed": 100 + index, "frequency": 440 + index * 37,
            "identity": identity_of(song),
        }

    omv_song = chosen[args.fills]
    scenario["songs"][omv_song["title"]] = {
        "expect": "omv-rejected", "videoId": "gateATV9998", "omvVideoId": "gateOMV0001",
        "artist": main_artist(omv_song), "durationSeconds": seconds_of(omv_song),
    }
    # An official video with a 20 s intro: well outside the default 3 s tolerance.
    scenario["videos"]["gateOMV0001"] = {
        "kind": "ok", "durationSeconds": seconds_of(omv_song) + 20, "seed": 200, "frequency": 523,
    }

    bot_song = chosen[args.fills + 1]
    scenario["songs"][bot_song["title"]] = {
        "expect": "bot-check", "videoId": "gateBOT0001", "artist": main_artist(bot_song), "durationSeconds": seconds_of(bot_song),
    }
    scenario["videos"]["gateBOT0001"] = {"kind": "bot-check"}

    Path(args.out).write_text(json.dumps(scenario, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"ok   wrote {args.out}: {len(scenario['songs'])} songs ({args.fills} fills, one OMV, one bot check), "
          f"monitored again", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
