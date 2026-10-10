# Changelog

All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released).

## [Unreleased]

### Added
- **A page for every song.** Click a song's title (in the Library, Wanted, Activity's Queue and History) to open its page: the cover and status at the top with Search, Interactive search (inline), Change album, Convert, Move and Delete; then tabs for the file (quality, codec, ReplayGain, AcoustID, where it came from), About (MusicBrainz, Deezer and every release it appears on), Last.fm (with a key), Lyrics, History, and its Queue and blocklist. The Library keeps its filters in the address, so Back returns to the same list, and the Change album dialog scrolls and gets a filter box when a song is on many releases.
- **A wider Change album dialog.** Each album shows its cover, its own artist (a compilation reads "by Various Artists" with a Compilation badge), the track number, and the release and first-release years.
- **The data behind a song page** in the API: the file in full (sample rate, bit depth, ReplayGain, AcoustID, where it came from), `GET /song/{id}/details` (the releases it appears on, MusicBrainz and Deezer facts including BPM), `GET /song/{id}/lyrics`, and `songId` filters on the queue and blocklist.
- **Optional Last.fm and AcoustID keys in Settings, and Last.fm facts on the song page's data.** Settings → General has a **Metadata services** card: the AcoustID client key and a new Last.fm API key (`lastfm.api_key`, `APP__LASTFM__API_KEY`), each stored without ever being shown again, each with a **Test** button and a link to where the key is made; a key set by an environment variable shows as read-only. With a Last.fm key, `GET /api/v1/song/{id}/details` gains `lastFm` (listeners, plays, top tags, a plain-text wiki, the artist's bio and up to ten similar tracks, with the Wondarr song when you already own it). Last.fm calls stay under four a second, are cached for 24 hours and back off when Last.fm rate limits; a Last.fm import list with no key of its own reads with the global one.
- **Create your login on the login page.** With `auth: forms` and no login yet, the login page asks for one (username, password twice) instead of a sign-in form that can never succeed; from the local network only, and it signs you in.

### Changed
- **Song page and Change album polish.** A cover that fails to load shows the placeholder instead of an empty square, editions of one album are grouped into a single expandable row ("N editions") in Change album and Appears on, and Library stays highlighted in the sidebar on a song page.

### Fixed
- **A busy MusicBrainz no longer turns Add songs into a server error.** A `Retry-After` from any metadata host (429 or 503) now holds back every later request to that host. Background work (syncs, pasted lists) waits it out and keeps its three retries; a search someone is waiting on retries twice and fails fast when the host asks for more than 15 seconds. If it still does not answer, the Search and Album tabs show what Deezer found with a notice above the results, and `POST /song/lookup` and `GET /album/lookup` send the list as before with an `X-Wondarr-Partial: musicbrainz` header. Only when neither provider answers is it a 503 problem ("Song search is unavailable", with a `Retry-After`) whose detail the page shows; the song and album endpoints that read from MusicBrainz or Deezer answer a provider outage with the same 503 instead of a 500.
- **The Match queue writes songs as `Artist - Title`**, like everywhere else, and shows the top candidate's length difference in its own **Δ Length** column.
- **Pressing Add on one search result no longer spins every Add button**, and an error shows on the row that failed.
- **The Tasks page's Task column is only as wide as its longest name.**
- **A reference library scan no longer downloads songs you already own.** Songs found in a reference library were being searched for (and grabbed again) the moment they were added, because the search ran before their file was recorded. They are now added without a search, and a song a reference file identifies is skipped by the missing-song search and left out of Wanted. A song whose file goes missing is wanted again, as before.
- **Scanning a second reference library is no longer swallowed by the first scan.** Starting a scan while another library's scan was queued or running returned the other scan and never scanned the library you asked for. A queued command is now reused only when it is the same command with the same settings, so two libraries queue two scans.

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
