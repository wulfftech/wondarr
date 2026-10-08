# Matching, decision and verification engine

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the scoring rules are the spec for the decision engine and its tests. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

This is the part that makes or breaks a single-song *arr. It has three layers: **normalisation** of every candidate into one shape, **hard rejections**, then **weighted scoring**. Every rejection and every score component is stored on the candidate so the interactive search can show *why*. The baseline ordering is Sockseek's (the best-tested Soulseek single-track ranker), extended with queue length, reputation, and cross-source quality/identity terms; the YouTube identity rules are spotDL's.

### 6.1 Normalisation

Every `SourceProvider.search()` returns candidates in one shape:

```
Candidate
  source_type, source_instance_id, blocklist_key
  display_name             # filename, video title, or release name
  parsed: { artist?, title?, album?, track_no?, version_hints[] }   # from filename/path/title parsing
  duration_ms?             # Soulseek attr, YouTube duration; torrents/usenet: unknown
  codec?, bitrate_kbps?, sample_rate?, bit_depth?, vbr?, size_bytes?
  quality_id (inferred)    # from attrs, YouTube format id, or release-name parsing
  container: single_file | album_container            # torrents/NZBs are containers
  container_files?: [{index, path, size}]             # from .torrent metainfo or Gazelle fileList
  availability: { free_slot?, queue_len?, upload_speed?, seeders?, leechers?, grabs?, age_days?, freeleech? }
  provenance: { username? | channel? | indexer?, is_topic_channel?, video_type? (ATV/OMV/UGC), user_success_count? }
  raw                      # untouched source payload for debugging
```

