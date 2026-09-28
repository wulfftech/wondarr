# Compilarr — Single-Song *arr: Research and Build Plan

| | |
|---|---|
| **Status** | Revision 3 — all owner decisions recorded in §0; no open design questions; next step is creating the repository (§12) |
| **Date** | 2026-09-28 |
| **Working name** | **Compilarr** (proposed by the owner; no GitHub or Docker Hub collision found by web search — confirm with a GitHub name search when creating the repo; §3.6) |
| **Target repo** | New repository (this plan lives in `wulfftech/Claude-General` until then) |
| **Evidence** | Six research reports in `research/` (≈40k words, URL-cited). Claims marked "[S]" in those reports come from search-engine extracts of pages this sandbox could not fetch and should be re-verified before being quoted in a spec. |

> **Read this first — build vs. adopt.** *(Revision 2: decided — build new, porting useful code; see §0.)* The research found two active open-source projects that already cover most of this brief: **SoulSync** (MIT, Python/React, 2.2k★) chains Soulseek → YouTube → torrents/usenet (via Prowlarr + qBittorrent/SABnzbd) with quality profiles, "upgrade until cutoff", a wishlist with progressive backoff, Picard-style MusicBrainz tagging, AcoustID verification, LRCLIB lyrics and a `/api/v1` REST API; **DroppedNeedle** (AGPL-3.0) takes single-track or album requests from the MusicBrainz catalogue and fulfils them via slskd and SABnzbd with verification and automatic upgrades. Neither is *arr-shaped (Wanted/Queue/History/Blocklist, `X-Api-Key` conventions, interactive search with rejection reasons), neither documents a Plexamp-specific layout with a release-context policy, and neither does selective single-file torrent downloads with wanted-song bundling. §3.6 has the comparison; the recorded decision (§0) is to **build new and port their useful, permissively licensed code**. The rest of this document plans that build.

---
**Contents**

