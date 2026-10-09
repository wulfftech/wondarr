# Changelog

All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released).

## [Unreleased]

## [0.1.0] — 2026-10-09

The first public release: every phase of the plan (0–8) is in. Install: `docker pull ghcr.io/wulfftech/wondarr:0.1.0` (linux/amd64, linux/arm64); documentation at https://wulfftech.github.io/wondarr/.

### Added
- **Songs, not albums.** One MusicBrainz recording per song; add by search (with length, release types, cover and a Deezer preview), by album (all or some of its tracks, pinned to the release), or by pasting up to 1000 `Artist - Title` lines; unresolved lines wait in a review screen.
- **Soulseek first, through a bundled slskd** (or your own slskd): searches within Soulseek's limits (≤ 30 per 4 minutes, ≤ 2 at once), candidates scored on identity, quality and availability, a wrong file caught after download and the next candidate tried; Settings → Soulseek writes slskd's own configuration, including "Share my library".
- **YouTube Music second** (off by default): Art Tracks first, yt-dlp with Deno, bot-check backoff, transcoded to AAC 256 by default and ranked as its OPUS-160 source so a better copy still replaces it.
- **Torrents and usenet last**: Torznab, Newznab, Prowlarr and Gazelle indexers; qBittorrent (4.5+ and 5.x) downloads only the wanted files of an album torrent and keeps seeding; SABnzbd posts trimmed where possible; other wanted songs in the same release ride along on one grab; `release/push` for autobrr.
- **Verified imports**: ffprobe, duration and AcoustID fingerprint checks; Picard-compatible tags; a **spectral fake-lossless check** that refuses a FLAC made from a lossy file (its spectrum stops below 19.5 kHz).
- **Libraries**: flat, artist, artist/album and a **Plexamp** preset whose album policy files each song under the fewest albums per artist (sticky, with a Compact task); several libraries, each with its own Plex section; covers, `cover.jpg`, LRCLIB lyrics; conversion rules per source (keep, AAC, MP3, Opus, FLAC/ALAC from lossless only) and on demand; optional **ReplayGain** track tags.
- **Quality profiles and upgrades**: Lidarr-style profiles (defaults "Standard 320" and "Lossless"), cutoff, fingerprint-confirmed automatic upgrades, the old file to the recycle bin.
- **Inputs**: synced import lists — CSV/Exportify (any language), Deezer playlists and artist top tracks, YouTube Music playlists, Last.fm, ListenBrainz, and your existing music folder as a reference library (identified by tags, ISRC, AcoustID or text, with a Match queue for the rest; your files are never changed); playlists kept in Plex and as `.m3u8`.
- **Plex**: sign in with plex.tv, partial scans after imports, playlists updated in place.
- **The Library page**: filters, saved views, tags, and a mass editor (monitor, quality profile, library, tags, delete) over many songs at once.
- **Operations**: Activity (queue with live progress, history, blocklist), Wanted (missing, cutoff unmet), System → Tasks / Backup (scheduled and on demand, staged restore) / Logs, a daily Housekeeping task, notifications (Webhook, Discord, Apprise), Settings → General (API key, login account).
- **\*arr conventions**: `/api/v1` with `X-Api-Key`, Lidarr's queue/history/system shapes for Homepage, Unpackerr and autobrr, the API reference at `/docs`; Docker with `/config`, `/data`, `PUID/PGID/UMASK/TZ`; an Unraid template.

### Upgrading from a development build
- A database from any earlier build, including the 0.0.1-alpha.1 image under its old name (`compilarr.db`), is migrated on the first start; take a backup first (System → Backup → Back up now).

### Changed
- **Renamed from Compilarr to Wondarr** (2026-09-28): the image is `ghcr.io/wulfftech/wondarr`; an existing `/config/compilarr.db` is renamed to `wondarr.db` on first start; `COMPILARR_CONFIG_DIR` is still honoured.

## [0.0.1-alpha.1] — 2026-09-28

Phase 0 — skeleton, foundations and a runnable single-container image (`ghcr.io/wulfftech/compilarr:0.0.1-alpha.1`, amd64 + arm64).

### Added
- Solution skeleton: `Compilarr.sln`, `src/Compilarr.Core`, `src/Compilarr.Api`, test projects, `global.json` pinning the .NET 10 SDK, `Directory.Build.props` and `Directory.Packages.props`.
- Configuration from `/config/config.yml` with `APP__` environment-variable overrides, typed and validated options, and a generated API key on first run.
- SQLite persistence with EF Core, checked-in migrations and a startup migration step.
- Authentication ported from Lidarr: API key (`X-Api-Key`, `apikey`, Bearer), Forms login with "disabled for local addresses", and URL base support for reverse proxies.
- `/ping`, `/api/v1/system/status` (Lidarr field set) and `/api/v1/health` with database, folder and bundled-slskd checks.
- Structured logging with a redaction filter that keeps secrets (API keys, Soulseek password, Plex token) out of logs and log files.
- Persisted command queue with `/api/v1/command` (Lidarr-shaped) and Quartz-scheduled tasks whose last/next run survive restarts.
- SignalR hub for real-time UI events.
- React 19 + TypeScript + Vite frontend shell (routing, theme, API client, loading/empty/error states).
- Bundled slskd supervisor: downloads the pinned release, renders its configuration and runs it as a separate process.
- Docker image (amd64/arm64): multi-stage build with s6-overlay (`PUID`/`PGID`/`UMASK`/`TZ`), ffmpeg/ffprobe, Chromaprint `fpcalc`, Deno and slskd.
- CI workflow building and testing the backend and the frontend, including the ported-code attribution check.

### Security
- No secrets in the repository; `.env` is git-ignored and redaction is applied before log output.
