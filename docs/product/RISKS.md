# Risks and mitigations

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the risk register; update as risks retire or appear. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

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
