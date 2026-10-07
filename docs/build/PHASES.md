# Phased build plan

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — phases, scope and "done when" gates. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

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
- Playlist output: Plex playlist by rating keys, updated in place (DECISIONS build session 7 #10); `.m3u8` export.
- Added by the owner on 2026-10-07 (DECISIONS build session 7 #1–#6): **album add** (search an album, add all or some of its tracks as songs with the album pinned; "add the rest of this album" from a song); **several libraries**, each with its own Plex section, songs assigned to and moved between them; **conversion for every source** (per-library rules for YouTube, lossy and lossless files; ADR-0008 amended) and **conversion on demand** for files already in the library.
- **Done when:** a 200-track Exportify CSV imports, resolves via ISRC ≥ 95 %, downloads, and appears as a Plex playlist after two search cycles; an album added by search arrives as its tracks under that album; a song moved to a second library lands in that library's folder and Plex section; a library set to convert lossless to MP3 imports a FLAC as an MP3 still ranked `FLAC`, and an on-demand conversion replaces an existing file with the original in the recycle bin.

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
