#!/usr/bin/env python3
"""Builds the Phase 4 gate scenario from the songs the Phase 2 scenario left missing on Soulseek.

The Phase 4 gate needs songs that Soulseek cannot fill (so the YouTube source is the only way),
plus its own OMV-with-intro and bot-check cases. This script reads the Phase 2 scenario's dropped
songs (the ones deleted from the library), picks the ones FakeSlskd offers nothing for, and writes
the Phase 4 scenario: each song's video id in the fake InnerTube's recorded fixtures, the behaviour
FakeYT should answer with, and the identity the AcoustID stub should resolve the generated file to.

The video ids come from the recorded songs fixture (`tests/fixtures/ytmusic/search-songs.json`):
the gate reuses the real ids the parser saw, so the candidate the app grabs is the one the fixture
offered. The first fixture result is the Art Track the gate fills with; the videos fixture's first
result (a UGC re-upload titled "Official Video", 4:08 against the 6:10 song) is the OMV case.

usage: scripts/phase4-scenario.py --url http://localhost:1077 --api-key KEY
       --dropped phase2-dropped.json --out phase4-scenario.json
"""

from __future__ import annotations

import argparse
import json
import sys
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
            with urllib.request.urlopen(request, timeout=180) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"FAIL: {method} {path} -> {error.code} {error.read()[:300]!r}") from error
        return json.loads(raw) if raw else None


def fixture_results(name: str) -> list[dict]:
    """The video ids and titles of one recorded fixture's shelf, in order."""
    document = json.loads((ROOT / "tests" / "fixtures" / "ytmusic" / f"search-{name}.json").read_text(encoding="utf-8"))

    results: list[dict] = []

    def walk(node: object) -> None:
        if isinstance(node, dict):
            if "musicResponsiveListItemRenderer" in node:
                item = node["musicResponsiveListItemRenderer"]
                video_id = find_video_id(item)
                title = find_title(item)
                if video_id:
                    results.append({"videoId": video_id, "title": title or video_id})
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    def find_video_id(item: dict) -> str | None:
        text = json.dumps(item)
        marker = '"videoId":"'
        at = text.find(marker)
        return text[at + len(marker) :].split('"', 1)[0] if at >= 0 else None

    def find_title(item: dict) -> str | None:
        text = json.dumps(item)
        marker = '"text":"'
        at = text.find(marker)
        return text[at + len(marker) :].split('"', 1)[0] if at >= 0 else None

    walk(document)
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True)
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--dropped", required=True, help="the songs scripts/phase2-scenario.py deleted (JSON)")
    parser.add_argument("--out", required=True)
    parser.add_argument("--fills", type=int, default=3, help="how many Art-Track fills the scenario asks for")
    args = parser.parse_args()

    api = Api(args.url, args.api_key)
    with open(args.dropped, encoding="utf-8") as handle:
        dropped = json.load(handle)

    # The songs still missing a file: the Phase 3 gate adopted some of the dropped ones back, so the
    # ones without a file are the gate's YouTube-only candidates.
    songs = api.call("GET", "/api/v1/song?sortKey=id&sortDirection=ascending&page=1&pageSize=1000")
    records = songs["records"] if isinstance(songs, dict) else songs
    by_title = {song["title"]: song for song in records}
    missing = [
        song
        for song in dropped
        if song.get("mbRecordingId")
        and not by_title.get(song.get("title", ""), {}).get("hasFile")
    ]

    if len(missing) < args.fills + 2:
        raise SystemExit(
            f"FAIL: only {len(missing)} songs are missing on Soulseek; the gate needs "
            f"{args.fills + 2} ({args.fills} fills, one OMV case, one bot-check case)"
        )

    songs_fixture = fixture_results("songs")
    videos_fixture = fixture_results("videos")

    if not songs_fixture or not videos_fixture:
        raise SystemExit("FAIL: the recorded fixtures hold no results; re-record tests/fixtures/ytmusic")

    scenario = {"version": "2026.08.19", "songs": {}, "videos": {}}

    # The Art-Track fills: the songs fixture's first results, real durations, distinct seeds.
    for index, song in enumerate(missing[: args.fills]):
        video = songs_fixture[index % len(songs_fixture)]
        scenario["songs"][song["title"]] = {
            "expect": "art-track",
            "source": "youtube",
            "videoId": video["videoId"],
        }
        scenario["videos"][video["videoId"]] = {
            "kind": "ok",
            "durationSeconds": max(30, round((song.get("durationMs") or 240000) / 1000)),
            "seed": 100 + index,
            "frequency": 440 + index * 37,
            "identity": {
                "recordingId": song["mbRecordingId"],
                "title": song["title"],
                "artists": [{"id": "", "name": song.get("artistCredit", "").split(" feat")[0]}],
                "durationSeconds": max(30, round((song.get("durationMs") or 240000) / 1000)),
            },
        }

    # The OMV case: the videos fixture's first result runs 4:08 against the 6:10 song — the
    # duration tolerance rejects it. The song's own Art Track is offered as the next candidate.
    omv_song = missing[args.fills]
    omv_video = videos_fixture[0]
    atv_video = songs_fixture[(args.fills + 1) % len(songs_fixture)]
    scenario["songs"][omv_song["title"]] = {
        "expect": "omv-rejected",
        "source": "youtube",
        "videoId": atv_video["videoId"],
        "omvVideoId": omv_video["videoId"],
    }
    scenario["videos"][omv_video["videoId"]] = {
        "kind": "ok",
        "durationSeconds": 248,  # the fixture's 4:08 against the 6:10 song: out of tolerance
        "seed": 200,
        "frequency": 523,
    }
    scenario["videos"][atv_video["videoId"]] = {
        "kind": "ok",
        "durationSeconds": max(30, round((omv_song.get("durationMs") or 240000) / 1000)),
        "seed": 201,
        "frequency": 587,
        "identity": {
            "recordingId": omv_song["mbRecordingId"],
            "title": omv_song["title"],
            "artists": [{"id": "", "name": omv_song.get("artistCredit", "").split(" feat")[0]}],
            "durationSeconds": max(30, round((omv_song.get("durationMs") or 240000) / 1000)),
        },
    }

    # The bot-check case: the first grab answers the bot check; the item fails, the backoff owns it.
    bot_song = missing[args.fills + 1]
    bot_video = songs_fixture[(args.fills + 2) % len(songs_fixture)]
    scenario["songs"][bot_song["title"]] = {
        "expect": "bot-check",
        "source": "youtube",
        "videoId": bot_video["videoId"],
    }
    scenario["videos"][bot_video["videoId"]] = {"kind": "bot-check"}

    Path(args.out).write_text(json.dumps(scenario, indent=2) + "\n", encoding="utf-8")
    print(f"ok   wrote {args.out}: {len(scenario['songs'])} songs, {len(scenario['videos'])} videos", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