0. [Decision log](#0-decision-log-revision-2-2026-09-28)
1. [Summary](#1-summary)
2. [Goals, non-goals, and constraints](#2-goals-non-goals-and-constraints)
3. [Research findings](#3-research-findings) — 3.1 Lidarr · 3.2 Soulseek · 3.3 YouTube · 3.4 Torrents/Usenet · 3.5 Identity, tagging, Plex · 3.6 Landscape and names · 3.7 Stack landscape
4. [Product definition](#4-product-definition)
5. [Architecture](#5-architecture) — components, pipeline, plugin interfaces, data model, scheduler, API
6. [Matching, decision, and verification engine](#6-matching-decision-and-verification-engine)
7. [Library output](#7-library-output-layouts-naming-tags-artwork-plex)
8. [Tech stack decision](#8-tech-stack-decision)
9. [Deployment](#9-deployment)
10. [Phased build plan](#10-phased-build-plan)
11. [Risks and mitigations](#11-risks-and-mitigations)
12. [Open questions and next steps](#12-open-questions-and-next-steps)

Appendices: A. Reference compose/config · B. Quality definitions seed · C. Research reports and method

---
## 0. Decision log (revision 2, 2026-09-28)

Answers from the owner to the round-1 questions, and what each changed in this plan.

| # | Question | Decision | Effect on the plan |
|---|---|---|---|
| 1 | Build vs adopt | **Build new**, porting useful code from existing projects | §3.6 lists what to port and from where; licences in §8.5 |
| 2 | Name | "Syncarr" proposed | **Taken** — `syncarr/syncarr` (syncs Radarr/Sonarr/Lidarr instances) plus forks; and the word implies sync, not acquisition. Candidates that still look free: Cratearr, Jukearr, Recordarr, Tracksarr, Songdarr (§3.6). Still open (§12) |
| 3 | Stack | **Aligned with the other *arrs** where possible | Recommendation switched to **C#/.NET 10 + React** (§8). This also unlocks near-verbatim porting of Lidarr/Prowlarr GPL-3.0 code (download clients, Torznab/Newznab, quality parser, naming engine, notifications, health checks, fingerprinting) |
| 4 | slskd | Not running yet; **deploy/package with the app**; **sharing configurable in-app** | Compose ships slskd headless alongside the app; the app owns slskd's configuration (credentials, shared folders, slots, limits) via its API; a bundled single-image variant is planned (§9.1, §9.5) |
| 5 | Plexamp album layer | Assess; prefer flat `Artist - Title.ext` or artist folders; else aggregate into the fewest albums | **Assessment (§7.2): Plex does not hard-require album folders but strongly advises them, and flat folders have documented mis-grouping failures under default settings.** The Plexamp preset therefore keeps an album layer and implements the "fewest albums per artist" compaction; flat and artist-folder layouts remain available for other players or for Plex with "Prefer local metadata" and the caveats stated |
| 6 | Quality | FLAC supported; **default cutoff 320**; transcode YouTube to AAC or MP3 | Default profile "Standard 320": cutoff MP3-320 (AAC-256 and FLAC allowed, upgrades on); YouTube grabs transcoded once to AAC-256 `.m4a` by default (MP3-320 selectable) but *ranked as their Opus-160 source* so they stay upgradeable (§6.5, Appendix B) |
| 7 | Torrents | **qBittorrent only**; no private-tracker rules for now | Phase 7 scoped to qBittorrent (+ SABnzbd for usenet); partial single-file downloads on by default; Transmission/Deluge/rTorrent/NZBGet and tracker-policy switches moved to backlog |
| 8 | Inputs | Individual adds, Deezer/YouTube Music playlists, **Spotify via exported CSV**, Last.fm, ListenBrainz, and **the user's existing music folder (flat or layered), with manual matching where AcoustID cannot resolve** | CSV importer speaks the Exportify column set (includes ISRC); **library adoption with a manual-match queue is promoted to Phase 3** because it is both an input and the dedupe source (§4.4, §7.6, §10) |
| 9 | Deployment | Docker on Linux, aligned with the primary *arrs | hotio/LinuxServer-style image: `/config`, `/data`, `PUID/PGID/UMASK/TZ`, `/ping`, URL base, multi-arch; Unraid template (§9) |
| 10 | Dedupe | Not via Plex/Lidarr; the app scans the user's main Music folder | "Reference library" roots: scanned, matched, marked as owned, optionally adopted (§7.6) |

### Round 2 (same day)

| # | Question | Decision | Effect on the plan |
|---|---|---|---|
| 1 | Name | "Comparr" or "Compilarr" (compilations) | **Compilarr** adopted as the working name: no GitHub hits by web search; "Comparr" only matches a French price-comparison site and reads as "compare". Confirm with a GitHub name search before creating the repo (§3.6) |
| 2 | Usenet client | Keep SABnzbd, "as is tradition" | Unchanged: SABnzbd in Phase 7; NZBGet in backlog |
| 3 | YouTube output | A setting; make it quite customisable | Output policy is per library with per-source override: codec (AAC, MP3, keep Opus), bitrate or VBR quality, container, sample rate, plus "never fake lossless"; ranked as the Opus-160 source regardless (§7.4, Phase 4) |
| 4 | Album policy | Both configurable; `fewest_albums` default | Unchanged; all four policies selectable per library and per song (§7.3) |
| 5 | slskd packaging | **Bundle from the start** | The app image ships slskd and supervises it as a child process from Phase 0; an "external slskd" mode remains for users who already run one (§9.1, §9.5, Appendix A) |
| 6 | Reference library | Reference by default; adopt/retag available | Unchanged (§7.6, Phase 3) |
| 7 | Match queue | Auto-accept above a high threshold, ask below | Unchanged (§7.6) |
| 8 | Port | 1077 if free | **1077 adopted**: no *arr-family application uses it (Sonarr 8989, Radarr 7878, Lidarr 8686, Readarr 8787, Prowlarr 9696, Bazarr 6767, Whisparr 6969, Seerr 5055, Tautulli 8181, slskd 5030); it is above the privileged range; IANA lists 1077 for an unrelated game service, which is irrelevant on a LAN (§9.2) |

The remaining open items are in §12.

---
## 1. Summary

Lidarr's unit of work is the MusicBrainz *release group* (an album, EP, or single). It monitors albums, searches indexers for albums, and imports files by matching them against an album's tracklist. That model cannot express "I want this one recording", which is why every attempt to use Lidarr for singles ends in unmonitored albums, partial-album import failures, or hundreds of one-track "singles" polluting Plex.

This document plans a new application whose unit of work is the **recording**. It reuses the *arr vocabulary and operating model (wanted lists, quality profiles, indexers/sources, download clients, completed-download handling, connect/notifications, import lists) but replaces the album-centred domain model, the search strategy, the decision engine, and the import pipeline with song-centred ones. Soulseek is the primary source because it is the only widely used network where individual tracks in high quality are the norm; YouTube (via yt-dlp and YouTube Music's catalogue) is the universal fallback at capped quality; torrents and usenet are supported as album-level sources with selective file download and wanted-song bundling to keep per-song cost low.

SoulSync and DroppedNeedle already implement large parts of this brief (§3.6); the recorded decision (§0) is to build new, in C#/.NET aligned with the other *arrs, porting useful code from Lidarr/Prowlarr and the MIT-licensed matching logic of SoulSync and spotDL.

The plan below is organised as: research findings (§3) → product definition (§4) → architecture, data model, API (§5) → matching/verification engine (§6) → library output including the Plexamp preset (§7) → stack recommendation (§8) → deployment (§9) → phased build plan with acceptance gates (§10) → risks (§11) → the questions I need you to answer (§12).
## 2. Goals, non-goals, and constraints

### Goals

- **G1. Song-level everything.** Add, monitor, search, download, verify, tag, file, upgrade, and report at the level of one recording. No album is ever required to exist for a song to be managed.
- **G2. Source ladder: Soulseek → YouTube → Torrent/Usenet.** Each source is a plugin behind one interface; the order and per-source rules are configurable per song via source profiles.
- **G3. Verified imports.** A song is only marked "has file" after the file has been probed, duration-checked, and (where possible) fingerprint-matched to the intended recording.
- **G4. Four library layouts** — flat, per-artist, per-artist/album, and a Plex/Plexamp preset — driven by one naming-template engine, with complete tags and artwork regardless of layout.
- **G5. *arr ergonomics.** Wanted / Queue / History / Blocklist / Quality Profiles / Root Folders / Connect / Import Lists, an `X-Api-Key` REST API under `/api/v1`, and Docker conventions self-hosters already know.
- **G6. Runs on a NAS.** One container plus slskd; SQLite; modest CPU and memory; no Redis/Postgres requirement.

### Non-goals (v1)

- Managing albums, discographies, or artist-level completeness (that is Lidarr's job; this app should coexist with Lidarr, not replace it).
- Being a music *player* or streaming server (Plex/Plexamp, Navidrome, Jellyfin do that).
- Acting as a Prowlarr "application" (we consume Torznab/Newznab endpoints; Prowlarr does not need to know about us).
- Bulk ripping of streaming services (Tidal/Qobuz/Deezer rippers) in v1. The plugin interface allows a community source later; it is not on the core roadmap.
- Multi-user request workflows (Overseerr-style) in v1.

### Constraints

- Must respect Soulseek's social contract: share content, one login per username, do not flood searches.
- MusicBrainz's public API allows about one request per second per client and requires a descriptive `User-Agent`; every metadata call is cached.
- YouTube is a moving target (bot checks, PO tokens, JS challenges); the YouTube source must degrade gracefully and never block the rest of the app.
- Torrent/usenet music releases are album-sized; per-song cost must be minimised (selective file download, bundling of wanted songs) and private-tracker rules must be respected.
## 3. Research findings

Six parallel investigations were run on 2026-09-28; each produced a URL-cited report in `research/`. This section condenses what changes the design. Where a claim rests on a page the sandbox could not fetch directly (MusicBrainz docs, Plex support, Spotify blog, slsknet.org, Servarr wiki), the report marks it and the wording should be re-verified before it goes into a spec.

### 3.1 Why Lidarr cannot do this, precisely (full notes: `research/research_arr.md`)

Lidarr's unit is the MusicBrainz release group. `Artist`, `Album` and `AlbumRelease` carry `Monitored` flags; `Track` does not ([Track.cs](https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Music/Model/Track.cs)). Search builds `artist + album + year` queries and maps results back to albums, rejecting anything that does not parse as "Artist – Album (Year)". Automatic import must match a release's full tracklist: `NoMissingOrUnmatchedTracksSpecification` rejects a new download that "Has missing tracks", and `CompletedDownloadService` marks any partial import as failed. `/api/v1/track` is read-only and the metadata proxy (`api.lidarr.audio`) has no track-title search. The official wiki states the position verbatim: "Individual tracks aren't addable in isolation" and "Singles-heavy libraries … can't be automated" ([lidarr/concepts.md](https://raw.githubusercontent.com/Servarr/Wiki/master/lidarr/concepts.md)); "Single tracks, partial releases, streaming-service rips, and per-track purchases are common plugin outputs that the core matcher wasn't designed to handle" ([import-troubleshooting.md](https://raw.githubusercontent.com/Servarr/Wiki/master/lidarr/import-troubleshooting.md)). Feature requests were closed as not planned in 2019 ([#826](https://github.com/Lidarr/Lidarr/issues/826)) and again within a day in December 2025 ([#5658](https://github.com/Lidarr/Lidarr/issues/5658)). Enabling "Single" release groups does not help: many songs have no single release group, one artist can have 50–100 singles that duplicate album tracks, single vs album title collisions cause wrong grabs, and the all-tracks import rule still applies.

**The right template is Sonarr's `Episode`, not Lidarr's `Album`:** the leaf entity has its own `Monitored` flag, `wanted/missing` returns leaf resources, and search criteria are per leaf. For songs the leaf identifier is the MusicBrainz recording (which Lidarr stores as `ForeignRecordingId` but never uses as a unit).

What is worth copying from the *arrs: quality definitions/profiles with cutoff and upgrade semantics, the grab-time specification list (size window derived from duration × bitrate, blocklist, already-grabbed, not-an-upgrade), completed-download handling with remote path mappings and hardlinks, the recycle bin, naming tokens, notification triggers, health checks, backups, the `release/push` contract (autobrr), `/api/v1/system/status` + `/queue/status` + `/wanted/missing` (which Homepage and Unpackerr read), Forms auth with "disabled for local addresses", and the Docker `/config` + single `/data` convention.
### 3.2 Soulseek via slskd (full notes: `research/research_soulseek.md`)

**slskd** (C#, AGPL-3.0 with additional terms, latest 0.26.0 of July 2026, .NET 10) exposes everything we need under `/api/v0` with an `X-API-Key` (roles `readonly`/`readwrite`/`administrator`) and Swagger at `/swagger` ([config.md](https://raw.githubusercontent.com/slskd/slskd/master/docs/config.md)). Facts that shape the integration:

- `POST /api/v0/searches` returns immediately and runs the search in the background; only **one POST may be in flight** (HTTP 429 otherwise) and the underlying Soulseek.NET client allows **two concurrent searches**, queuing the rest silently ([SearchesController.cs](https://raw.githubusercontent.com/slskd/slskd/master/src/slskd/Search/API/Controllers/SearchesController.cs)). `searchTimeout` is documented as seconds but is passed through as **milliseconds** (every client passes 5000–15000); it is measured from the last response received. Results stream over the SignalR hub `/hub/search` (one `RESPONSE` per peer, `UPDATE` on state change) or can be polled via `GET /searches/{id}/responses`. Searches are never pruned unless `retention.search` is set, so the app must `DELETE` its searches; a known "stuck search" bug means the app also enforces its own wall-clock cancel.
- Response objects carry per-user `hasFreeUploadSlot`, `uploadSpeed`, `queueLength`, and per-file `filename` (full backslash virtual path), `size` (bytes), `extension`, `bitRate?`, `sampleRate?`, `bitDepth?`, `length?` (seconds), `isVariableBitRate?`, `isLocked`. Protocol attribute sets differ by format: lossy files usually carry bitrate/duration/VBR, lossless carry duration/sample-rate/bit-depth, and any attribute may be missing (Sockseek's `AcceptMissingProps` default exists for this reason).
- Downloads: `POST /api/v0/transfers/downloads/batches` with one username per batch (201 all enqueued / 200 all failed / 207 partial); progress via `GET /transfers/downloads/{user}/{id}` (state strings like `"Completed, Succeeded"`, `"Queued, Remotely"`); `…/position` asks the peer for queue position; `DELETE` cancels. There is **no transfers hub**, so progress is polled; `DownloadFileComplete` webhooks (`integrations.webhooks`) give completion events. Since 0.26.0 the destination folder is templated (`transfers.download.destination.subdirectory`, tokens `${BATCH_EXTERNAL_ID}`, `${SOURCE_DIRECTORY}` …) or set per batch via `options.destination`, so each grab can land in a deterministic folder; older versions drop files into `downloads/<remote folder name>/`.
- The API is declared **v0/unstable** and has broken downstream tools before (a 0.22.x response-shape change broke soularr), so the client is isolated behind an interface and version-checked at startup.
- slskd does **not** reconnect after a duplicate-login kick ("another client logged in using the same username"), so the health check must surface "logged out" and the docs must insist on a dedicated Soulseek account.

**Network rules the design must respect.** One login per username (the server kicks the earlier session); sharing is enforced client-side by almost every peer (slskd's own `leechers` group throttles non-sharers to 1 slot at 100 KiB/s), so the slskd we drive must share a real library; the server bans clients that search too fast (Sockseek's limiter defaults to **34 searches per 220 s** because "higher values may cause 30-minute bans"; soularr enforces a 5 s minimum interval); results arrive asynchronously per peer so "complete" is a timeout heuristic; wishlist re-searches on the network run every 12 minutes (2 for privileged users), a sensible floor for our own re-search cadence ([SLSKPROTOCOL.md](https://github.com/nicotine-plus/nicotine-plus/blob/master/doc/SLSKPROTOCOL.md), [Sockseek README](https://raw.githubusercontent.com/fiso64/sockseek/master/README.md)).

**How the best existing tools match single tracks.** Sockseek (ex-sldl, C#, 1.1k★, v3 with an experimental daemon API) searches `artist title` lower-cased with special characters stripped plus a diacritics-free variant, filters "necessary" conditions at receive time (formats, length ±3 s), and ranks by: user success history → necessary conditions met → preferred user → has length → bracket check (no `[...]`/`(...)` noise in the filename) → strict/fuzzy title, album, artist containment → length tolerance → format/bitrate/sample-rate/bit-depth preferences → free upload slot → coarse upload-speed buckets → bitrate → deterministic tiebreak. Queue length is *not* a key (slskdN and SoulSync do use it). Fast-search starts a download as soon as a candidate with a free slot and ≥ 1 MB/s passes preferred conditions. soularr (album-only) uses `difflib` ratio ≥ 0.8 for title matching. §6 adopts Sockseek's ordering as the baseline with queue length and reputation added.

**Landscape on Soulseek specifically.** Almost every automation tool drives slskd over HTTP (SoulSync, DroppedNeedle, Explo, Tubifarry, soulbeet, soularr, waves); only Sockseek and Kima Hub embed a client. Embeddable libraries: Soulseek.NET (C#, proven, but GPL-3.0-only with policed version terms and an unlisted NuGet package), aioslsk (Python, Beta, full-featured), soulseek-rs-lib (Rust, active, JSON-RPC daemon), slsk-client (Node, weeks-old rewrite); Go has nothing mature.
### 3.3 YouTube / YouTube Music via yt-dlp (full notes: `research/research_youtube.md`)

- **yt-dlp** (2026.08.19; 10 releases so far in 2026, 27 in 2025) needs an external JavaScript runtime for full YouTube support since 2025.11.12: Deno is the default (≥ 2.3.0), Node ≥ 22 works; `pip install "yt-dlp[default]" deno` gives a self-contained container ([EJS wiki](https://github.com/yt-dlp/yt-dlp/wiki/EJS)). Expect emergency releases; auto-update at least monthly.
- **Formats.** Universal audio: itag 251 Opus (~128–160 kbps VBR, 48 kHz) and 140 AAC-LC 128 kbps; 141 AAC-256 / 774 Opus-256 need Premium cookies and have repeatedly regressed in 2025 ([#14208](https://github.com/yt-dlp/yt-dlp/issues/14208)), so 256 kbps is opportunistic. `-x` defaults to `bestaudio`, which resolves to 251; `-x --audio-format opus` (or `best`) does a **lossless remux** of webm/Opus into `.opus` (`-acodec copy`) and leaves `.m4a` untouched. Never store `.webm` (Plex will not scan it).
- **YouTube Music search** (ytmusicapi 1.12.3, no auth needed for search): `search(query, filter="songs", ignore_spelling=True)` returns `videoId`, `title`, `artists`, `album`, `duration_seconds`, `isExplicit`; `resultType == "song"` / `videoType == ATV` identifies **Art Tracks**, the auto-generated "Provided to YouTube by … Auto-generated by YouTube" uploads on "- Topic" channels whose audio is the delivered master and whose length matches the catalogue (the classic hazard from ytmusicapi's own docs: Wonderwall song 4:19 vs video 4:38). ISRC strings work as search queries (spotDL relies on it). yt-dlp parses Art-Track descriptions into `track`/`artists`/`album`/`release_year` automatically.
- **spotDL's matching rules** (captured exactly): ISRC search first; then `songs`, then `videos`; return immediately when a *verified* (song-type) result scores ≥ 80; name match via slugified token-sort ratio; −15 per forbidden word (`remix`, `remastered`, `live`, `acoustic`, `instrumental`, `cover`, `slowed`, `8daudio` …); duration score `exp(−0.1·Δs)·100` (3 s → 74, 5 s → 61, 10 s → 37) with rejections at < 25 and at < 50 unless the name/artist average is ≥ 75; −5 when explicit flags disagree; +15 view-count bonus among near-ties ([matching.py](https://raw.githubusercontent.com/spotDL/spotify-downloader/master/spotdl/utils/matching.py)).
- **Bot checks and PO tokens.** YouTube binds PO tokens to video IDs; the recommended setup is the **bgutil-ytdlp-pot-provider** sidecar (v2.0.0, Sept 2026, HTTP mode on port 4416, binds localhost) feeding the `mweb` client, plus cookies exported from a private browser window on a throwaway account; bgutil itself says a token "does not guarantee bypassing 403 errors or bot checks" ([PO Token Guide](https://github.com/yt-dlp/yt-dlp/wiki/PO-Token-Guide), [bgutil](https://github.com/Brainicism/bgutil-ytdlp-pot-provider)). Documented rate limits: ~300 videos/hour for guests, ~2000 with an account; the `-t sleep` preset (`--sleep-requests 0.75 --sleep-interval 10 --max-sleep-interval 20`) is the recommended pacing; datacenter/VPN egress is challenged far more than residential (secondary sources only).
- **Closest prior art**: Angrido's Lidarr-YouTube-Downloader (v1.9.1, Sept 2026) already does per-track yt-dlp search with title/duration/official-channel scoring, optional AcoustID verification against MusicBrainz recording IDs, mutagen tagging with MBIDs and a bundled bgutil sidecar, disguised as a Newznab indexer + SABnzbd client so Lidarr can drive it. Tubifarry (Lidarr plugin, 1k★) and LidaTube are album-level.
- **Other sources** for a later plugin: SoundCloud via yt-dlp (128 kbps MP3 / 64 kbps Opus free; 256 kbps AAC with Go+), Bandcamp via yt-dlp (128 kbps streams free; FLAC for purchased items with cookies — the cleanest *legal* lossless source), Deezer (community deemix fork and deezspot alive but fragile), Tidal/Qobuz rippers (streamrip; Qobuz has been banning ripper accounts since October 2025). All subscription rippers violate the services' terms even with a paid account; they stay out of core.
- **Legal note.** YouTube's Terms of Service prohibit downloading and automated access except where YouTube permits it; consequences seen in practice are account bans and IP blocks. Product implications: the YouTube source ships but must be enabled by the user, uses only user-supplied cookies/tokens, never bundles accounts, and shows a disclaimer.
### 3.4 Torrents and Usenet for one song (full notes: `research/research_torrent_usenet.md`)

**Track-level search does not exist in practice.** The Newznab `t=music` function defines `q, artist, album, label, track, year, genre` ([spec](https://github.com/torznab/torznab.github.io/blob/master/spec-1.3-draft/external/newznab/api.html)) and Prowlarr plumbs `track=` through, but of 557 Prowlarr indexer definitions, 392 advertise music search, 376 of those support only `q`, and **none** declare `track` ([Prowlarr/Indexers](https://github.com/Prowlarr/Indexers)). Redacted and Orpheus map only artist/album/year. The only real per-track search is Gazelle's `filelist=` browse parameter, which matches file names inside torrents and, via `action=torrent`, returns every file's name and byte size before download ([Gazelle JSON API](https://github.com/WhatCD/Gazelle/wiki/JSON-API-Documentation)); Prowlarr does not expose it, so a "Gazelle direct" indexer type is worth having. Prowlarr's own `GET /api/v1/search?query=&type=music&indexerIds=&categories=` needs only an API key, returns `guid, size, files, seeders, protocol, downloadUrl, infoHash, magnetUrl…`, and caches results for 30 minutes; no app registration is required ([openapi](https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Api.V1/openapi.json)). Audio categories: 3000 Audio, 3010 MP3, 3020 Audio/Video, 3030 Audiobook, 3040 Lossless, 3050 Other, 3060 Foreign.

**Selective single-file download works on every major client.** Pattern: add stopped → read file list → set unwanted files to priority 0 → start → wait for the *finished* state (all wanted pieces) → import the one file. qBittorrent: `torrents/add` with `stopped`/`paused` (5.x renamed pause→stop; states `stoppedUP` etc., API 2.11+), `stopCondition=MetadataReceived` for magnets, `torrents/files`, `filePrio` (0/1/6/7), `torrents/start` ([qBittorrent WebUI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-5.0))). Transmission: `files-unwanted` on `torrent-add` works for `.torrent` metainfo but is **not honoured for magnets** until metadata arrives (verified in `libtransmission/torrent.cc`), so poll `metadataPercentComplete` then `torrent-set` ([rpc-spec](https://github.com/transmission/transmission/blob/main/docs/rpc-spec.md)). Deluge: `file_priorities` [0,1,4,7] plus `core.prefetch_magnet_metadata`. rTorrent: `f.priority.set` then the mandatory `d.update_priorities`. Skipped files still receive boundary pieces (documented by rTorrent and aria2; BEP 47 padding removes it), so import by file index, never "everything in the folder".

**Partial downloads never count as seeding on Gazelle trackers.** libtorrent announces `left` over all pieces, and Ocelot files any peer with `left > 0` as a leecher and only records a snatch at `left == 0` ([Ocelot worker.cpp](https://github.com/WhatCD/Ocelot/blob/master/worker.cpp)). A one-track grab costs ratio for the bytes fetched and earns no seed credit; RED/OPS rule text on partial seeding could not be retrieved (sites blocked), so partial-download policy must be a per-indexer user setting with "off" as the default for private trackers.

**Usenet cannot do true single-track fetches in general.** An NZB is per-file XML, and both clients allow per-file trimming (SABnzbd `get_files` + `delete_nzf`; NZBGet `listfiles` + `editqueue FileDelete/FilePause`), but music posts are commonly RAR-packed and/or obfuscated (hash-named files whose real names live in par2/RAR headers; posting tools like ngPost obfuscate subjects on purpose), so the wanted track is unidentifiable before download. Default: download the whole post, unpack, keep the wanted track(s); attempt per-file trimming only when every `<file subject>` carries a plausible per-track audio filename and no archive volumes exist ([SABnzbd API](https://github.com/sabnzbd/sabnzbd.github.io/blob/master/wiki/configuration/5.2/api.html), [NZBGet API](https://github.com/nzbgetcom/nzbget/tree/develop/docs/api)).

**No prior art.** Lidarr rejects partial-album imports outright (`NoMissingOrUnmatchedTracksSpecification`, "Has missing tracks") and closed per-track monitoring as not planned ([Lidarr#826](https://github.com/Lidarr/Lidarr/issues/826)); Headphones, soularr and Tubifarry are album-based. Torrents expose only file name and byte size, so pre-download matching uses title/track-number fuzzy match plus a size window derived from the MusicBrainz duration × the quality tier's bitrate, with real verification after download. Lidarr's release-name regexes, `QualityParser` codec/bitrate rules and its quality definitions with weights are captured in the research notes and are reused for our quality seed (Appendix B).
### 3.5 Song identity, verification, tagging, and Plex (full notes: `research/research_metadata_plex.md`)

**Identity.** MusicBrainz's *recording* is the entity that means "this specific audio"; a *track* is a recording's appearance on one release, a *release group* is the album concept, a *work* is the composition. Per the MusicBrainz recording style guide, remasters are **not** separate recordings, while radio/single edits, remixes and live versions **are** ([Style/Recording](https://musicbrainz.org/doc/Style/Recording)). So the wanted item is a recording MBID, the app also persists the chosen release, release-group and release-track MBIDs plus ISRCs, and the UI must show disambiguation, length and release types so the user can distinguish "album version" from "single edit". WS/2 supports recording search with `recording:`, `artist:`, `isrc:`, `dur:`, `primarytype:`, `status:` fields, `inc=artist-credits+isrcs+releases+release-groups` lookups, and `/ws/2/isrc/{isrc}`; the limit is about 1 request/s per IP with a mandatory descriptive `User-Agent`, and HTTP 503 on excess ([MusicBrainz API](https://musicbrainz.org/doc/MusicBrainz_API), [rate limiting](https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting)). Cover art: `coverartarchive.org/release-group/{mbid}/front-500` (307 redirect), falling back to the release, then Deezer/iTunes. Lidarr's `api.lidarr.audio` proxy (MusicBrainz Postgres + Solr + fanart/TheAudioDB/Spotify/Wikipedia providers with cache TTLs) is the caching model to copy at small scale: a local cache keyed by MBID with TTLs.

**Verification.** `fpcalc -json` fingerprints the first 120 s; AcoustID `lookup?client=&fingerprint=&duration=&meta=recordingids` returns candidates with a 0–1 score and recording MBIDs; limit 3 requests/s, HTTP 429 on excess ([acoustid-server API source](https://github.com/acoustid/acoustid-server/blob/master/acoustid/api/v2/__init__.py), [pyacoustid](https://github.com/beetbox/pyacoustid)). Accept when the wanted recording MBID appears with score ≥ ~0.7 (configurable; 0.5–0.7 flagged for review) and duration is within tolerance. Known failure modes: pitch/speed-shifted YouTube uploads (a 2.5 % speed change defeats matching), very short files, and recordings with no AcoustID coverage.

**Supplementary sources (2026 status).** Spotify's Web API is effectively owner-only for new apps (development mode: 5 users, owner must have Premium, audio-features/recommendations/previews removed for new apps; playlists, saved tracks and `isrc:` search still work) ([Spotify blog Nov 2024](https://developer.spotify.com/blog/2024-11-27-changes-to-the-web-api), [quota update Jul 2026](https://developer.spotify.com/blog/2026-07-23-web-api-quota-updates)). Deezer no longer issues tokens to individuals but unauthenticated catalogue reads, `/track/isrc:{ISRC}`, 30-second previews and 1000 px art still work (~50 requests per 5 s). iTunes Search needs no auth (≈20 calls/min, previews, artwork via the `3000x3000bb` token, no ISRC lookup). Last.fm (5 req/s TOS), ListenBrainz (open, MBID-native), Discogs (60/min), TheAudioDB (30/min free key), fanart.tv (project key, MBID-keyed artist art) and LRCLIB (free synced lyrics, honour `Retry-After`) remain usable. Genius has no lyrics in its API.

**Tagging.** Picard's mapping is the convention every scanner reads ([tag mapping](https://picard-docs.musicbrainz.org/en/appendices/tag_mapping.html)): the recording MBID goes in Vorbis `MUSICBRAINZ_TRACKID` / MP4 `MusicBrainz Track Id` / ID3 `UFID:http://musicbrainz.org`; the release-track MBID in `MUSICBRAINZ_RELEASETRACKID`; release, release-group, artist and album-artist IDs in their `MUSICBRAINZ_*` fields; ID3v2.4 uses `TDRC`/`TDOR` where v2.3 uses `TYER`/`TORY`; Opus uses `R128_TRACK_GAIN` rather than ReplayGain; MP4 `covr` accepts only JPEG/PNG. Navidrome and Jellyfin read exactly this convention. mutagen (Python), ATL (.NET) and lofty (Rust) write all four containers natively.

**Plex Music / Plexamp.** Plex expects `Music/Artist/Album/NN - Title.ext` and groups by embedded tags first: the Album Artist and Album tags must be identical across an album's files, track/disc numbers must be set, and compilations need `Album Artist = Various Artists` (Plex ignores the iTunes compilation flag) ([Adding music from folders](https://support.plex.tv/articles/200265296-adding-music-media-from-folders/), [embedded metadata](https://support.plex.tv/articles/200381093-identifying-music-media-using-embedded-metadata/)). Local Media Assets must be enabled for embedded tags, `cover.jpg`/`folder.jpg`, `artist.jpg` and `.lrc`/`.txt` sidecars (same base name as the track) to be used ([local lyrics and artwork](https://support.plex.tv/articles/215916117-adding-local-lyrics/)). Fix Match accepts a *release* MBID; automatic use of embedded MBIDs is undocumented, so consistent MBIDs and identical date strings across an album's files are cheap insurance. Singles have no special layout: a single is a one-file album folder, and Plexamp only files it under "Singles & EPs" when the Plex Music agent matches it online, hence the community "Artist – Singles" pseudo-album workaround. Partial scan: `GET /library/sections/{id}/refresh?path=` with `X-Plex-Token` (what Lidarr does); playlist push: `POST /playlists/upload?sectionID=&path=` with a server-side `.m3u` path, or build the playlist from rating keys. Supported music formats include FLAC, ALAC, MP3, AAC, OGG Vorbis, Opus, WAV and AIFF; Opus direct-play in Plexamp is device-dependent, so FLAC and AAC/MP3 are the safe library formats. Sonic Analysis, loudness analysis and Sweet Fades are server-side Plex Pass features; Plex ignores ReplayGain tags (Navidrome and Jellyfin honour them).

**Naming.** Lidarr's tokens (`{Artist Name}`, `{Artist CleanName}`, `{Album Title}`, `{Release Year}`, `{track:00}`, `{medium:0}`, `{Track Title}`, `{Quality Full}`, `{MediaInfo AudioCodec}` …) and its default `{Album Title} ({Release Year})/{Artist Name} - {Album Title} - {track:00} - {Track Title}` are the syntax *arr users know; Picard's default script produces `AlbumArtist/Album/NN Title` and beets uses `$albumartist/$album/$track $title` with `Non-Album/$artist/$title` for singletons. The template engine in §7 adopts Lidarr's `{Token}` syntax with a superset of tokens.
### 3.6 Landscape, overlap, and names (full notes: `research/research_arr.md` §3, `research/research_soulseek.md` §2.3)

**Closest existing projects** (state as of 2026-09-28):

| Project | What it is | Overlap with this brief | What it lacks vs this brief |
|---|---|---|---|
| **SoulSync** ([Nezreka/SoulSync](https://github.com/Nezreka/SoulSync), MIT, Python 3.11 Flask + React 19, 2.2k★, v3.4.7) | Playlist/wishlist-driven downloader: sources Soulseek (slskd), Tidal, Qobuz, Deezer, HiFi, YouTube, SoundCloud, Lidarr, torrents (Prowlarr + qBittorrent/Transmission/Deluge/aria2) and usenet (Prowlarr + SABnzbd/NZBGet) in a user-ordered "hybrid chain"; quality profiles as a ranked ladder with cutoff and "upgrade until cutoff"; wishlist "retried automatically with progressive backoff"; naming templates; Picard-style MusicBrainz tagging via mutagen; LRCLIB synced lyrics; cover art; ReplayGain; AcoustID, ffmpeg real-audio checks, fake-lossless detector; Plex/Jellyfin/Navidrome; REST API at `/api/v1` | Very high: source ladder, profiles, upgrades, verification, tagging, lyrics, torrents/usenet, Plex | Not *arr-shaped (no Wanted/Queue/History/Blocklist model, no `X-Api-Key` conventions, no interactive search with rejection reasons); song-first add flow not documented (requests are links/playlists/files); no documented Plexamp preset or release-context policy; selective single-file torrent download and wanted-song bundling not documented; broad scope (six streaming sources, podcasts, audiobooks) |
| **DroppedNeedle** ([DroppedNeedle/DroppedNeedle](https://github.com/DroppedNeedle/DroppedNeedle), AGPL-3.0 + commercial, 1.4k★) | "Whole albums or single tracks from the MusicBrainz catalogue" fulfilled via slskd, SABnzbd (Newznab/Prowlarr) and Internet Archive; per-format quality floors with automatic upgrades; failed requests re-searched automatically; verification (tags, duration, optional fingerprint); Picard-style tags; OpenSubsonic/Jellyfin player APIs | High on identity (MB catalogue) and verification | No torrents, no YouTube; it is also a music server/player; AGPL with commercial licensing; no *arr conventions or layout presets |
| Sockseek ([fiso64/sockseek](https://github.com/fiso64/sockseek), AGPL, C#, 1.1k★) | The strongest Soulseek single-track search/rank/download engine; v3 adds an experimental daemon HTTP API and job engine | Matching engine | Not a library manager; embeds Soulseek.NET (licence terms); does not share files itself |
| Kima Hub, Musicarr (benjamin-decreusefond), Explo, soulbeet, downtify, waves | Per-track Soulseek downloaders for specific workflows (Spotify playlists, ListenBrainz recommendations, beets, TUI) | Per-file slskd download | No profiles/upgrades, no multi-source ladder, no *arr model |
| soularr, Tubifarry, Tunearr, Lidarr-YouTube-Downloader (Angrido), LidaTube | Feed Lidarr (album-level) from Soulseek/YouTube, some via a fake Newznab indexer + SABnzbd client façade | Integration tricks | All bound by Lidarr's album import rules |
| Songarr ×4 (2026) | One is a *planned* "track-first request manager" (Prowlarr → qBittorrent with other files disabled); others delegate to Lidarr or are Subsonic proxies | The selective-file idea | Nothing usable released |
| Lidarr plugins branch, Seerr | Lidarr 3.1.x nightly has plugins (Tubifarry, Tidal/Qobuz/Deezer, slskd); Seerr merged Lidarr requests (artists/albums) | — | Album-level throughout |

**Positioning.** The unmet gap is an *arr-native, song-first* manager: a recording-identified Wanted list with release-context choice, an interactive search that shows every candidate's score and rejection reasons, a decision engine and upgrade loop with *arr semantics, torrent/usenet handled with selective file download and bundling, and library presets (including Plexamp) driven by one template engine, behind an API that dashboards, Unpackerr and autobrr already understand. SoulSync's matching, verification and fake-lossless logic is MIT-licensed and is ported (§8.4); forking SoulSync itself was considered and declined (§0).

**Name collisions.** Taken: Songarr (four 2026 repos), Trackarr (188★ private-tracker tool plus several dashboards), Tunearr (active slskd→Lidarr bridge), Musicarr (several, one per-track), Melodarr (Lidarr fork + GitHub org), Listenarr (900★ audiobook *arr), Audiarr, Spotarr (Docker Hub), Singlarr and Soundarr (tiny). Free on GitHub as of 2026-09-28: **Tracksarr**, **Songdarr**; web search also found nothing for **Cratearr** (crate-digging; DJs' crates are single tracks), **Jukearr** (a jukebox plays singles) and **Recordarr** (the MB "recording" is the unit), but those three were only checked by web search, so confirm with a GitHub name search before committing. Round 2 checks: **Syncarr** is taken (`syncarr/syncarr`, a Radarr/Sonarr/Lidarr instance-sync tool, plus forks) and the word says "sync" rather than "acquire"; **Seekarr** is taken by at least four *arr companion tools that trigger missing-item searches ([scottrobertson/seekarr](https://github.com/scottrobertson/seekarr), [tumeden/seekarr](https://github.com/tumeden/seekarr) with releases, [diybits/seekarr](https://github.com/diybits/seekarr), [matthw-labs/seekarr](https://github.com/matthw-labs/seekarr)), so it would be confused with them inside the very ecosystem this app lives in. Round 3: the owner proposed **Comparr** or **Compilarr**. Web search finds no GitHub project for either; "Comparr" collides with a French price-comparison site (comparr.fr) and reads as "compare", while **Compilarr** is free, says what the app does (it compiles single songs into a library), and is adopted as the working name pending a GitHub name search at repo creation.
### 3.7 Stack landscape (full notes: `research/research_stack.md`)

See §8 for the decision. The two findings that shaped it: the *arrs share no reusable core package and Lidarr would need a rewrite rather than a refactor to become song-centred; and Soulseek.NET, the only mature embeddable Soulseek client, now carries policed licence terms and an unlisted NuGet package, which makes "talk to slskd over HTTP" the right integration regardless of language. Every candidate language can write tags to MP3/FLAC/M4A/Opus today (mutagen, ATL, go-taglib, taglib-wasm), so tagging no longer decides the language; the canonical yt-dlp / ytmusicapi / plexapi / pyacoustid libraries and the Bazarr precedent do.
## 4. Product definition

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
## 5. Architecture

### 5.1 Components

```
                       ┌──────────────────────────────────────────────────────────┐
                       │                       Web UI (SPA)                        │
                       │  Songs · Artists · Wanted · Queue · History · Activity     │
                       │  Interactive search · Libraries · Profiles · Settings      │
                       └───────────────▲──────────────────────────────▲───────────┘
                                       │ REST /api/v1 (X-Api-Key)      │ WebSocket/SSE events
┌──────────────────────────────────────┴──────────────────────────────┴───────────────────────┐
│                                        Core service                                          │
│                                                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  ┌───────────────┐  ┌───────────┐ │
│  │ Identity &   │  │ Search       │  │ Decision engine  │  │ Grab & Queue  │  │ Import    │ │
│  │ Metadata     │  │ orchestrator │  │ (score/reject)   │  │ tracker       │  │ pipeline  │ │
│  └──────┬───────┘  └──────┬───────┘  └────────┬─────────┘  └──────┬────────┘  └─────┬─────┘ │
│         │                 │                   │                   │                 │       │
│  ┌──────▼─────────────────▼───────────────────▼───────────────────▼─────────────────▼─────┐ │
│  │                      Scheduler / job runner  ·  Event bus  ·  SQLite                    │ │
│  └─────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                              │
│  Plugin interfaces:                                                                          │
│   MetadataProvider   SourceProvider   DownloadClient   ImportList   LibraryLayout   Notifier  │
└─────┬──────────────────────┬─────────────────┬───────────────┬──────────────┬────────────────┘
      │                      │                 │               │              │
 MusicBrainz + CAA      slskd (HTTP)      qBittorrent     Spotify/Deezer   Plex (scan,      Discord,
 AcoustID, Deezer,      yt-dlp + ytmusic  Transmission    YT Music, CSV    playlist)        webhook,
 Spotify, LRCLIB        Prowlarr/Torznab  Deluge/rTorrent Last.fm, LB      m3u8 export      Apprise…
 (Wikidata/fanart opt.) Newznab           SABnzbd/NZBGet
```

The core is a single long-running process (one container). Soulseek access goes through a **companion slskd container that the app configures** (credentials, shares, slots; §9.5); torrent/usenet clients are the user's existing ones; YouTube is handled in-process via yt-dlp. Nothing else is required to run.

### 5.2 The song lifecycle (pipeline)

```
 add ──► resolve identity ──► wanted ──► search ──► decide ──► grab ──► track ──► verify ──► tag ──► place ──► notify
  ▲                                       ▲                                                  │
  │                                       └────────── retry / next candidate / next source ◄─┘ (on failure)
  │
 import lists (playlists, loved tracks, CSV, artist top-N)          upgrade loop: cutoff unmet ──► search again (backoff)
```

1. **Add.** From UI search, API, or an import list. Input is either an identity (MBID/ISRC/Spotify/Deezer id) or free text.
2. **Resolve identity.** Look up MusicBrainz first (recording search or ISRC lookup); fall back to Deezer/Spotify for songs MB does not have; store *all* known ids. Assign the album context under the library's album policy (§7.3: fewest albums per artist by default). Fetch duration, cover art, artist ids.
3. **Wanted.** Song is monitored and has no file (Missing) or a file below cutoff (Cutoff Unmet).
4. **Search.** The orchestrator asks each enabled source in profile order. Sources are run *sequentially by tier* (Soulseek first; only if it yields nothing acceptable does YouTube run; torrents/usenet last) unless the user runs an interactive search, which fans out to all sources in parallel. Each source returns normalised `Candidate`s.
5. **Decide.** Reject candidates that violate hard rules; score the rest; pick the best (section 6).
6. **Grab.** Hand the candidate to its source's grab implementation (slskd enqueue, yt-dlp job, download-client add with file selection). Record a Queue item and a History "grabbed" event.
7. **Track.** Poll/subscribe for progress. Handle stalls (Soulseek user went offline, torrent no seeds, yt-dlp bot-check) with per-source timeouts, then fall back to the next candidate.
8. **Verify.** ffprobe (decodable, duration within tolerance, real codec/bitrate/sample rate). Chromaprint fingerprint → AcoustID → expected recording MBID (or a recording of the same work/title/artist). Reject → blocklist candidate → back to step 5 with the remaining candidates.
9. **Tag.** Write the full tag set (section 7.4), embed cover art, optionally write lyrics and ReplayGain.
10. **Place.** Render the naming template for the library's layout, move/hardlink, set permissions, write sidecars (`cover.jpg`, `.lrc`) where the layout wants them.
11. **Notify.** Plex partial scan, webhooks, Discord, etc. Emit history "imported" (or "upgraded", in which case the old file is recycled).

### 5.3 Plugin interfaces

All interfaces are small and synchronous-in-spirit (async in implementation). Each source implementation lives in its own module and is registered by name; settings are stored as JSON per instance so the UI can render forms from a schema.

```
MetadataProvider
  search_recordings(query|artist+title) -> [RecordingMatch]
  lookup(ids: {mbid|isrc|spotify|deezer|ytm}) -> SongIdentity
  releases_for(recording) -> [AlbumContext]        # album/single choices with track numbers and cover
  cover_art(album_context, size) -> bytes|url

SourceProvider                                       # one per source *type*; instances configured by user
  capabilities: {single_file: bool, album_container: bool, needs_download_client: bool}
  search(song, source_profile) -> [Candidate]        # must return within a budget (e.g. 20 s Soulseek, 10 s YT)
  grab(candidate) -> GrabHandle                      # enqueue and return a handle to track
  status(handle) -> {state, progress, bytes, eta, message}
  cancel(handle)
  completed_path(handle) -> Path                     # where the file(s) landed for import
  blocklist_key(candidate) -> str                    # slskd: user+path; YT: video id; torrent: infohash+file

DownloadClient                                       # only for torrent/usenet sources
  add(payload, category, paused) -> client_id
  files(client_id) -> [{index, path, size}]
  set_wanted_files(client_id, [index])
  resume / pause / remove(client_id, delete_data)
  status(client_id) -> {state, progress, save_path, files_done}

ImportList
  fetch() -> [SongRequest]                            # (identity ids | artist+title+duration), plus list metadata
  # sync policy: add-only | add-and-unmonitor-removed | mirror

LibraryLayout
  render_path(song, file_info, template_vars) -> relative path
  sidecars(song) -> [(relative path, content)]        # cover.jpg, .lrc, artist.jpg…
  post_place_hooks(song) -> []                        # e.g. plex partial scan for this folder

Notifier
  on_grab / on_import / on_upgrade / on_failure / on_health(event)
```

### 5.4 Data model (SQLite; every table has `id`, `created_at`, `updated_at`)

| Table | Key columns |
|---|---|
| `artist` | `name`, `sort_name`, `mb_artist_id?`, `spotify_id?`, `deezer_id?`, `monitored_top_n?`, `tags` |
| `song` | `title`, `artist_credit` (display), `primary_artist_id`, `mb_recording_id?`, `mb_work_id?`, `isrcs` (json), `spotify_id?`, `deezer_id?`, `ytm_video_id?`, `duration_ms?`, `version_flags` (json: live/remix/acoustic/instrumental/radio_edit/remaster/explicit/cover), `album_context_id`, `monitored`, `quality_profile_id`, `source_profile_id`, `library_id`, `file_id?`, `added_by` (ui/api/list:{id}), `tags` |
| `song_artist` | `song_id`, `artist_id`, `role` (main/featured), `position` |
| `album_context` | `song_id`, `kind` (album/single/ep/compilation/pseudo_singles), `album_title`, `album_artist`, `album_key` (real release MBID or synthetic pseudo-album UUID; one per folder), `mb_release_group_id?`, `track_no?`, `disc_no?`, `total_tracks?`, `date` (identical per folder), `original_date?`, `label?`, `cover_url?`, `is_various_artists`, `sticky` |
| `song_file` | `song_id`, `path`, `size`, `codec`, `container`, `bitrate_kbps`, `sample_rate`, `bit_depth?`, `channels`, `duration_ms`, `quality_id`, `acoustid?`, `fingerprint_verified` (bool), `source_type`, `source_ref` (json), `imported_at`, `tags_written` (json snapshot) |
| `quality` (seed) | `name`, `codec`, `lossless`, `rank`, `min_bitrate`, `max_bitrate`, `bit_depth?` |
| `quality_profile` | `name`, `items` (json ordered allowed qualities), `cutoff_quality_id`, `upgrade_allowed`, `min_score`, `duration_tolerance_ms` |
| `source_profile` | `name`, `order` (json: source instance ids), `rules` (json: per-source overrides) |
| `source_instance` | `type` (slskd/youtube/torznab/newznab), `name`, `settings` (json), `enabled`, `priority`, `download_client_id?` |
| `download_client` | `type`, `name`, `settings` (json), `category`, `remote_path_mappings` (json), `enabled` |
| `library` | `name`, `root_path`, `layout` (flat/artist/artist_album/plexamp), `naming_template`, `sidecar_options` (json), `album_policy` (fewest_albums/singles_only/original_album/single_release), `min_tracks_per_real_album`, `plex_section_id?`, `is_default` |
| `import_list` | `type`, `name`, `settings` (json), `sync_interval`, `policy`, `quality_profile_id`, `library_id`, `last_synced_at` |
| `import_list_item` | `import_list_id`, `external_id`, `song_id?`, `raw` (json), `state` (added/unresolved/skipped) |
| `search_run` | `song_id`, `trigger` (auto/manual/upgrade/list), `started_at`, `finished_at`, `sources` (json), `candidate_count`, `outcome` |
| `candidate` | `search_run_id`, `song_id`, `source_instance_id`, `blocklist_key`, `normalised` (json: title/artist/album guess, duration, codec, bitrate, sr, depth, size, availability metrics), `score`, `rejections` (json), `grabbed` |
| `queue_item` | `song_id`, `candidate_id`, `source_instance_id`, `handle` (json), `state` (queued/downloading/paused/completed/importing/failed), `progress`, `bytes`, `eta`, `message`, `attempts`, `next_check_at` |
| `history` | `song_id`, `event` (grabbed/imported/upgraded/failed/verified/rejected/deleted/renamed), `source_instance_id?`, `data` (json), `quality_id?` |
| `blocklist` | `song_id?`, `source_type`, `blocklist_key`, `reason`, `expires_at?` |
| `metadata_cache` | `provider`, `key`, `payload` (json), `fetched_at`, `ttl` |
| `notification` | `type`, `name`, `settings` (json), `events` (json) |
| `job` | `name`, `interval`, `last_run_at`, `next_run_at`, `last_result` |
| `reference_library` | `name`, `root_path`, `mode` (reference/adopt), `library_id?` (target when adopting), `last_scanned_at` |
| `reference_file` | `reference_library_id`, `path`, `size`, `mtime`, `probe` (json), `fingerprint?`, `song_id?`, `confidence`, `state` (identified/ambiguous/unmatched/adopted) |
| `match_candidate` | `reference_file_id`, `identity` (json: mbid/isrc/deezer id), `score`, `reason` |
| `slskd_state` | `version`, `logged_in`, `sharing`, `shared_dirs` (json), `restart_required`, `last_checked_at` |
| `setting` | `key`, `value` (json) — general settings, API key, auth, url base |

### 5.5 Scheduler and jobs

| Job | Default interval | Notes |
|---|---|---|
| Wanted search (missing) | every 6 h, plus immediately on add | Per-song backoff after repeated failures: 1 h → 6 h → 24 h → 72 h → weekly (capped); "search on add" configurable |
| Cutoff-unmet upgrade search | every 24 h | Bounded per run (e.g. max 50 songs) to be polite to Soulseek |
| Queue poll | every 10 s (active) / 60 s (idle) | Progress + completion detection; per-source stall timeouts |
| Import scan of completed folder | on completion event + every 5 min | Catches downloads finished while the app was down |
| Import list sync | per list (default 12 h) | Adds new items; policy decides about removals |
| Reference library scan | daily + on demand | Detects new/changed files; identification pipeline; feeds the Match queue |
| Compact library | on demand | Re-plans album assignments under the `fewest_albums` policy and applies moves with the Plex scan/empty-trash sequence |
| Metadata refresh | weekly | Re-pull cover/ISRC/durations for songs missing them |
| Housekeeping | daily | Vacuum, expire blocklist entries, prune old candidates/search runs |
| Health checks | every 15 min | slskd reachable & logged in; clients reachable; root folders writable; yt-dlp up to date |
| Backup | weekly | DB + config zip in `/config/backups`, keep N |

The scheduler must survive restarts (state in DB), run jobs with concurrency limits per source (Soulseek: 1 search at a time, ≤ N downloads in flight; YouTube: 1 download at a time with sleep between; torrents: unlimited), and expose "Run now" for every job.

### 5.6 API (v1) sketch

Conventions mirror the *arrs: `X-Api-Key` header (also `?apikey=`), JSON, `/api/v1`, pagination via `page`/`pageSize`/`sortKey`/`sortDirection`, and a `/api/v1/system/status` endpoint that dashboards (Homarr/Homepage) and Notifiarr-style tools can probe.

```
GET    /api/v1/system/status | /health | /log
GET    /api/v1/song?artistId=&monitored=&page=…        POST /api/v1/song      PUT/DELETE /api/v1/song/{id}
POST   /api/v1/song/lookup?term=… | ?mbid= | ?isrc= | ?spotifyId= | ?deezerId= | ?url=   (resolve without adding)
GET    /api/v1/song/{id}/albumcontexts                   PUT /api/v1/song/{id}/albumcontext
GET    /api/v1/referencelibrary | POST … | POST /api/v1/referencelibrary/{id}/scan
GET    /api/v1/matchqueue  | POST /api/v1/matchqueue/{id}/resolve {identity}
GET/PUT /api/v1/soulseek/settings   (writes through to slskd)   GET /api/v1/soulseek/status
GET    /api/v1/artist  …  GET /api/v1/artist/{id}/toptracks
GET    /api/v1/wanted/missing | /wanted/cutoff
GET    /api/v1/queue | DELETE /api/v1/queue/{id}?blocklist=true&removeFromClient=true
GET    /api/v1/history?songId=&eventType=
GET    /api/v1/blocklist | DELETE /api/v1/blocklist/{id}
GET    /api/v1/release?songId=  (interactive search; runs all sources)   POST /api/v1/release (grab a candidate)
POST   /api/v1/release/push  (autobrr-style push of a candidate)
POST   /api/v1/command  {name: SongSearch|MissingSearch|CutoffUnmetSearch|ImportListSync|RescanLibrary|RefreshSong…}
GET/PUT /api/v1/qualityprofile | /qualitydefinition | /sourceprofile
GET/POST/PUT/DELETE /api/v1/source | /downloadclient | /importlist | /notification | /library
POST   /api/v1/library/{id}/preview  {songId}  -> rendered path
POST   /api/v1/library/{id}/scan     (adopt existing files)
GET    /api/v1/tag  …
WS/SSE /api/v1/events  (queue progress, imports, health)
```

**Ecosystem compatibility (deliberate).** `/api/v1/system/status`, `/api/v1/queue` + `/queue/status`, `/api/v1/wanted/missing`, `/api/v1/history`, `/api/v1/calendar` (release dates of monitored songs) and `/api/v1/release/push` keep the Lidarr shapes closely enough that Homepage/Homarr widgets, Unpackerr (which polls a Lidarr-shaped queue to extract archives) and autobrr (`release/push` with `rejected`/`tempRejected` semantics) work with a "Lidarr-compatible" toggle. Prowlarr cannot register us as an application (its app list is hard-coded), so indexers are added by pasting Prowlarr's per-indexer Torznab/Newznab URL and API key, or by pointing at Prowlarr's `/api/v1/search` once.
## 6. Matching, decision, and verification engine

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
| Artist mismatch | no artist token overlap after normalisation when the artist is known (guards against same-titled songs by other artists); soft-fail for compilation paths without the artist |
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

"Good enough" early stop: when a candidate scores ≥ 850 with a free slot and ≥ 1 MB/s upload speed while Soulseek results are still streaming, grab it immediately (Sockseek's fast-search behaviour), then let the search finish for the record.

### 6.4 Search strategy per source

- **Soulseek (slskd).** Queries in order: `artist title` → the same with diacritics stripped and special characters removed → `title artist` → title-only (artist must then match in the path) → `artist album` (browse the folder for the track) → title with `feat.` clauses stripped. Stop early when the candidate pool is good. Each search: `searchTimeout` 8000 ms (from last response), `responseLimit` 100, `fileLimit` 2000, `minimumPeerUploadSpeed` 1, streamed via `/hub/search`, always `DELETE`d afterwards, and cancelled by our own 30 s wall clock. **Budget**: a global token bucket of 30 searches per 4 minutes, at most 2 outstanding, ≥ 5 s between submissions (Sockseek's 34/220 s and soularr's 5 s floor). Server-banned query terms are wildcarded (`*inkin`).
- **YouTube.** ytmusicapi `search(isrc, ignore_spelling=True)` when an ISRC is known → `search("artist title", filter="songs")` → `filter="videos"` only when the source rule allows → plain yt-dlp `ytsearch5:` as last resort. Candidates from the `songs` filter are marked verified and get the ATV bonus; a verified candidate scoring ≥ 850 ends the search immediately. Grab = yt-dlp with `bestaudio[acodec=opus]/bestaudio`, `-x`, cookies/PO-token provider if configured, `-t sleep` pacing, `--print after_move:filepath`.
- **Torznab/Newznab.** We cannot search for a recording, so we search for releases that contain it: from MusicBrainz, list the releases the recording appears on (the assigned album context first, then other official albums, then singles, then compilations); per indexer read `caps` and use `t=music&artist=&album=` when supported, else `t=search&q=artist album`, categories 3000/3010/3040; for Gazelle trackers with a user API key, call `ajax.php?action=browse&filelist=<title>&artistname=<artist>` directly and read `fileList` (name and size per file) before choosing. Parse quality from the release name with Lidarr's regexes (`FLAC`, `24BIT`/`TR24`, `MP3-320`, `V0`, `WEB`). For `.torrent` results download the metainfo via the Prowlarr proxy link and bencode-parse the file list locally; for magnets defer file selection until metadata is fetched. Locate the wanted track inside the container by track number + title fuzzy match + size window; carry `container_files` so the grab can select just that file and also every *other* wanted song in the same container ("bundling").

### 6.5 Verification after download

1. **Probe** with ffprobe: must decode; capture codec, bitrate, sample rate, bit depth, channels, duration. Measured quality replaces inferred quality, with one exception: a file the app itself transcoded from a lossy source (YouTube Opus → AAC/MP3) keeps its *source* quality (`OPUS-160`), never the target bitrate, so a 320 cutoff still treats it as upgradeable (a "FLAC" that is 128 kbps in a FLAC container is caught here; a spectral cutoff check for transcoded lossless, like SoulSync's fake-lossless detector, is a Phase 8 enhancement).
2. **Duration** within tolerance of the song's known length (YouTube rips of official videos with intros fail here).
3. **Fingerprint**: `fpcalc -json` (first 120 s), then AcoustID `lookup?client=&fingerprint=&duration=&meta=recordingids` (3 req/s, honour 429). Pass if the wanted recording MBID appears with score ≥ 0.7 (configurable); 0.5–0.7 is "needs review" (imported with a low-confidence badge unless the profile says strict); when the song has no MBID yet, accept a returned recording whose normalised title and artist match and **learn** the MBID. If the first window fails (DJ talk-over, long intro), retry with `-chunk`/`-length` on the middle of the file. If AcoustID has no fingerprint for the recording at all (common for new or obscure music), fall back to probe + duration + score ≥ threshold and mark `fingerprint_verified = false`. Pitch/speed-shifted YouTube uploads are a known failure mode (a 2.5 % speed change defeats matching); they fail verification and get blocklisted, which is the desired outcome.
4. **Outcome**: pass → import; fail → history "rejected" with reason, candidate blocklisted, the next candidate grabbed automatically (bounded by `max_auto_attempts_per_search`, default 4), then the next source tier.

### 6.6 Upgrades and re-search cadence

A song below cutoff stays in Cutoff Unmet. The upgrade search runs on a slower schedule, in bounded batches, and only grabs when the candidate's quality rank is strictly higher and its identity score is not worse than the current file's. On import the old file goes to the recycle bin and history records "upgraded". Missing-song re-search backs off per song (1 h → 6 h → 24 h → 72 h → weekly) and never re-runs the same Soulseek query more often than the network's own 12-minute wishlist cadence.
## 7. Library output: layouts, naming, tags, artwork, Plex

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
- Embeds front cover art (JPEG, bounded to a configurable max edge, default 1400 px) **and** writes `cover.jpg` in the album folder (for the pseudo-album: a generated cover from the artist image, or the first track's art, configurable); optionally `artist.jpg` in the artist folder from fanart.tv/TheAudioDB.
- Writes an `.lrc` (synced) or `.txt` sidecar with the track's base name when LRCLIB has lyrics, plus the unsynced text in the tag.
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
## 8. Tech stack decision

### 8.1 What the *arrs are, and why not fork Lidarr

Sonarr, Radarr, Lidarr and Prowlarr are C#/ASP.NET Core (Radarr/Lidarr/Prowlarr on `net8.0`, Sonarr v4 still on `net6.0`), Dapper + FluentMigrator on SQLite/Postgres, DryIoc, NLog, and a React 18 + Redux + webpack front end; all GPL-3.0. There is no shared "Servarr" package: the forks stay in sync by a cherry-picking bot, and extracting the core was closed as not planned ([Sonarr#7528](https://github.com/Sonarr/Sonarr/issues/7528)). Lidarr is "Sonarr but made for music" and measures about 95k lines of C# in Core (264 files touching Album/Artist/Track), 82 migrations and 103k lines of front end; changing its unit from album to recording would rewrite the domain, parser, decision engine, metadata source, import pipeline, API and most of the UI. **Decision: do not fork Lidarr; build new and port its reusable subsystems.**

### 8.2 The Soulseek library question

The only production-grade embeddable Soulseek client is Soulseek.NET (C#, the engine inside slskd). Since April 2026 it is GPL-3.0-only with policed "Additional Terms" requiring each embedding app to transmit its own registered client version, and every NuGet version is currently unlisted and flagged deprecated with no public explanation ([release 10.0.0](https://github.com/jpdillingham/Soulseek.NET/releases/tag/10.0.0), [nuget.org](https://www.nuget.org/packages/Soulseek/10.0.2)). The network's rules also expect a full client that shares. **Decision: drive slskd over its HTTP API** (packaged with the app, §9); the app never embeds a Soulseek client, so this question no longer constrains the language.

### 8.3 Decision: C#/.NET 10 + React 19 (aligned with the *arrs)

The owner's preference is alignment with the other *arrs; with slskd external, C# carries no Soulseek-library risk, and it turns the *arrs' GPL-3.0 code base into a parts bin:

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS to Nov 2028) | slskd already targets it; Lidarr is on .NET 8 so ported code needs no downgrade |
| Web/API | ASP.NET Core controllers + OpenAPI, SignalR for live queue/search events | SignalR is what the *arrs and slskd use for push updates |
| Data | EF Core 10 + SQLite (WAL), migrations in-repo | Simpler than the *arrs' Dapper + FluentMigrator + Marr mini-ORM; ported classes bring their logic, not their persistence |
| Jobs | Hosted services + a SQLite-backed command queue exposing `/api/v1/command` semantics; Quartz.NET for cron-style schedules | Mirrors the *arrs' command/task model that dashboards understand |
| DI / logging | Microsoft.Extensions.DependencyInjection; Serilog (JSON + rolling files) with key redaction | |
| Tagging | ATL (`z440.atl.core`, MIT, managed, writes MP3/FLAC/M4A/Opus with pictures and lyrics); TagLib# as fallback | |
| Fingerprinting | `fpcalc` subprocess + AcoustID HTTP (Lidarr's `FingerprintingService` is directly portable); AcoustID.NET optional | |
| yt-dlp | YoutubeDLSharp (BSD-3) subprocess wrapper with `-J`/`--print` parsing; Deno bundled in the image | |
| YouTube Music search | Small InnerTube client in C# (port of ytmusicapi's `search` parser for the `songs`/`videos` filters); the `YTMusicAPI` NuGet package is evaluated first | ytmusicapi is a thin wrapper over `music.youtube.com/youtubei/v1/search` |
| MusicBrainz / CAA / Deezer / iTunes / LRCLIB / ListenBrainz / Last.fm | Thin typed `HttpClient`s with a shared rate limiter and the metadata cache | |
| Plex | Port Lidarr's `PlexServerProxy` (sections, partial refresh) + `emptyTrash`; playlists via `/playlists/upload` | |
| Torrents / usenet | Port Lidarr's qBittorrent and SABnzbd proxies and its Torznab/Newznab request generators, caps and RSS parsers | |
| Frontend | React 19 + TypeScript + Vite + TanStack Query/Table + Mantine, Lucide icons; *arr-style shell (Library / Wanted / Activity / Settings / System) | Lidarr's own React 18 + Redux + webpack UI is not worth porting; conventions are |
| Packaging | Multi-stage build; self-contained trimmed publish on `mcr.microsoft.com/dotnet/aspnet:10.0` (or Alpine) + static ffmpeg/ffprobe + chromaprint `fpcalc` + Deno; multi-arch (amd64, arm64) | |

**Runner-up: Python (FastAPI + React)** as in revision 1: every music library is native there (yt-dlp, mutagen, pyacoustid, ytmusicapi, plexapi), SoulSync and spotDL code could be reused directly rather than ported, and Bazarr proves the shape. It loses on the owner's alignment preference and on the size of the portable *arr code base.

### 8.4 What gets ported, from where

| Source (licence) | Take | How |
|---|---|---|
| Lidarr, Prowlarr, Sonarr (GPL-3.0) | qBittorrent + SABnzbd clients; Newznab/Torznab request generators, caps and RSS parsers, category tables; `QualityParser` and release-name regexes; `FileNameBuilder` token engine and illegal-character rules; notification providers (Webhook, Discord, Apprise, Email, Telegram, Plex…); health-check framework; API-key/Forms auth handlers, URL base, `/ping`; disk transfer (hardlink/move/copy), permissions, recycle bin; remote path mappings; decision-specification pattern with permanent/temporary rejections; backup service; `FingerprintingService` | Copy with attribution, adapt to EF Core and our domain; app licensed GPL-3.0 so this is clean |
| SoulSync (MIT, Python) | Fake-lossless detector approach, version-aware title matching heuristics, quality-ladder presets | Port logic to C#; keep attribution |
| spotDL (MIT, Python) | YouTube Music matching rules (ISRC-first, verified-result early return, forbidden-word penalties, duration decay) | Port rules (§6) |
| Sockseek (AGPL-3.0, C#) | Ranking order and search-query strategy | **Re-implement from the documented behaviour; do not copy code**, to keep the app plainly GPL-3.0 |
| Exportify (MIT) | CSV column contract | Read only |

### 8.5 Licence

GPL-3.0, public repository. Required by the ported Lidarr/Prowlarr code and consistent with the *arr convention; slskd (AGPL-3.0 + additional terms) runs as a separate program and is redistributed unmodified in the bundled image with its notices and source offer.
## 9. Deployment

### 9.1 Containers

| Container | Role | Required? |
|---|---|---|
| **the app** | API + UI + scheduler + import pipeline (single process) | yes |
| **slskd** (bundled) | Soulseek client: searches, downloads, and shares the library back. **Ships inside the app image and runs as a child process supervised by the app** (§9.5), headless, configured entirely from the app's Soulseek settings page. An *external slskd* mode (URL + API key) is available for users who already run one | yes for the Soulseek source (bundled by default) |
| qBittorrent | torrent client (v1 scope) | optional |
| SABnzbd | usenet client | optional |
| Prowlarr | indexer aggregation; the app consumes its per-indexer Torznab/Newznab endpoints or `/api/v1/search` with an API key | optional |
| bgutil-ytdlp-pot-provider | PO-token provider for YouTube when bot checks bite | optional |
| Plex Media Server | library target; partial scans, empty-trash and playlist push via `X-Plex-Token` | optional |

Bundling is the default from Phase 0 (owner decision): one container, one settings page, no second service to wire up. slskd's pinned release build (linux-x64 / linux-arm64 self-contained archives from its GitHub releases, verified by checksum at image build) lives under `/opt/slskd`; each app release pins and tests one slskd version, and the release notes state it. slskd is redistributed unmodified with its AGPL-3.0 licence, additional terms and source link. Users who prefer the two-container layout run the same image with `APP__SOULSEEK__MODE=external` and their own slskd.

### 9.2 Image conventions (aligned with hotio/LinuxServer *arr images)

- One shared `/data` layout (`/data/downloads/{slskd,torrents,usenet}` and `/data/media/music`) so imports are hardlinks/atomic moves, plus `/config` for the SQLite DB, config, logs and backups ([Servarr docker guide](https://github.com/Servarr/Wiki/blob/master/docker-guide.md)). Legacy `/downloads` + `/music` mappings also work (copy instead of hardlink, with a health warning).
- `PUID`, `PGID`, `UMASK`, `TZ`; non-root by default; s6-overlay init like hotio/LinuxServer images.
- HTTP on **port 1077** (owner decision; no *arr-family app uses it); Soulseek listen port **50300/tcp** exposed for the bundled slskd (forward it on the router for best results); `URL base` setting; `/ping` (unauthenticated readiness) and `/api/v1/health`; container `HEALTHCHECK` on `/ping`.
- Remote path mappings per download client and a Plex path mapping.
- Multi-arch images (amd64, arm64) on GHCR on every tag: `:latest`, `:develop`, semver; Unraid Community Applications XML template in the repo.

### 9.3 Compose example (Appendix A)

The reference `docker-compose.yml` is a single service: the app image with slskd bundled, `/config` (including `/config/slskd`) and `/data` volumes, ports 1077 and 50300. An alternative snippet shows external-slskd mode.

### 9.4 Operational rules baked into defaults

- **Soulseek**: global budget 30 searches per 4 minutes, at most 2 outstanding, ≥ 5 s between submissions; 8 s search timeout (from last response) with a 30 s wall-clock cancel; ≤ 3 concurrent downloads; every search deleted after use; sharing on by default; dedicated account; slskd ≥ 0.26.0 for per-batch destination folders.
- **YouTube**: user must enable the source; 1 download at a time; `-t sleep`-equivalent pacing; Deno in the image; optional cookies and bgutil sidecar; daily yt-dlp version check with in-place update by setting; health probe against a known Art Track.
- **Torrents (qBittorrent)**: partial single-file downloads on; never remove before the client's own seed goals are met; magnets resolved to metadata (`stopCondition=MetadataReceived`) before file selection.
- **Usenet (SABnzbd)**: whole post by default; opportunistic per-file trimming only when file subjects are clean.
- **Metadata APIs**: MusicBrainz 1 req/s with a descriptive `User-Agent`, AcoustID 3 req/s, LRCLIB 200–500 ms gaps, iTunes 20/min, honour `Retry-After`; every response cached with TTLs.

### 9.5 Managing the bundled slskd from the app

slskd exposes its configuration for remote management when `SLSKD_REMOTE_CONFIGURATION=true`: `GET/PUT /api/v0/options/yaml` and `POST /options/yaml/validate` (these need a JWT session from `POST /api/v0/session` with slskd's username/password, not an API key), and it **watches its YAML file and reloads changes**; speed limits and the listen port apply live, while shared directories, slot limits, credentials and distributed-network settings need a restart or reconnect ([slskd config docs](https://raw.githubusercontent.com/slskd/slskd/master/docs/config.md)). In bundled mode the app does not need remote configuration or a JWT session at all: a `SlskdHost` hosted service renders `/config/slskd/slskd.yml` from the app's settings, launches the slskd binary with `SLSKD_APP_DIR=/config/slskd`, `SLSKD_HEADLESS=true` and a generated API key, captures its logs into the app's log viewer, watches its health (`/api/v0/application`, login state), and restarts it when a change needs it (shares, slots, credentials); live-reloadable settings (speed limits, listen port) just take effect. The app's **Soulseek settings page** therefore owns everything: Soulseek username/password, listen port, shared folders (default: the managed library root, read-only, plus any reference library the user ticks), a **"Share my library" toggle** (default on; turning it off shows the leech-ban warning and a persistent health notice), upload slots and speed limits, download/incomplete directories, distributed-network participation, and "open slskd's own UI" for the curious (headless off). In external mode the same page writes through slskd's options API (`GET/PUT /api/v0/options/yaml`, which needs `SLSKD_REMOTE_CONFIGURATION=true` and a JWT session from the slskd web login) and shows a "restart slskd" notice where needed.
## 10. Phased build plan

Effort is for one developer working with AI assistance, in focused weeks; treat it as relative sizing. Each phase ends in something runnable in Docker and a "done when" gate. **Phases 0–4 are the MVP.** Ported code (§8.4) is what keeps Phases 0, 5 and 7 short.

### Phase 0 — Repository and skeleton (≈1 week)

- New repo (GPL-3.0), README, `CONTRIBUTING`, issue templates, ADR folder (this plan's decisions become ADR-0001…), attribution file for ported code.
- Backend: .NET 10 solution (`Core`, `Api`, `Host`, `Sources.*`, tests); EF Core + SQLite (WAL) with migrations; config from `/config/config.yml` + env; API key auth (`X-Api-Key`, `apikey`, Bearer), Forms login with "disabled for local addresses", URL base, `/ping`, `/api/v1/system/status`, `/api/v1/health`; Serilog with redaction; SignalR hub for events; command queue + `/api/v1/command`; Quartz for schedules with "run now".
- Frontend: React 19 + TypeScript + Vite + TanStack Query/Table + Mantine; *arr-style shell (Library / Wanted / Activity / Settings / System); settings forms rendered from schema; dark/light theme.
- Docker: multi-stage build, trimmed self-contained publish, static ffmpeg/ffprobe, chromaprint `fpcalc`, Deno, **bundled slskd** (pinned release archive, checksum-verified) under `/opt/slskd`; s6-overlay init with `PUID/PGID/UMASK/TZ`; `SlskdHost` service that launches, monitors and restarts slskd and captures its logs; single-service `docker-compose.yml` on ports 1077 and 50300; `HEALTHCHECK` on `/ping`; CI (build, tests, lint, image build, multi-arch on tag).
- **Done when:** the container starts on port 1077, the UI loads behind a URL base, the API key works, health shows DB, folders and the bundled slskd process OK (logged out until credentials are entered), and a scheduled no-op job survives a restart.

### Phase 1 — Song identity and the Wanted list (≈2 weeks)

- MetadataProvider: MusicBrainz recording search/lookup/ISRC + Cover Art Archive (`release-group/{id}/front-500` → release → Deezer → iTunes), cache with TTLs, 1 req/s limiter, descriptive `User-Agent`; Deezer fallback identity, preview and art; iTunes lookups.
- Album-policy engine (§7.3) with `fewest_albums`, `singles_only`, `original_album`, `single_release`, `compilation`; sticky assignments.
- Version-flag parser (title + MB disambiguation + release-group secondary types).
- Song/Artist CRUD; add-by-search UI with disambiguation, length, release types, cover and Deezer preview; bulk add from pasted `Artist - Title` lines; `POST /api/v1/song/lookup` for URLs/ids.
- Wanted (Missing / Cutoff Unmet), History, Blocklist views.
- Quality definitions seeded (Appendix B); profiles CRUD with defaults **"Standard 320"** (cutoff MP3-320; AAC-256, FLAC allowed; upgrades on) and "Lossless" (cutoff FLAC).
- **Done when:** a pasted list of 50 songs resolves ≥ 90 % to MB recordings with correct durations and cover art; the rest resolve via Deezer or land in an "unresolved" review state; every song has an album assignment under the library's policy.

### Phase 2 — Soulseek source, slskd management, and the import pipeline (≈3 weeks)

- slskd client behind `SourceProvider`: version check, `X-API-Key` for search/transfer calls, JWT session for options; multi-query search strategy; `/hub/search` streaming with polling fallback; early stop; always-delete; wall-clock cancel; `POST /transfers/downloads/batches` with per-grab `options.destination`; transfer polling + `DownloadFileComplete` webhook receiver; queue-position; stall/offline/remote-queue timeouts; blocklist keys `user + path`; reputation table; ignore lists; global search token bucket.
- **Soulseek settings page** (§9.5): credentials, listen port, shared folders + "Share my library" toggle, slots, limits, directories, distributed-network participation; renders `slskd.yml` and restarts the bundled process when required (external mode: options API + restart notice); health checks: process running, reachable, logged in, sharing, download dir writable, duplicate-login kick detected.
- Candidate normalisation and filename/path parsing (§6.1); decision engine v1 (§6.2–6.3) with persisted rejection reasons.
- Queue page with live progress (SignalR), cancel, blocklist-and-retry; Interactive search modal with score breakdown and rejections.
- Import pipeline: ffprobe probe, duration check, `fpcalc` + AcoustID verification with MBID learning, ATL tag writer for MP3/FLAC/M4A/Opus (Picard mapping, ID3v2.4), naming-template engine with live preview, layouts `flat`, `artist`, `artist_album`, hardlink/move/copy with remote path mappings, permissions, recycle bin, history events, automatic next-candidate on failure.
- **Done when:** 100 wanted songs → ≥ 80 % imported automatically at or above cutoff with zero wrong-recording imports in a manual audit of 30 files; a deliberately wrong file (live version) is caught and the next candidate tried without user action; the search budget is never exceeded; toggling "Share my library" changes slskd's shares.

### Phase 3 — Reference libraries, adoption and matching UI; Plexamp preset (≈2.5 weeks)

- Reference library scan (flat or layered): tag-first identification (MBIDs, ISRC), AcoustID, then title/artist/duration search; confidence tiers; **Match queue** UI for ambiguous files (ranked candidates, manual search, bulk actions); modes *reference only* and *adopt* (re-tag + place under the library's layout and album policy, recycle bin).
- `plexamp` preset (§7.2–7.4): album-folder layout with compaction, Plex-safe tag rules, `cover.jpg`, optional `artist.jpg`, Opus→AAC/MP3 transcode setting; "Compact library" task with the Plex move-out/scan/empty-trash/move-back sequence.
- Plex connection: token via the plex.tv PIN flow, section picker, path mapping, partial `refresh?path=` after import, `emptyTrash`, connection test.
- Lyrics provider (LRCLIB) → `.lrc`/`.txt` sidecars and unsynced tag lyrics. Notifications: Webhook, Discord, Apprise, Plex (ported), per-event toggles.
- **Done when:** an existing folder of 500 mixed files is identified ≥ 90 % automatically, the rest are resolved in the Match queue in one sitting, and adopted files appear in a fresh Plex Music library with correct grouping (no split albums, no one-track albums except deliberate singles); Plexamp shows local lyrics.

### Phase 4 — YouTube source (≈2 weeks)

- YouTube Music search client (InnerTube `search` with `songs`/`videos` filters; ISRC query first), spotDL-derived identity scoring, ATV/OMV/UGC handling, plain yt-dlp search fallback.
- yt-dlp via YoutubeDLSharp: `bestaudio` Opus, cookies file, optional bgutil PO-token URL, Deno detection, pacing, concurrency 1, error taxonomy (bot check, 429/402, geo, age gate, unavailable) → retry-later vs blocklist; self-update setting; health probe.
- Output policy per library with per-source and per-grab overrides (§7.4): codec (AAC / MP3 / keep Opus), CBR bitrate or VBR quality, container, sample rate; defaults AAC-256 `.m4a`; the file's *quality* is recorded as `OPUS-160` (its source) so a 320 cutoff keeps it upgradeable; "lossless from lossy" refused.
- Source profile ordering UI; YouTube runs only when Soulseek yields nothing acceptable, or on interactive search; enable toggle with a one-time disclaimer.
- **Done when:** songs missing on Soulseek are filled from Art Tracks within one search cycle and pass fingerprint verification; official-video candidates with intros are rejected by duration; a simulated bot-check response backs off instead of looping.

### Phase 5 — Upgrades and operations (≈1 week)

- Cutoff-unmet upgrade loop with bounded batches and reputation-aware scoring; per-song backoff; "search on add".
- Tasks page with intervals and run-now; backup/restore; log viewer; `/api/v1/release/push`; OpenAPI UI; Homepage/Unpackerr compatibility check.
- External-slskd mode (URL + API key + options API) for users who already run slskd, with the same settings page.
- **Done when:** a song imported at MP3-320 with a FLAC cutoff is upgraded when a FLAC appears (old file recycled, history "upgraded"); a backup restores into a fresh container; external-slskd mode passes the Phase 2 gate unchanged.

### Phase 6 — Import lists and playlist sync (≈2 weeks)

- ImportList providers: **CSV** (Exportify's Spotify column set — `Track Name`, `Artist Name(s)`, `Album Name`, `Track Duration (ms)`, `ISRC`, `Album Release Date`, `Explicit?` — plus a generic column mapper), Deezer playlist (no auth), YouTube Music playlist, Last.fm loved/top tracks, ListenBrainz loved/playlists, artist top-N (Deezer/Last.fm), and reference libraries (§7.6) as a list source; Spotify Web API import kept optional (owner's own client id).
- Sync policies (add-only / add-and-unmonitor-removed / mirror); unresolved-item review; per-list quality profile, source profile and library.
- Playlist output: Plex playlist via `/playlists/upload` or rating keys; `.m3u8` export.
- **Done when:** a 200-track Exportify CSV imports, resolves via ISRC ≥ 95 %, downloads, and appears as a Plex playlist after two search cycles.

### Phase 7 — qBittorrent and SABnzbd sources (≈2 weeks)

- Torznab/Newznab SourceProvider (ported): per-indexer `caps`, `t=music` vs `q=` fallback, categories, Prowlarr endpoints; Gazelle-direct `filelist` search where the user has a key; recording → releases (MB) → release searches; Lidarr-derived release-name quality parsing; `.torrent` metainfo file listing; container-file matching by track number/title/size window.
- qBittorrent client (ported): add stopped, `stopCondition=MetadataReceived` for magnets, `files`, `filePrio`, `start`, 4.x/5.x state names, categories, remote path mappings, completion by wanted files, import by file index. SABnzbd client (ported): whole post, opportunistic `delete_nzf` trimming, post-unpack track selection.
- Bundling: other wanted songs in the same container imported from one grab; optional album cache.
- **Done when:** a song only available inside an album torrent is imported by downloading just that file; an NZB album downloads, unpacks, and only the wanted track(s) are imported; two wanted songs from the same album are satisfied by one grab.

### Phase 8 — Polish and release (≈1.5 weeks)

- Mass editor, tags, filters, saved views; spectral fake-lossless check (SoulSync's approach); ReplayGain writer (optional).
- Unraid template, docs site, first tagged release, migrations tested from the Phase 0 schema.
- **Done when:** a first public release is cut with multi-arch images and the docs cover setup with slskd, Plex and qBittorrent end to end.

### Later / backlog

- Transmission, Deluge, rTorrent, NZBGet clients; per-indexer private-tracker policies (partial-download switch, seed-whole-album).
- Embedded Soulseek client behind the same interface (would remove the bundled slskd process), only if slskd bundling proves troublesome.
- Deezer-preview cross-correlation for recordings unknown to AcoustID.
- Delay profiles; custom formats (regex scoring on release names).
- Jellyfin/Navidrome/Emby hooks; Subsonic playlist push.
- Multi-user requests (Seerr-style) and a mobile "request a song" page.
- Bandcamp collection (purchased FLAC) and SoundCloud sources.
## 11. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| A comparable project already exists (SoulSync, DroppedNeedle) | Duplicated effort; smaller community | Decided: build new (§0); differentiate on the *arr model, song-first flow, Plexamp compaction preset, selective torrent download; port MIT code where it fits (§8.4) |
| Wrong recording imported (live/remix/cover/re-recording/sped-up upload) | Library quality; trust | Version-flag hard rules, duration tolerance, AcoustID verification, per-source blocklist, low-confidence badge, "report wrong file" button that blocklists and re-searches |
| Soulseek search bans (30-minute server bans for bursts) | Source goes dark | Global token bucket (30 per 4 min), ≤ 2 outstanding, 5 s spacing, per-song backoff, wishlist-cadence floor, one slskd per account |
| Being a leech on Soulseek | Peers throttle/ban the account | slskd shares the library read-only; health check warns when nothing is shared |
| slskd API instability (v0) | Integration breaks on upgrade | Thin client behind an interface, startup version check with a tested range, contract tests against slskd's Swagger, pinned image tag in the compose example |
| slskd duplicate-login kick with no auto-reconnect | Silent outage | Health check "Soulseek logged out" + notification; docs require a dedicated account |
| YouTube bot detection, JS-runtime and PO-token churn, format regressions | Secondary source degraded | yt-dlp self-update, Deno in image, cookies + bgutil support, error taxonomy with backoff, health probe, YouTube never the only source, honest `OPUS-160` quality so grabs stay upgradeable |
| YouTube ToS exposure | User's risk | User must enable the source, supplies own cookies/tokens, disclaimer in UI; source is a plugin that can be disabled |
| MusicBrainz gaps for new/obscure songs | Unresolvable identity | Deezer/Spotify/iTunes identity fallback, "unresolved" review state, MBID learning from AcoustID, duration/title-based verification fallback |
| MusicBrainz rate limit with big playlists | Slow imports | Persistent cache, ISRC batch lookups, background resolution queue, optional self-hosted mirror later |
| Spotify/Deezer API restrictions tighten further | Playlist import breaks | Spotify is owner-only by design; Deezer/YouTube Music/ListenBrainz/Last.fm/CSV are the primary list sources |
| Torrent per-song cost; partial seeds count as leechers on Gazelle | Ratio damage on private trackers | v1 targets qBittorrent with partial downloads on and no tracker-specific rules (owner decision); bundling and "never remove before seed goals" limit the cost; per-indexer policies are backlog |
| Plex never reconsiders a track's album membership | Mis-grouped tracks are permanent | Deliberate album assignment at import (§7.3), sticky assignments, explicit Compact task using move-out / scan / empty-trash / move-back |
| Bundled slskd: version coupling, crash loops, AGPL redistribution | Soulseek outage; licence errors | Pinned, tested slskd per release; supervised restarts with backoff and a health alert; slskd port 5030 not published; licence/NOTICE and source link shipped in the image; external mode as an escape hatch |
| slskd YAML holds secrets (Soulseek password, API key) | Credential exposure | Written by the app with 600 permissions inside `/config/slskd`; slskd headless; documented as LAN-only; external mode stores slskd web credentials encrypted |
| Porting GPL-3.0 code | Licence obligations | App is GPL-3.0; per-file attribution and a NOTICE file; AGPL code (Sockseek) re-implemented, not copied |
| Usenet obfuscation/RAR packing | Cannot pick a track pre-download | Whole-post default with post-unpack selection; trimming only when file names are clean |
| Plex splitting or mis-grouping albums | Ugly library | Identical album tags and date strings per folder, Various Artists rule, tested `plexamp` preset, partial scans, pseudo-"Singles" policy |
| Tag-writing bugs corrupt files | Data loss | Write to temp copy, re-read to verify, atomic replace; recycle bin for replaced files |
| Scope creep toward Lidarr (albums, discographies) or SoulSync (six streaming sources) | Never ships | Non-goals in §2; phase gates; plugin interface for anything beyond the three source families |
## 12. Open questions and next steps

All design questions from rounds 1–3 are answered (§0). Nothing blocks starting the repository. Two housekeeping items to confirm at repo creation:

1. **Name check.** Run a GitHub name search for "Compilarr" (organisation and repository) and check Docker Hub/GHCR before pushing the first commit; web search found no collision, but that is not a registry search.
2. **Organisation.** Create the repository under `wulfftech` (assumed) with GPL-3.0, and reserve the GHCR image name `ghcr.io/wulfftech/compilarr`.

Suggested first milestone: Phase 0 (§10) as the repository's first pull request, with this plan copied in as `docs/PLAN.md` and its decisions split into ADRs.
## Appendix A. Reference `docker-compose.yml` (illustrative)

```yaml
services:
  compilarr:
    image: ghcr.io/wulfftech/compilarr:latest   # slskd is bundled inside this image (§9.1)
    container_name: compilarr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Australia/Brisbane
      # everything else (Soulseek account, shares, sources, Plex, profiles) is set in the UI
    volumes:
      - ./config/compilarr:/config          # app DB/config/logs; bundled slskd state under /config/slskd
      - /data:/data                         # /data/downloads/{slskd,torrents,usenet}, /data/media/music
    ports:
      - "1077:1077"        # web UI / API
      - "50300:50300"      # Soulseek listen port for the bundled slskd (forward it for best results)
    healthcheck:
      test: ["CMD", "wget", "-q", "--spider", "http://localhost:1077/ping"]
      interval: 30s
    restart: unless-stopped

  # Optional companions (the app talks to them over their APIs):
  # qbittorrent: ...   sabnzbd: ...   prowlarr: ...   bgutil-provider: brainicism/bgutil-ytdlp-pot-provider

  # External-slskd mode instead of the bundled one: set APP__SOULSEEK__MODE=external,
  # APP__SLSKD__URL, APP__SLSKD__API_KEY (+ web username/password for the options API) and run
  # slskd/slskd:latest with SLSKD_REMOTE_CONFIGURATION=true.
```

`/config/<app>/config.yml` (subset):

```yaml
server: { port: 1077, url_base: "", auth: forms, api_key: "<generated>" }
metadata:
  musicbrainz: { user_agent: "<app>/0.1 (you@example.com)", rate_limit_rps: 1 }
  acoustid: { client_key: "<register at acoustid.org>", min_score: 0.7, review_score: 0.5 }
  lyrics: { provider: lrclib, write_lrc: true, embed_unsynced: true }
soulseek:                                  # rendered into /config/slskd/slskd.yml (§9.5)
  mode: bundled                            # bundled | external
  username: <dedicated-soulseek-user>
  password: <password>
  listen_port: 50300
  share_library: true                      # "Share my library" toggle
  shared_folders: [/data/media/music]
  upload_slots: 10
  upload_speed_limit_kib: 2000
sources:
  - type: slskd
    name: Soulseek
    # url/api_key only needed in external mode
    max_concurrent_downloads: 3
    search_timeout_ms: 8000
    preferred_formats: [flac, mp3, m4a]
    ignore_users: []
  - type: youtube
    name: YouTube Music
    prefer_topic_channel: true
    allow_videos: false
    # output policy (§7.4): fully configurable; also overridable per library and per grab
    output: { codec: aac, mode: cbr, bitrate_kbps: 256, container: m4a, sample_rate: keep }
    # e.g. { codec: mp3, mode: vbr, quality: 0 } or { codec: opus, keep: true }; ranked as OPUS-160 regardless
    cookies_file: null
    po_token_provider: null
  - type: torznab
    name: Prowlarr
    url: http://prowlarr:9696
    api_key: "<prowlarr key>"
    download_client: qbittorrent
    partial_download: true
libraries:
  - name: Plexamp
    root: /data/media/music
    layout: plexamp
    album_policy: fewest_albums              # fewest_albums | singles_only | original_album | single_release
    min_tracks_per_real_album: 2
    naming: "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}"
    sidecars: { cover_jpg: true, lrc: true, artist_jpg: false }
    plex: { url: http://plex:32400, token: "<token>", section: Music, path_map: { "/data/media/music": "/music" } }
reference_libraries:
  - name: My existing music
    root: /data/media/music-old
    mode: reference                          # reference | adopt
quality_profiles:
  default: Standard 320                     # cutoff MP3-320; AAC-256 and FLAC allowed; upgrades on
```
## Appendix B. Quality definitions seed (adapted from Lidarr's `Quality.cs` weights)

Ascending rank; the profile UI groups them as Lidarr does. Lossless ranks above every lossy tier; the profile's cutoff is one of these.

| Rank | Group | Qualities |
|---|---|---|
| 1 | Unknown | Unknown |
| 2 | Trash lossy | MP3-8 … MP3-80 (import-only, never grabbed) |
| 3 | Poor lossy | MP3-96, MP3-112, MP3-128, MP3-160, Vorbis Q5, **OPUS-96** |
| 4 | Low lossy | MP3-192, MP3-224, AAC-192, Vorbis Q6, WMA, **OPUS-128** |
| 5 | Mid lossy | MP3-256, MP3-VBR-V2, AAC-256, Vorbis Q7, Vorbis Q8, **OPUS-160** (YouTube's best) |
| 6 | High lossy | MP3-320, MP3-VBR-V0, AAC-320, AAC-VBR, Vorbis Q9, Vorbis Q10, **OPUS-192+** |
| 7 | Lossless | FLAC, ALAC, APE, WavPack |
| 8 | Hi-res lossless | FLAC 24-bit, ALAC 24-bit |
| 9 | Uncompressed | WAV, AIFF |

Default profile **"Standard 320"**: allowed = MP3-320, MP3-VBR-V0, AAC-256, AAC-320, FLAC, FLAC 24-bit, plus lower lossy tiers down to OPUS-128/MP3-192 as placeholders; cutoff = MP3-320 (AAC-256 counts as met); upgrades on. Second profile "Lossless": cutoff FLAC.

Additions over Lidarr: explicit Opus tiers (Lidarr folds Opus into Vorbis Q-levels), so YouTube results are ranked honestly. Detection order: measured (ffprobe) after download always wins; before download use Soulseek attributes (bitrate, sample rate, bit depth, VBR flag), YouTube format ids (251 → OPUS-160, 140 → AAC-128), and Lidarr-style release-name parsing for torrents/usenet (`FLAC`, `24BIT`, `MP3-320`, `V0`, `WEB`…). Size sanity windows per tier come from the MusicBrainz duration × bitrate with Lidarr-like min/max budgets.
## Appendix C. Research reports and method

The six reports in `research/` are the evidence base for §3 and were produced on 2026-09-28 by parallel investigations; each claim carries a URL and each report ends with an explicit list of what could not be verified.

| File | Covers |
|---|---|
| `research_arr.md` | *arr architecture from source (Sonarr/Radarr/Lidarr/Prowlarr), Lidarr's data model and why singles fail, issue history, landscape and name collisions, Prowlarr/Torznab/Unpackerr/autobrr/Homepage/Seerr integration points, Docker conventions |
| `research_soulseek.md` | slskd API/config/Docker/events/relay/limits, Sockseek and soularr matching internals, other Soulseek tools, network rules and protocol facts, client libraries by language |
| `research_youtube.md` | yt-dlp 2026 (EJS runtime, formats, PO tokens, cookies, rate limits, embedding), ytmusicapi and Art Tracks, spotDL's matching rules, Lidarr-ecosystem YouTube tools, audio-quality realities, legal notes, other secondary sources |
| `research_torrent_usenet.md` | Torznab/Newznab music search reality, Prowlarr search API, Gazelle `filelist`, selective file download per torrent client, magnet handling, piece alignment, partial-seed/ratio mechanics, NZB trimming and obfuscation, SABnzbd/NZBGet APIs, Lidarr quality parsing and definitions |
| `research_metadata_plex.md` | MusicBrainz entities and WS/2, Cover Art Archive, AcoustID/Chromaprint, Spotify/Deezer/iTunes/Last.fm/ListenBrainz/Discogs/TheAudioDB/fanart.tv/LRCLIB status, Picard tag mapping, tag libraries, Plex Music/Plexamp requirements, ReplayGain, naming conventions |
| `research_stack.md` | What the *arrs are built with, the cost of forking Lidarr, Python/Go/TypeScript/C# candidate stacks with versions, Soulseek.NET licence and NuGet status, frontend and deployment conventions, licensing |

**Sandbox limitation.** The research environment's egress proxy blocked many primary documentation hosts (musicbrainz.org, acoustid.org, support.plex.tv, developer.spotify.com, slsknet.org, wiki.servarr.com, sabnzbd.org, nzbget.com, readthedocs.io, and others). Where possible the same material was read from its GitHub source (Servarr wiki markdown, Picard docs source, project source code); where not, claims come from search-engine extracts and are tagged as such in the reports. Before any such claim is quoted in a specification or a README, re-open the linked page.
