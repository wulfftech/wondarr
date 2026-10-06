#!/usr/bin/env python3
"""FakeYT — a stand-in yt-dlp for the Phase 4 gate, in the FakeSlskd spirit.

CI cannot reach YouTube, so the Phase 4 gate runs the real image with this script in
place of the bundled yt-dlp (`APP__YOUTUBE__YTDLP__BINARY_PATH` points at it). It
answers the exact surface `YtDlpRunner` and `YtDlpAvailability` use:

  yt-dlp --version                     -> the scenario's version line
  yt-dlp -F --no-progress -- <url>     -> a formats listing (or a scenario failure)
  yt-dlp <download flags> -- <url>     -> writes real encoded Opus into the -o
                                          template's directory (ffmpeg, pink noise
                                          + sine, distinct per video id), then
                                          prints after_move:filepath and exits 0
                                          (or exits 1 with the scenario's stderr)

The scenario file (FAKE_YT_SCENARIO) maps each video id to its behaviour:

  {
    "version": "2026.08.19",
    "videos": {
      "<videoId>": {
        "kind": "ok",                  // the download succeeds
        "durationSeconds": 249,
        "seed": 7,                     // distinct seeds give distinct fingerprints
        "frequency": 440
      } | {
        "kind": "bot-check"            // exit 1, the bot-check stderr
      } | { "kind": "geo" }            // exit 1, the geo-restricted stderr
      | ...
    }
  }

A video id the scenario does not name answers "ok" with a 30 s file, so a gate
scenario only has to describe its interesting videos. The failure stderr strings
are the exact ones `YtDlpErrorTaxonomy` matches (ported from yt-dlp's extractor).

It is a test tool: never part of the image, never shipped.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

SCENARIO_PATH = os.environ.get("FAKE_YT_SCENARIO", "")
DEFAULT_VERSION = "2026.08.19"

# The exact stderr strings YtDlpErrorTaxonomy matches; each kind's message must
# contain the substring the taxonomy looks for.
FAILURES: dict[str, str] = {
    "bot-check": "ERROR: [youtube] <id>: Sign in to confirm you're not a bot. "
    "This helps protect our community. Learn more",
    "rate-limited": "ERROR: [youtube] <id>: This content isn't available, try again later. (Client 429)",
    "geo": "ERROR: [youtube] <id>: The uploader has not made this video available in your country",
    "age-gated": "ERROR: [youtube] <id>: Login details are needed to download this content "
    "(age-restricted video)",
    "private": "ERROR: [youtube] <id>: This video is private",
    "unavailable": "ERROR: [youtube] <id>: Video unavailable",
}

# The download invocation's output template: -o <dir>/%(id)s.%(ext)s
TEMPLATE = re.compile(r"-o\s+(\S+)")
# The watch URL the runner passes after --
URL = re.compile(r"https://music\.youtube\.com/watch\?v=([A-Za-z0-9_-]+)")


def scenario() -> dict:
    if not SCENARIO_PATH or not Path(SCENARIO_PATH).is_file():
        return {}
    with open(SCENARIO_PATH, encoding="utf-8") as handle:
        return json.load(handle)


def video(scenario: dict, video_id: str) -> dict:
    videos = scenario.get("videos", {})
    entry = videos.get(video_id)
    return entry if isinstance(entry, dict) else {}


def fail(kind: str, video_id: str) -> int:
    message = FAILURES.get(kind, FAILURES["unavailable"]).replace("<id>", video_id)
    sys.stderr.write(message + "\n")
    return 1


def generate(video_id: str, entry: dict, template: str) -> str:
    """Writes the Opus file yt-dlp would have produced and returns its path."""
    duration = float(entry.get("durationSeconds", 30))
    seed = int(entry.get("seed", abs(hash(video_id)) % 100000))
    frequency = float(entry.get("frequency", 440))

    # The template is <dir>/%(id)s.%(ext)s; the remux lands .opus (ADR-0006).
    target = template.replace("%(id)s", video_id).replace("%(ext)s", "opus")
    Path(target).parent.mkdir(parents=True, exist_ok=True)

    subprocess.run(
        [
            "ffmpeg",
            "-nostdin",
            "-v",
            "error",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"anoisesrc=d={duration}:c=pink:r=44100:a=0.25:seed={seed}",
            "-f",
            "lavfi",
            "-i",
            f"sine=f={frequency}:d={duration}:r=44100",
            "-filter_complex",
            "[0][1]amix=inputs=2:duration=first",
            "-ac",
            "2",
            "-c:a",
            "libopus",
            "-b:a",
            "160k",
            target,
        ],
        check=True,
    )

    # The gate's verification step fingerprints this file and asks AcoustID what it is. The fake's
    # AcoustID stub only knows fingerprints it was told about, so an entry with an identity block
    # registers the fingerprint against that recording — the same rule the Soulseek scenario's
    # identity blocks follow.
    identity = entry.get("identity")
    if isinstance(identity, dict) and "recordingId" in identity:
        fingerprint = subprocess.run(
            ["fpcalc", "-json", target], capture_output=True, text=True, check=True
        ).stdout
        fingerprint_value = json.loads(fingerprint).get("fingerprint", "")

        register = os.environ.get("FAKE_ACOUSTID_REGISTER", "")
        if register and fingerprint_value:
            body = json.dumps(
                {
                    "fingerprint": fingerprint_value,
                    "recordingId": identity["recordingId"],
                    "title": identity.get("title", ""),
                    "artists": identity.get("artists", []),
                    "durationSeconds": identity.get("durationSeconds", duration),
                }
            ).encode("utf-8")
            subprocess.run(
                [
                    "curl",
                    "-fsS",
                    "-X",
                    "POST",
                    "-H",
                    "Content-Type: application/json",
                    "--data-binary",
                    "@-",
                    register,
                ],
                input=body,
                check=False,
            )

    return target


def main(argv: list[str]) -> int:
    scenario_data = scenario()

    if argv[:1] == ["--version"]:
        sys.stdout.write(scenario_data.get("version", DEFAULT_VERSION) + "\n")
        return 0

    joined = " ".join(argv)

    if "-F" in argv:
        # The formats probe: a listing with format rows, so HasFormatRows passes.
        match = URL.search(joined)
        video_id = match.group(1) if match else "unknown"
        entry = video(scenario_data, video_id)

        if entry.get("kind") in FAILURES:
            return fail(entry["kind"], video_id)

        sys.stdout.write(
            f"[info] {video_id}: Downloading webpage\n"
            "ID  EXT   RESOLUTION CHOPS FILESIZE   TBR PROTO INFOCODEC VCODEC    ACODEC   MORE INFO\n"
            "251 opus audio only        3.2MiB   160k https          unknown    opus   [default]\n"
            "140 m4a  audio only        2.1MiB   128k https          unknown    mp4a   \n"
        )
        return 0

    # The download: -f bestaudio[acodec=opus]/... -x --audio-format opus ... -o <template> -- <url>
    match = URL.search(joined)
    if match is None:
        sys.stderr.write("ERROR: no watch URL in the download arguments\n")
        return 1

    video_id = match.group(1)
    entry = video(scenario_data, video_id)
    kind = entry.get("kind", "ok")

    if kind in FAILURES:
        return fail(kind, video_id)

    template_match = TEMPLATE.search(joined)
    if template_match is None:
        sys.stderr.write("ERROR: no -o template in the download arguments\n")
        return 1

    target = generate(video_id, entry, template_match.group(1))

    # --print after_move:filepath: the moved file's path is the last line of stdout.
    sys.stdout.write(target + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