Soulseek filename parsing is deliberately generous: split on ` - `, `_`, `.`; strip leading track numbers (`01`, `01.`, `01 -`, `A1`); bracketed content becomes *version hints* (`(Live at …)`, `[Remix]`, `(2011 Remaster)`) rather than noise; `feat.` variants are detected; the parent folder is the album guess and the grandparent the artist guess (shares are almost always `Artist/Album/NN - Title.ext` or `Artist - Album/NN Title.ext`). Normalised comparison strings are lower-cased, diacritics-stripped, with `_` and `:|?><*"` replaced by spaces and whitespace collapsed (Sockseek's rule); title is checked against the filename without extension, artist against the full path, album against the directory name.

### 6.2 Hard rejections (never grabbed automatically)

| Rule | Detail |
|---|---|
| Blocklisted | `blocklist_key` present (permanent or unexpired) |
| Format not allowed | extension/codec outside the quality profile's allowed set |
| Not an upgrade | the song already has a file and the candidate's quality rank ≤ current (manual grab bypasses) |
| Duration out of tolerance | `abs(candidate − song) > tolerance` (default 3 s Soulseek, 5 s YouTube; skipped when unknown, in which case the candidate's maximum score is capped and post-download verification is mandatory) |
| Version mismatch | candidate has a version hint the song lacks, or lacks one the song has: `live`, `remix`, `acoustic`, `instrumental`, `karaoke`, `cover`, `demo`, `edit`, `slowed`, `8d`, `bassboosted` are hard by default; `remaster` is soft (remasters share the MusicBrainz recording) |
| Artist mismatch | no artist token overlap after normalisation when the artist is known (guards against same-titled songs by other artists); the candidate's parsed artist is matched when it carries one, the path tokens only when it does not (a YouTube candidate's path is a bare video id); soft-fail for compilation paths without the artist |
| Size sanity | `< 500 KB`, or outside the quality tier's window (duration × min/max bitrate, Lidarr-style budgets) |
| Source-specific | Soulseek: user in ignore list, locked files, `[private]`; YouTube: shorts, live streams, > 15 min, age-gated without cookies, `videos` results when the source rule is "songs only"; torrent: zero seeders, or private indexer with partial downloads disabled and whole-album disabled; usenet: no par2 and no plausible per-track file names when trimming is required |
| Container too expensive | album container above `max_container_size` (default 1.5 GB) with no selective download possible |

### 6.3 Scoring (0–1000, higher is better)

```
score = identity (0–400) + quality (0–300) + availability (0–150) + source_preference (0–100) ± adjustments (≤ 50)
```

- **identity** — title token-sort ratio (after removing version hints and `feat.` clauses) × 200; artist token overlap × 100; duration closeness 100 at 0 s, decaying with spotDL's `exp(−0.1·Δs)` shape to 0 at the tolerance edge, flat 40 when unknown; a hard identity hit (YouTube Music song id we resolved from ISRC/MB, a Gazelle `fileList` entry whose album we matched to the MB release) sets identity to 400.
- **quality** — position of the inferred quality in the profile: cutoff or above = 300, decreasing by rank; VBR flag, sample rate and bit depth break ties. Above-cutoff candidates score the same as the cutoff unless the profile says "prefer highest".
- **availability** — Soulseek: free upload slot +80, queue length 0 → +40 decaying to 0 at 20, upload-speed bucket +30 (Sockseek's `speed/650` and `/350` buckets); YouTube: flat 120; torrents: seeders log-scaled to +100, freeleech +20, `.torrent`/known file list over bare magnet +30; usenet: age < 1000 days +100 decaying, grabs +20.
- **source_preference** — from the source profile order: tier 1 = 100, tier 2 = 60, tier 3 = 30, plus a per-instance user offset.
- **adjustments** — YouTube `ATV`/topic-channel +40, `OMV` −20, `UGC` −40; Soulseek "bracket check" passed +15 (no unexplained `[...]`/`(...)` in the filename), album folder matches the assigned album context +20, track number matches +10; user reputation: previously delivered verified files +20 per success (capped +40), previous failures −40 each (a user with ≥ 2 failures is skipped for a day); explicit-flag disagreement −5.

Ties: smaller size wins for lossy, larger for lossless, then a deterministic hash of `user+filename` so results are stable between runs.

"Good enough" early stop: when a candidate scores ≥ 850 with a free slot and ≥ 1 MB/s upload speed while Soulseek results are still streaming, grab it immediately (Sockseek's fast-search behaviour), then let the search finish for the record. (Phase 5, with hub streaming; in v1 a search completes in seconds and the early stop acts between queries.)

### 6.4 Search strategy per source

- **Soulseek (slskd).** Queries in order: `artist title` → the same with diacritics stripped and special characters removed → `title artist` → title-only (artist must then match in the path) → `artist album` (browse the folder for the track) → title with `feat.` clauses stripped. Stop early when the candidate pool is good. Each search: `searchTimeout` 8000 ms (from last response), `responseLimit` 100, `fileLimit` 2000, `minimumPeerUploadSpeed` 1, polled to completion (v1; `/responses` is empty until a search completes, so streaming needs `/hub/search`, deferred to Phase 5), always `DELETE`d afterwards, and cancelled by our own 30 s wall clock. **Budget**: a global token bucket of 30 searches per 4 minutes, at most 2 outstanding, ≥ 5 s between submissions (Sockseek's 34/220 s and soularr's 5 s floor). Server-banned query terms are wildcarded (`*inkin`).
- **YouTube.** ytmusicapi `search(isrc, ignore_spelling=True)` when an ISRC is known → `search("artist title", filter="songs")` → `filter="videos"` only when the source rule allows → plain yt-dlp `ytsearch5:` as last resort. Candidates from the `songs` filter are marked verified and get the ATV bonus; a verified candidate scoring ≥ 850 ends the search immediately. Grab = yt-dlp with `bestaudio[acodec=opus]/bestaudio`, `-x`, cookies/PO-token provider if configured, `-t sleep` pacing, `--print after_move:filepath`. The source runs as tier 2: a tier-1 (Soulseek) pool with an accepted candidate ends the search before YouTube is asked, and a tier-2 acceptance stops the search before tier 3 (torrents/usenet) is asked; a manual search always queries every enabled tier.
- **Torznab/Newznab.** We cannot search for a recording, so we search for releases that contain it: from MusicBrainz, list the releases the recording appears on (the assigned album context first, then other official albums, then singles, then compilations); per indexer read `caps` and use `t=music&artist=&album=` when supported, else `t=search&q=artist album`, categories 3000/3010/3040; for Gazelle trackers with a user API key, call `ajax.php?action=browse&filelist=<title>&artistname=<artist>` directly and read `fileList` (name and size per file) before choosing. Parse quality from the release name with Lidarr's regexes (`FLAC`, `24BIT`/`TR24`, `MP3-320`, `V0`, `WEB`). For `.torrent` results download the metainfo via the Prowlarr proxy link and bencode-parse the file list locally; for magnets defer file selection until metadata is fetched. Locate the wanted track inside the container by track number + title fuzzy match + size window; carry `container_files` so the grab can select just that file and also every *other* wanted song in the same container ("bundling").
  As built (P7-04): one source per protocol (`torznab`, `newznab`), each available when an indexer and a download client of its protocol are enabled; at most 3 releases (distinct artist and title), every indexer of the protocol asked at once within a 60 s budget (the file lists of what did answer get 10 s more); releases merged by info-hash, else indexer and guid; the 8 best-seeded (torrents) or most-grabbed (usenet) of each query have their file lists read; a release whose list lacks the song gives no candidate, and a magnet or an obfuscated NZB gives one "file list unknown" candidate whose path is the release title. A file's quality is the release name's unless its extension names another codec.

### 6.5 Verification after download

1. **Probe** with ffprobe: must decode; capture codec, bitrate, sample rate, bit depth, channels, duration. Measured quality replaces inferred quality, with one exception: a file the app itself transcoded from a lossy source (YouTube Opus → AAC/MP3) keeps its *source* quality (`OPUS-160`), never the target bitrate, so a 320 cutoff still treats it as upgradeable (a "FLAC" that is 128 kbps in a FLAC container is caught here; a spectral cutoff check for transcoded lossless, like SoulSync's fake-lossless detector, is a Phase 8 enhancement).
2. **Duration** within tolerance of the song's known length (YouTube rips of official videos with intros fail here).
3. **Fingerprint**: `fpcalc -json` (first 120 s), then AcoustID `lookup?client=&fingerprint=&duration=&meta=recordingids` (3 req/s, honour 429). Pass if the wanted recording MBID appears with score ≥ 0.7 (configurable); 0.5–0.7 is "needs review" (imported with a low-confidence badge unless the profile says strict); when the song has no MBID yet, accept a returned recording whose normalised title and artist match and **learn** the MBID. If the first window fails (DJ talk-over, long intro), retry on the middle of the file (fpcalc has no offset option: `ffmpeg -ss` piped into `fpcalc -`). If AcoustID has no fingerprint for the recording at all (common for new or obscure music), fall back to probe + duration + score ≥ threshold and mark `fingerprint_verified = false`. Pitch/speed-shifted YouTube uploads are a known failure mode (a 2.5 % speed change defeats matching); they fail verification and get blocklisted, which is the desired outcome.
4. **Outcome**: pass → import; fail → history "rejected" with reason, candidate blocklisted, the next candidate grabbed automatically (bounded by `max_auto_attempts_per_search`, default 4), then the next source tier.

### 6.6 Upgrades and re-search cadence

A song below cutoff stays in Cutoff Unmet. The upgrade search (`UpgradeSearch`) runs on a slower schedule, in bounded batches, and only grabs when the candidate's quality rank is strictly higher and its identity score is not clearly worse than the current file's. On import the old file goes to the recycle bin and history records "upgraded". An automatic upgrade must also be confirmed by its fingerprint: a replacement AcoustID does not know (verified by probe and length only) is rejected and blocklisted, because it would replace a file the user already has with one nobody could confirm (DECISIONS build session 6 #9; manual grabs are exempt). Missing-song re-search backs off per song (1 h → 6 h → 24 h → 72 h → weekly) and never re-runs the same Soulseek query more often than the network's own 12-minute wishlist cadence. The upgrade loop's selection also skips a song with an unfinished compaction move (planned, staged, or failed with its file still in the staging folder): the compaction has to place the file first, and the import would defer anyway (LIBRARY_OUTPUT §7.3, the per-song guard).

The identity rule is implemented: an automatic upgrade candidate is rejected (`worseIdentity`) when its identity sub-score is more than 40 below the identity sub-score of the candidate that produced the held file — when that is known; a held score from a hard identity hit (400) counts as 360, because no candidate scored from its name and length can reach it (DECISIONS build session 7 #11; fingerprint confirmation of automatic upgrades does the fine work); a held file without a stored candidate (adopted, or imported before scores were stored) is judged by the quality rule alone, and manual grabs stay exempt. The upgrade loop's backoff counts only its own `Upgrade` runs, so a missing-song search never delays an upgrade and the reverse; adding a monitored song queues a `SongSearch` at once unless `search.search_on_add` is off.
