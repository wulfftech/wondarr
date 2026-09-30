# Library output: layouts, album policy, tags, Plex

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the spec for the organizer, tag writer and Plex integration. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

### 7.1 Layout presets

| Preset | Default template | Intended for |
|---|---|---|
| `flat` | `{Artist Name} - {Track Title}` | One big folder; DJ crates; simple players; Navidrome/Jellyfin, which group by tags reliably |
| `artist` | `{Artist Name}/{Artist Name} - {Track Title}` | Folder per artist, no album layer |
| `artist_album` | `{Artist Name}/{Album Title} ({Release Year})/{track:00} - {Track Title}` | Standard music-server layout, faithful albums |
| `plexamp` | `{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}` + album **compaction policy** (§7.3) + `cover.jpg` + `.lrc` sidecar + Plex-safe tag rules + partial scan | Plex Music / Plexamp libraries |

All presets are defaults for the same knobs: **naming template**, **album policy**, **sidecar options**, and **post-place hooks**; the user can edit any of them, and the UI previews the resulting path with a real song. Templates use Lidarr's `{Token}` syntax so *arr users can paste what they know: `{Artist Name}`, `{Artist CleanName}`, `{Artist NameThe}`, `{Artist NameFirstCharacter}`, `{Artist MbId}`, `{Album Artist Name}`, `{Album Title}`, `{Album CleanTitle}`, `{Album Type}`, `{Album MbId}`, `{Release Year}`, `{Original Year}`, `{Track Title}`, `{Track CleanTitle}`, `{Track ArtistName}`, `{track:00}`, `{medium:0}`, `{Recording MbId}`, `{Release MbId}`, `{ISRC}`, `{Quality Full}`, `{Quality Title}`, `{MediaInfo AudioCodec}`, `{MediaInfo AudioBitRate}`, `{MediaInfo AudioSampleRate}`, `{MediaInfo AudioBitsPerSample}`, `{Source}`, `{Version}`; modifiers `{Album Title:60}` (truncate), casing/separator variants (`{Artist.Name}`, `{artist_name}`); optional groups `[ ({Release Year})]` vanish when empty; beets/Picard-style replacement of `\ / : * ? " < > |`, leading dots, trailing dots/spaces; optional ASCII folding; a max-path guard; and `%aunique`-style disambiguation when two different albums render the same folder.

### 7.2 Assessment: does Plex/Plexamp still need an album folder layer?

Short answer: **not as a hard requirement, but in practice yes for a library you want to behave.** Evidence:

- Plex's current guidance says music "should be organized at least so that there are separate folders of files for each album", and, in so many words, that even with complete and perfect embedded tags Plex "strongly encourages" album folders because a flat file list "can result in failures or a poor experience" ([Adding Music Media From Folders](https://support.plex.tv/articles/200265296-adding-music-media-from-folders/)). A 2019 feature request for flat-folder support is marked "[Implemented]" on the Plex forum, which matches the observed behaviour that tags *can* drive grouping.
- Under the default setting (online matching, "Prefer local metadata" off), a December 2025 forum report shows loose files in one folder, each with correct and distinct album tags, being merged into the wrong album ([forum thread](https://forums.plex.tv/t/loose-mp3s-with-correct-album-tags-grouped-into-wrong-album-when-respecttags-false/934205)).
- A detailed community write-up of the scanner's mechanics finds that with "Prefer local metadata" on, "one folder is one release": the folder is the grouping boundary, conflicting `musicbrainz_albumid` values or different `date` strings inside a folder fragment the album, and, critically, **Plex never reconsiders a track's album membership on rescan**; fixing a mis-grouped track requires moving files out, scanning, emptying trash, moving back and scanning again ([how Plex groups music](https://github.com/nailz1000/music-library-rescue/blob/main/docs/01-how-plex-groups-music.md)).
- Plexamp is a client of the server's library; it adds no structure rules of its own, but its artist pages, Sonic Analysis and "Singles & EPs" grouping all operate on server-side albums.

Consequences for the design:

1. The **`plexamp` preset keeps an album layer** and makes the album assignment deliberate (§7.3), because that is what the scanner keys on and because assignments are effectively permanent once scanned.
2. **`flat` and `artist` layouts stay available** for Plex users who accept the trade-off; the preset then writes the same album tags (§7.3 policy still decides the album *tag*), and the UI shows a one-time notice: enable "Prefer local metadata" and Local Media Assets on the Plex library before the first scan.
3. Any later re-assignment (compaction, policy change, adoption of a reference library) is performed as an explicit task that does the move-out / scan / empty-trash / move-back / scan sequence through the Plex API, never silently.

### 7.3 Album policy ("compaction"): the fewest albums per artist

Every song is filed and tagged with exactly one album context. The policy is per library, overridable per song, and the Plexamp preset defaults to **`fewest_albums`**:

1. **`fewest_albums` (Plexamp default).** For each artist, choose the smallest set of *real* official releases (from MusicBrainz, preferring Album > EP > Single, official status, earliest date) that together contain all of the artist's owned recordings — a greedy set cover — but only use a real album when it will hold at least `min_tracks_per_real_album` (default 2) owned tracks; everything left over goes into the artist's **"Singles" pseudo-album**. Result: an artist with 14 scattered songs typically becomes 2–3 real albums plus one "Singles" album rather than 14 one-track albums. Assignments are **sticky**: adding a song never moves existing songs; a "Compact library" task re-plans and applies moves only when the user runs it.
2. **`singles_only`.** Everything by an artist goes into `{Artist} – Singles`: one album per artist, the absolute minimum, at the cost of real album identity and online album art (the pseudo-album is unmatched online and shows one embedded cover).
3. **`original_album`.** Each song under the earliest official album/EP it appears on; faithful, possibly many partial albums.
4. **`single_release`.** The MusicBrainz single with its own cover; one folder per song.
5. **`compilation`** (for `flat`/`artist`): album artist `Various Artists`, compilation flag set.

Rules that make any of these survive Plex's scanner: one `musicbrainz_albumid` per folder (the chosen release's id, or one stable synthetic UUID per pseudo-album — never the tracks' differing real release ids), one identical `DATE` string per folder (the pseudo-album uses one date; each track's real date lives in `ORIGINALDATE`/`ORIGINALYEAR`, which Plex uses for the displayed year), one identical `ALBUMARTIST`, and real or assigned track numbers with no duplicates.

### 7.4 Plexamp-specific rules (what the `plexamp` preset does beyond the path)

Plex groups by embedded tags first and by file names only as a fallback; Album Artist and Album must be identical across every file of an album, track/disc numbers must be set, and compilations are recognised only via `Album Artist = Various Artists` (Plex ignores the iTunes compilation flag). Local Media Assets must be enabled for embedded tags, folder art and lyric sidecars to be used. So the preset:

- Writes `ALBUMARTIST`, `ALBUM`, `TITLE`, `ARTIST`, `TRACKNUMBER`/`TRACKTOTAL`, `DISCNUMBER`/`DISCTOTAL`, `DATE` with the same strings across the folder, per §7.3.
- Writes the MusicBrainz release, release-group, artist, album-artist and recording ids (one release id per folder). Automatic use by Plex is undocumented, but Fix Match accepts a *release* MBID and consistent ids cost nothing.
- Sets `ALBUMARTIST=Various Artists` (+ compilation flag) for compilation contexts.
- Embeds front cover art (JPEG, bounded to a configurable max edge, default 1400 px) **and** writes `cover.jpg` in the album folder (for the pseudo-album: a generated cover from the artist image, or the first track's art, configurable); optionally `artist.jpg` in the artist folder from fanart.tv/TheAudioDB. The first file placed into an album folder writes its `cover.jpg` and later files never replace it, and the embedded cover and `cover.jpg` are the same JPEG, bounded with ffmpeg to the library's configured max edge.
- Writes an `.lrc` (synced) or `.txt` sidecar with the track's base name when LRCLIB has lyrics, plus the unsynced text in the tag. The lyrics come from LRCLIB (`/api/get` with the track's length, `/api/search` as the fallback, matched within ±2 s); synced lyrics become `.lrc` and plain-only lyrics become `.txt`; a sidecar of either kind that is already there is never replaced; and no LRCLIB failure — a miss, a throttle, a timeout — fails or delays an import beyond the client's bounded timeout.
- Keeps formats to what Plexamp direct-plays everywhere: FLAC, MP3, AAC/M4A, ALAC. YouTube's native Opus is transcoded once according to the library's **output policy**, which is fully user-configurable: target codec (AAC, MP3, or keep Opus untouched), constant bitrate (e.g. 256/320 kbps) or VBR quality level, container (`.m4a`/`.mp3`/`.opus`), sample rate (keep or 44.1 kHz), optional loudness measurement; per-source override (e.g. YouTube → AAC-256, SoundCloud → MP3-320) and per-song override in the interactive grab. The Plexamp preset defaults to AAC-256 `.m4a`; lossless targets from lossy sources are refused; `.webm` is never written. Whatever the target, the file's quality stays `OPUS-160` (its source) so the song remains upgradeable.
- After placing, calls Plex `GET /library/sections/{id}/refresh?path=<album folder>` with `X-Plex-Token`; the compaction task additionally uses `emptyTrash` between moves.
- Optional playlist sync per import list via `/playlists/upload` (server-side `.m3u8` path) or rating keys, and `.m3u8` export into the library root.
- Does not write ReplayGain for Plex (Plex ignores it and does its own Plex Pass loudness analysis); the optional writer exists for Navidrome/Jellyfin users of the same files.

### 7.5 Tag specification (Picard's mapping, which Plex, Navidrome, Jellyfin and beets all read)

| Field | Vorbis (FLAC/Opus) | MP4 (M4A) | ID3v2.4 (MP3) | Value |
|---|---|---|---|---|
| Title / Artist / Album Artist / Album | `TITLE`, `ARTIST`, `ALBUMARTIST`, `ALBUM` | `©nam`, `©ART`, `aART`, `©alb` | `TIT2`, `TPE1`, `TPE2`, `TALB` | song + album context |
| Artists (multi-valued) | `ARTISTS` | `----:com.apple.iTunes:ARTISTS` | `TXXX:ARTISTS` | all credited artists |
| Track / Disc numbers and totals | `TRACKNUMBER`, `TRACKTOTAL`, `DISCNUMBER`, `DISCTOTAL` | `trkn`, `disk` | `TRCK`, `TPOS` (`n/total`) | album context (pseudo-album: assigned) |
| Date / Original date | `DATE`, `ORIGINALDATE`, `ORIGINALYEAR` | `©day` (no original-date atom; freeform optional) | `TDRC`, `TDOR` (v2.3: `TYER`, `TORY`) | album date (identical per folder); recording's first release |
| Genre | `GENRE` | `©gen` | `TCON` | optional, off by default |
| ISRC | `ISRC` | `----:com.apple.iTunes:ISRC` | `TSRC` | from song |
| Recording MBID | `MUSICBRAINZ_TRACKID` | `----:com.apple.iTunes:MusicBrainz Track Id` | `UFID:http://musicbrainz.org` | note the naming trap: "Track Id" holds the *recording* id |
| Release-track MBID | `MUSICBRAINZ_RELEASETRACKID` | `…:MusicBrainz Release Track Id` | `TXXX:MusicBrainz Release Track Id` | when the context is a real release |
| Release / Release-group / Artist / Album-artist MBIDs | `MUSICBRAINZ_ALBUMID`, `MUSICBRAINZ_RELEASEGROUPID`, `MUSICBRAINZ_ARTISTID`, `MUSICBRAINZ_ALBUMARTISTID` | `…:MusicBrainz Album Id` etc. | `TXXX:MusicBrainz Album Id` etc. | one album id per folder (synthetic for pseudo-albums) |
| Release type / status | `RELEASETYPE`, `RELEASESTATUS` | `…:MusicBrainz Album Type/Status` | `TXXX:MusicBrainz Album Type/Status` | album/single/ep; official |
| AcoustID | `ACOUSTID_ID` (+ `ACOUSTID_FINGERPRINT` optional) | `…:Acoustid Id` | `TXXX:Acoustid Id` | from verification |
| Compilation | `COMPILATION=1` | `cpil` | `TCMP` | Various Artists contexts |
| Cover art | `METADATA_BLOCK_PICTURE` (Opus) / PICTURE block (FLAC) | `covr` (JPEG/PNG only) | `APIC` | front cover |
| Lyrics (unsynced) | `LYRICS` | `©lyr` | `USLT` | when found; synced go to `.lrc` |
| ReplayGain (optional) | `REPLAYGAIN_TRACK_GAIN/PEAK`; Opus uses `R128_TRACK_GAIN` | `----:com.apple.iTunes:REPLAYGAIN_*` | `TXXX:REPLAYGAIN_*` | ffmpeg `ebur128`, RG2 −18 LUFS |
| Comment | `COMMENT` | `©cmt` | `COMM` | `Imported by <app> from <source>` (off by default) |

ID3v2.4 UTF-8 by default with a v2.3 option for old car stereos. Tags are written to a temp copy, re-read to verify, then atomically replaced; existing tags are replaced rather than merged except `ENCODER`/`ENCODED_BY`.

### 7.6 Reference libraries and adoption (Phase 3)

A **reference library** is any folder the user already has (flat or layered). The app scans it, identifies each file (tags with MBIDs/ISRC first, then AcoustID fingerprint, then title/artist/duration search against MusicBrainz and Deezer), and records the result as an *owned* song so it is never downloaded again. Files that cannot be identified confidently land in a **Match queue** where the user picks from ranked candidates or searches manually (Picard-style), one file or many at a time. Two modes per reference library: **reference only** (read-only; the files stay where they are and are simply counted as owned) and **adopt** (the app re-tags and moves/renames them into a managed library layout, with the recycle bin as the safety net). Import lists can point at a reference library too, so "my existing Music folder" is a first-class input, exactly like a playlist.

Adoption itself is a **copy**, never a move (Phase 3): each identified file is copied into the target library through the same `LibraryOrganizer` imports go through — the Picard tag set with the cover, the library's layout and the album policy, the lyrics sidecar — and the song's `song_file` row is repointed at the copy with source `adopted`, so it is an ordinary library file from then on: upgradable, shared and scanned by Plex. The reference row becomes `adopted` and a later scan leaves it alone. The user's original file is only ever read: it stays exactly where it is and the song's file row no longer names it. A file whose song already holds another file (it was downloaded since, or another file of the same recording satisfies it) is skipped and left `identified`. The import event is published so the share rescan and the Plex partial scan follow, exactly as for an import.
