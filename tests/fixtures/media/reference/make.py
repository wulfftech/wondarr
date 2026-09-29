"""Builds the Picard-style tagged reference-library fixtures from the tone files next door.

Dev-only (needs mutagen: `pip install mutagen`); the outputs are committed, so tests never run this.
Run from the repo root: `python tests/fixtures/media/reference/make.py`.

The tags mirror what MusicBrainz Picard writes (docs/architecture/LIBRARY_OUTPUT.md §7.5), including
the ones ATL does not write itself: the ID3 recording id lives in a UFID frame, not a TXXX.
"""
from __future__ import annotations

import shutil
from pathlib import Path

from mutagen.flac import FLAC
from mutagen.id3 import ID3, TALB, TDRC, TIT2, TPE1, TPE2, TPOS, TRCK, TSRC, TXXX, TYER, UFID
from mutagen.mp4 import MP4, MP4FreeForm

HERE = Path(__file__).resolve().parent
MEDIA = HERE.parent

# Queen — Bohemian Rhapsody on A Night at the Opera (the recording in tests/fixtures/musicbrainz/).
RECORDING = "b1a9c0e9-d987-4042-ae91-78d6a3267d69"
RELEASE = "6defd963-fe91-4550-b18e-82c685603c2b"
RELEASE_GROUP = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd"
ARTIST = "0383dadf-2a4e-4d10-a46a-e9e041da8eb3"
ACOUSTID = "8b3fd1b6-4e2b-4ea3-a0b9-6b2e1ab5e0a1"
ISRC = "GBUM71029604"
TEXT = {"title": "Bohemian Rhapsody", "artist": "Queen", "albumartist": "Queen", "album": "A Night at the Opera"}
IDS = {
    "MusicBrainz Album Id": RELEASE,
    "MusicBrainz Release Group Id": RELEASE_GROUP,
    "MusicBrainz Artist Id": ARTIST,
    "MusicBrainz Album Artist Id": ARTIST,
    "Acoustid Id": ACOUSTID,
}


def copy(source: str, target: str) -> Path:
    path = HERE / target
    shutil.copyfile(MEDIA / source, path)
    return path


def id3(path: Path, v2_version: int) -> None:
    tags = ID3()
    tags.add(TIT2(encoding=3, text=TEXT["title"]))
    tags.add(TPE1(encoding=3, text=TEXT["artist"]))
    tags.add(TPE2(encoding=3, text=TEXT["albumartist"]))
    tags.add(TALB(encoding=3, text=TEXT["album"]))
    tags.add(TRCK(encoding=3, text="11/12"))
    tags.add(TPOS(encoding=3, text="1/1"))
    if v2_version == 4:
        tags.add(TDRC(encoding=3, text="1975-11-21"))
    else:
        tags.add(TYER(encoding=1, text="1975"))
    tags.add(TSRC(encoding=3, text=ISRC))
    tags.add(UFID(owner="http://musicbrainz.org", data=RECORDING.encode("ascii")))
    for description, value in IDS.items():
        tags.add(TXXX(encoding=3, desc=description, text=value))
    tags.save(path, v2_version=v2_version)


def vorbis(path: Path) -> None:
    audio = FLAC(path)
    audio.delete()
    audio["TITLE"] = TEXT["title"]
    audio["ARTIST"] = TEXT["artist"]
    audio["ALBUMARTIST"] = TEXT["albumartist"]
    audio["ALBUM"] = TEXT["album"]
    audio["TRACKNUMBER"] = "11"
    audio["TRACKTOTAL"] = "12"
    audio["DISCNUMBER"] = "1"
    audio["DATE"] = "1975-11-21"
    audio["ISRC"] = ISRC
    audio["MUSICBRAINZ_TRACKID"] = RECORDING
    audio["MUSICBRAINZ_ALBUMID"] = RELEASE
    audio["MUSICBRAINZ_RELEASEGROUPID"] = RELEASE_GROUP
    audio["MUSICBRAINZ_ARTISTID"] = ARTIST
    audio["MUSICBRAINZ_ALBUMARTISTID"] = ARTIST
    audio["ACOUSTID_ID"] = ACOUSTID
    audio.save()


def mp4(path: Path) -> None:
    audio = MP4(path)
    audio.delete()
    audio["\xa9nam"] = TEXT["title"]
    audio["\xa9ART"] = TEXT["artist"]
    audio["aART"] = TEXT["albumartist"]
    audio["\xa9alb"] = TEXT["album"]
    audio["trkn"] = [(11, 12)]
    audio["disk"] = [(1, 1)]
    audio["\xa9day"] = "1975-11-21"
    audio["----:com.apple.iTunes:ISRC"] = MP4FreeForm(ISRC.encode("utf-8"))
    audio["----:com.apple.iTunes:MusicBrainz Track Id"] = MP4FreeForm(RECORDING.encode("utf-8"))
    for description, value in IDS.items():
        audio["----:com.apple.iTunes:" + description] = MP4FreeForm(value.encode("utf-8"))
    audio.save()


def text_only(path: Path) -> None:
    """Title and artist only, the way a hand-tagged or ripped file often is."""
    tags = ID3()
    tags.add(TIT2(encoding=3, text="Get Lucky"))
    tags.add(TPE1(encoding=3, text="Daft Punk feat. Pharrell Williams"))
    tags.save(path, v2_version=3)


def main() -> None:
    id3(copy("tone-320.mp3", "picard-v24.mp3"), 4)
    id3(copy("tone-v0.mp3", "picard-v23.mp3"), 3)
    vorbis(copy("tone.flac", "picard.flac"))
    mp4(copy("tone-256.m4a", "picard.m4a"))
    text_only(copy("tone-v0.mp3", "text-only.mp3"))
    shutil.copyfile(MEDIA / "tone-160.opus", HERE / "untagged.opus")


if __name__ == "__main__":
    main()
