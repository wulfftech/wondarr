# Product definition

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — concepts, principles, user stories. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

### 4.1 The one-line pitch

> A self-hosted *arr for **songs**. You tell it which recordings you want (one at a time, or by pasting a playlist), it finds the best copy it can on Soulseek first, YouTube second, and torrents/usenet last, verifies that what it downloaded is actually that recording, tags it properly, files it into your library in the layout you chose, and keeps looking for a better copy until your quality cutoff is met.

### 4.2 Core concepts (the domain model in words)

| Concept | Meaning in this app | Lidarr equivalent |
|---|---|---|
| **Song** | The unit of everything. One *recording* of one piece of music, identified by a MusicBrainz Recording MBID when one exists, plus ISRC(s) and external IDs (Spotify, Deezer, YouTube Music). Carries an *album context* assigned by the library's album policy (which album, single, or per-artist "Singles" pseudo-album it is filed and tagged as; §7.3). | Track (but Lidarr cannot monitor or search one) |
| **Artist** | Display and grouping entity, MB Artist MBID when known. Created implicitly when a Song is added. Can optionally be monitored for "top tracks" or "new singles". | Artist |
| **Release context** | The album/single/EP the song will be *filed under* and *tagged with*: album title, album artist, release/release-group MBID, track and disc number, year, cover art. A song has one active context; the user can switch it (original album, the single, or a per-artist "Singles" pseudo-album). | Album |
| **Version flags** | Structured facts about the recording: live, remix, acoustic, instrumental, radio edit, remaster, explicit, cover, karaoke. Used for matching so a *Live at Wembley* copy never satisfies a studio request. | none |
| **Quality** | A codec + bitrate/bit-depth bucket (e.g. `MP3-320`, `FLAC 16`, `OPUS-160`). Detected from source attributes before download and *measured* with ffprobe after download. | Quality |
| **Quality profile** | Ordered list of allowed qualities, a cutoff, and whether upgrades are allowed. Assigned per song, with a default. | Quality Profile |
| **Source profile** | Which sources may be used for a song and in what order (default: Soulseek → YouTube → Torrent/Usenet), plus per-source rules (min free-slot preference, YouTube "ATV only", allow album containers). | Indexer/Download client settings (no per-item ordering in *arrs) |
| **Wanted** | Songs that are monitored and either *missing* (no file) or *cutoff unmet* (file below cutoff). | Wanted: Missing / Cutoff Unmet |
| **Candidate** | One concrete thing that could be downloaded: a Soulseek file on a user's share, a YouTube Music video, a torrent/NZB that contains the song. Normalised across sources so the decision engine can compare them. | Release |
| **Grab** | The act of committing to a candidate: enqueue in slskd, run yt-dlp, send torrent/NZB to a download client. Tracked in the **Queue**. | Grab / Queue |
| **Import** | Verify → tag → rename/place → record file → notify. Runs on completed downloads. | Completed Download Handling |
| **Library** | A root folder plus a **layout** (`flat`, `artist`, `artist-album`, `plexamp`) plus a naming template. Multiple libraries allowed. | Root Folder |
| **Import list** | An external list that is polled and turned into Songs: Deezer/YouTube Music playlists, a Spotify playlist exported as CSV (Exportify columns, including ISRC), Last.fm loved tracks, ListenBrainz, plain text, an artist's top-N tracks, or a reference library. | Import Lists |
| **Reference library** | An existing music folder (flat or layered) the user already has. Scanned and identified so its songs count as owned and are never re-downloaded; optionally adopted into a managed layout. Ambiguous files go to a Match queue. | Unmapped Files / Manual Import (partly) |
| **Blocklist** | Candidates (by source-specific identity: slskd user+path, YouTube video id, torrent hash/NZB guid) that failed verification or were rejected by the user; never grabbed again. | Blocklist |
| **History** | Every grab, import, upgrade, failure, and deletion, with the reason. | History |

### 4.3 Design principles

1. **Song identity is a first-class problem, not a filename problem.** Every song has a canonical identity (MBID/ISRC where possible) and a *fingerprint-verified* file. Matching on "Artist - Title" strings is how naive tools end up with the live version. We match on identity, duration, version flags, and fingerprint.
2. **Sources are plugins behind one interface.** Soulseek, YouTube, and Torznab/Newznab each implement `search(song) → candidates` and `grab(candidate) → job`. The decision engine and import pipeline do not care where a file came from.
3. **Soulseek is primary because it is the only source that regularly has *single files* in the wanted quality.** YouTube is the fallback that almost always has *something* (at capped quality). Torrents/usenet are album-level and expensive per song, so they are last and are made cheaper with selective file download and "bundle other wanted songs from the same album".
4. **Never trust a download until it is verified.** ffprobe for duration/codec, Chromaprint/AcoustID for identity where the recording is known, and a duration tolerance otherwise. Failed verification → blocklist the candidate → try the next one automatically.
5. **The library is the product.** Tags are complete (including MBIDs, ISRC, album artist, cover art), naming is templated, and the Plexamp layout is a tested preset, not an afterthought.
6. **Feel like an *arr.** Same vocabulary (Wanted, Queue, History, Blocklist, Quality Profiles, Root Folders, Connect, Import Lists), same API conventions (`X-Api-Key`, `/api/v1`), same Docker conventions (`/config`, `/downloads`, `/music`, `PUID/PGID/TZ`), so existing dashboards, notification tooling, and muscle memory carry over.
7. **Be a good Soulseek citizen.** The app shares the library it builds (via slskd), throttles searches, respects queue etiquette, and never hammers users.

### 4.4 User stories (MVP unless marked later)

- As a user I paste `Artist - Title` (or search) and the app shows me the matching recordings with album, year, duration and a preview (Deezer 30-second clip where available), and I pick one to add as wanted.
- As a user I paste a Deezer/YouTube Music playlist URL, or upload a Spotify playlist exported as CSV, and every track becomes a wanted song, with a re-sync schedule so new additions get picked up. *(Phase 6)*
- As a user I see a Wanted page with Missing and Cutoff-Unmet tabs and can trigger "Search all" or per-song search.
- As a user I can run an **interactive search** for a song and see all candidates from all sources with their score, quality, duration delta, and the reason any were rejected, then grab one manually.
- As a user I see a Queue page showing live progress from slskd, yt-dlp, and my torrent/usenet clients, with the ability to cancel/blocklist-and-retry.
- As a user I choose per library whether files land flat, per artist, per artist/album, or in the Plexamp preset (fewest albums per artist), and I can preview the resulting path for a sample song before saving.
- As a user I can pick, per library or per song, the album policy: fewest albums per artist, singles-only pseudo-album, original album, or the single release.
- As a user I get a Plex library partial-scan and a Discord/webhook notification when a song is imported.
- As a user I can see why a song has not been found (last search time, candidates seen and rejected, next retry).
- As a user I can point the app at my existing Music folder (flat or layered) so it identifies what is already there, counts those songs as owned, lets me resolve ambiguous files in a Match queue, and optionally adopts them into the managed layout. *(Phase 3)*
- As a user I manage the bundled slskd from the app: Soulseek account, listen port, what I share (with a "Share my library" toggle), slots and speed limits. *(Phase 2)*
- As a user I can sync a playlist (e.g. the imported Spotify playlist) to Plex as a Plex playlist, or as an `.m3u8` file in the library. *(Phase 6)*
