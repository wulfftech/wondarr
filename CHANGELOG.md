# Changelog

All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released).

## [Unreleased]

## [0.1.1] — 2026-10-11

Fixes from a full code review of 0.1.0 (`docs/reviews/CODE_REVIEW_2026-10-10.md`): files that move are always recorded, compaction can no longer get stuck, a slow slskd or indexer no longer stops the app or a search batch, and the release workflow publishes an image only after its tests and smoke checks pass. Update by pulling `:0.1` (or `:0.1.1`).

**Upgrading from 0.1.0:** the first start applies one database migration and, before it, writes a backup to `/config/backups/scheduled/` (named `wondarr_pre-unknown_…zip` this once, since 0.1.0 did not record its version). Leave about twice the database's size free in `/config`; if the backup cannot be written, Wondarr stops before changing anything and says why in the log.

### Added
- **An automatic backup before an upgrade migrates your database.** When a new version has database changes to apply, Wondarr first writes a backup (`wondarr_pre-<previous version>_<time>.zip`, in the scheduled backups, cleared out by the usual retention). If that backup fails, nothing is migrated and Wondarr does not start, so a failed upgrade can always be rolled back.
- **Restores keep the last three sets of replaced files** (`*.pre-restore`, `.pre-restore.1`, `.pre-restore.2`), and a restore that is interrupted part-way is finished at the next start instead of leaving a fresh `config.yml` with a new API key. How to undo a restore is in the deployment guide.
- **`backup.max_restore_database_bytes`** (default 4 GiB): the most a restore will extract from an uploaded backup's database.

### Changed
- **Choosing a song's album now moves its file.** In Change album, a managed file is moved to the folder the naming template renders for the new album and retagged, with its lyrics and cover, and Plex is asked to scan the old and new folders. The dialog says "The file will be moved to the new album's folder" and follows the move until it finishes. `PUT /api/v1/song/{id}/albumcontext` returns the new `refileCommandId` (nothing is queued for a song with no file or only a reference file, which are never touched). While a Compact run has the song the choice is refused with a 409.
- **Compact empties Plex's trash only when that is safe.** It leaves the trash alone, logs why and says so in the run's result when the scan was not seen to finish, when Plex's own "empty trash automatically" (`autoEmptyTrash`) is on, or when a folder of the Plex section cannot be confirmed present (missing, empty like an unmounted share, or not under any library or reference library root). Before, it emptied after a 10 minute timeout whatever Plex was doing.
- **Housekeeping's `VACUUM` waits for a quiet moment.** It is skipped, and logged as "imports in progress", while a download is waiting to be imported or an import is running. The app's SQLite connections now wait up to 60 seconds for a lock.
- **Releases publish a tested image.** The image is pushed under a `sha-*` tag only; `:<version>`, `:<major>.<minor>`, `:latest` and `:develop` move after the tests and the amd64 and arm64 smoke runs pass, and `:latest` follows only the highest stable release. The ffmpeg image is pinned by digest and every GitHub Action by commit.
- **Switching between the bundled slskd and your own takes effect at once.** Saving **Settings → Soulseek → Connection** with a different mode stops the bundled slskd (or starts it) without restarting Wondarr, and the API no longer answers `restartsWondarr`.
- **Stopping Wondarr fits in Docker's default 10 seconds.** The container's process manager and the .NET host are given a budget that adds up to less than 10 s (`S6_SERVICES_GRACETIME` 8.5 s, `S6_KILL_GRACETIME` 1 s, host shutdown 8 s), and slskd is asked to stop (SIGTERM, up to 4 s) before it is killed, so it can log out and save its transfers.

### Fixed
- **A file that moved is always recorded.** A recycle whose time stamp is refused (library files owned by another user, or a mount that refuses it) no longer fails after the old file is already in the bin; the placer also says so, instead of "untouched", when a recycle fails after the move. An import, library move or conversion whose placement moved the file and then failed (a refused chmod, a download that could not be removed) is now recorded as placed instead of leaving the song pointing at nothing, every database write after a move ignores a shutdown, and a restart finds the file an interrupted import had already placed instead of grabbing a second copy beside it. An upgrade that renames the file takes its `.lrc` sidecar along, and conversions and library moves decide from the song as it is when they get their turn.
- **The recycle bin cannot delete another library's files.** A library cannot be created or moved onto, around or inside the recycle bin, and the bin's cleanup skips any library or reference root it finds under it (and does nothing at all when the bin sits inside one). Health reports an error when `import.recycle_bin_path` overlaps a library or reference root.
- **A transcoded YouTube download is no longer lost when AcoustID is down.** The queue item now points at the transcoded file, so a deferred import finds it again and a rejection deletes it.
- **A failed import no longer re-files the song.** The album alignment made before tagging is dropped when tagging or placement fails.
- **A slow media tool no longer blocklists a good file.** An ffprobe, decode-check or fpcalc timeout defers the import.
- **Formats Wondarr cannot tag are turned away early.** APE, WavPack and WMA are rejected (`untaggableFormat`) instead of being grabbed again and again, unless a conversion rule in the library turns them into a format it can tag.
- **A rejected AcoustID key is reported.** Health shows an error naming AcoustID and where to fix the key, and imports waiting on it fail after about an hour and a half with a message that names the key, without blocklisting the file.
- **ffmpeg never waits on a terminal.** The transcode, decode check and fingerprint cut pass `-nostdin`, and the transcode passes `-n`.
- **A compaction that fails can no longer block its library, and one that is cut short is finished.** A move that keeps failing is retried three times and then given up on: its file goes back where it came from (or stays, named in the log and the result) and the song points at it. A move whose song was deleted sends its staged file to the recycle bin. A song with a move in flight cannot be deleted (409). After a restart, Wondarr queues Compact for every library with unfinished moves, and a **Compaction** health warning names the library until they are done. Database writes after a file has moved are no longer cancelled by a shutdown, a file an interrupted run had already placed is found and recorded, and a placement that reports an error after putting the file down is recorded as placed.
- **Choosing an album during a compaction is no longer overwritten.** The choice is refused until the run finishes, and a run never applies its plan over an album you pinned after it was made.
- **"Adopt now" beside a scan no longer copies every file twice.** It takes the same per-library gate as the scan, and adoption holds each song's file lock while it files the copy.
- **A backup from a newer Wondarr is refused** at restore time, naming the newest unknown migration, and Health shows an error when the live database was written by a newer Wondarr.
- **Two backups at once can no longer tear each other.** Backups and restore staging are serialised, temporary files are uniquely named, an existing backup is never overwritten, and leftover temporary files older than an hour are deleted at start.
- **A restore sets the database aside complete.** The write-ahead log is folded into the database first, so renaming `wondarr.db.pre-restore` back loses nothing.
- **A slow slskd no longer stops Wondarr.** In external mode one poll that took more than 5 seconds ended the whole app, which then crash-looped; it is now reported as "slskd did not answer within 5 s". In bundled mode a slow poll silently ended the supervisor, and after the first minute one failed poll killed a healthy slskd; now a restart needs 3 failed polls in a row ("slskd not answering (n/3)").
- **A search provider that times out fails one query, not the whole batch.** Soulseek, YouTube Music, Torznab, Newznab, Gazelle and Prowlarr timeouts are failed queries; a Missing or Upgrade search carries on with the next song, a timeout during a grab is a failed grab rather than a cancelled run, and a slow Prowlarr search is logged instead of coming back empty without a trace. yt-dlp exiting with code 101 is a failed download, not a cancellation.
- **A restart no longer resets the Soulseek search allowance.** The 30-per-4-minutes window is kept in `/config/slskd/search-budget.json` and reloaded at start.
- **A slskd left running by a crashed Wondarr is cleaned up.** Wondarr keeps slskd's pid in `/config/slskd/slskd.pid` and kills a leftover slskd (never any other process) before starting a new one.
- **A cancelled Soulseek search is stopped before it is deleted**, and its place in the search allowance is held until slskd says it is gone.
- **A process whose output pipe stays open after a kill can no longer hang a download** (the drain is capped at 5 seconds).

## [0.1.0] — 2026-10-10

The first public release: every phase of the plan (0–8) is in. Install: `docker pull ghcr.io/wulfftech/wondarr:0.1.0` (linux/amd64, linux/arm64); documentation at https://wulfftech.github.io/wondarr/.

### New since the release candidate (0.1.0-rc.1)

#### Added
- **Wondarr tells you when a newer version is out.** It never updates itself (it runs in a container), but twice a day it asks GitHub's public releases list for the newest release. An **Update available** badge appears in the header (dismiss it for that version), **System → Updates** shows the release notes and how to update with Docker Compose, `docker run` and Unraid, **System → Status** has an Update row, and a new **On update available** notification event is sent once per version. A release candidate hears about newer candidates and the release; a development build is never told it is out of date. Turn it off under **Settings → General → Updates** or with `update.check_enabled` / `APP__UPDATE__CHECK_ENABLED`; off, Wondarr makes no request at all. New API: `GET /api/v1/update`, `POST /api/v1/update/check`, `GET`/`PUT /api/v1/update/settings`; new task `CheckForUpdates` (every 12 hours).
- **A page for every song.** Click a song's title (in the Library, Wanted, Activity's Queue and History) to open its page: the cover and status at the top with Search, Interactive search (inline), Change album, Convert, Move and Delete; then tabs for the file (quality, codec, ReplayGain, AcoustID, where it came from), About (MusicBrainz, Deezer and every release it appears on), Last.fm (with a key), Lyrics, History, and its Queue and blocklist. The Library keeps its filters in the address, so Back returns to the same list, and the Change album dialog scrolls and gets a filter box when a song is on many releases.
- **A wider Change album dialog.** Each album shows its cover, its own artist (a compilation reads "by Various Artists" with a Compilation badge), the track number, and the release and first-release years.
- **The data behind a song page** in the API: the file in full (sample rate, bit depth, ReplayGain, AcoustID, where it came from), `GET /song/{id}/details` (the releases it appears on, MusicBrainz and Deezer facts including BPM), `GET /song/{id}/lyrics`, and `songId` filters on the queue and blocklist.
- **Optional Last.fm and AcoustID keys in Settings, and Last.fm facts on the song page's data.** Settings → General has a **Metadata services** card: the AcoustID client key and a new Last.fm API key (`lastfm.api_key`, `APP__LASTFM__API_KEY`), each stored without ever being shown again, each with a **Test** button and a link to where the key is made; a key set by an environment variable shows as read-only. With a Last.fm key, `GET /api/v1/song/{id}/details` gains `lastFm` (listeners, plays, top tags, a plain-text wiki, the artist's bio and up to ten similar tracks, with the Wondarr song when you already own it). Last.fm calls stay under four a second, are cached for 24 hours and back off when Last.fm rate limits; a Last.fm import list with no key of its own reads with the global one.
- **Create your login on the login page.** With `auth: forms` and no login yet, the login page asks for one (username, password twice) instead of a sign-in form that can never succeed; from the local network only, and it signs you in.

#### Changed
- **Song page and Change album polish.** A cover that fails to load shows the placeholder instead of an empty square, editions of one album are grouped into a single expandable row ("N editions") in Change album and Appears on, and Library stays highlighted in the sidebar on a song page.

#### Fixed
- **Songs added together no longer lose their search when the download slots are full.** Adding ten songs at once left most of them "Cancelled — No free download slot" and waiting for the next 6-hourly Missing Search. A search that found something now waits for a slot (up to `search.slot_wait_minutes`, 30 by default; Activity → Tasks shows "Waiting for a download slot") and grabs the best candidate it already found when one frees, without searching again. Waiting searches no longer hold up other tasks.
- **A file from an unrelated track is no longer grabbed.** A Soulseek result was accepted whatever its title, and a path with "Various Artists" or "OST" in it skipped the artist check entirely, so "Lonely Girl" on a jazz anthology was downloaded for "Story of a Girl". A candidate's own track title (from the file name, never the folders) now has to be close to the song's, or it is rejected with `Title mismatch`; a title that holds the other as a parenthesised part ("Absolutely (Story of a Girl)") still matches. On a compilation path the file name must name the artist or the song almost exactly, or the result is rejected with `Artist mismatch`.
- **A busy MusicBrainz no longer turns Add songs into a server error.** A `Retry-After` from any metadata host (429 or 503) now holds back every later request to that host. Background work (syncs, pasted lists) waits it out and keeps its three retries; a search someone is waiting on retries twice and fails fast when the host asks for more than 15 seconds. If it still does not answer, the Search and Album tabs show what Deezer found with a notice above the results, and `POST /song/lookup` and `GET /album/lookup` send the list as before with an `X-Wondarr-Partial: musicbrainz` header. Only when neither provider answers is it a 503 problem ("Song search is unavailable", with a `Retry-After`) whose detail the page shows; the song and album endpoints that read from MusicBrainz or Deezer answer a provider outage with the same 503 instead of a 500.
- **The Match queue writes songs as `Artist - Title`**, like everywhere else, and shows the top candidate's length difference in its own **Δ Length** column.
- **Pressing Add on one search result no longer spins every Add button**, and an error shows on the row that failed.
- **The Tasks page's Task column is only as wide as its longest name.**
- **A reference library scan no longer downloads songs you already own.** Songs found in a reference library were being searched for (and grabbed again) the moment they were added, because the search ran before their file was recorded. They are now added without a search, and a song a reference file identifies is skipped by the missing-song search and left out of Wanted. A song whose file goes missing is wanted again, as before.
- **Scanning a second reference library is no longer swallowed by the first scan.** Starting a scan while another library's scan was queued or running returned the other scan and never scanned the library you asked for. A queued command is now reused only when it is the same command with the same settings, so two libraries queue two scans.

### Since the first development builds
#### Added
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

#### Upgrading from a development build
- A database from any earlier build, including the 0.0.1-alpha.1 image under its old name (`compilarr.db`), is migrated on the first start; take a backup first (System → Backup → Back up now).

#### Changed
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
