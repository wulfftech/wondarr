---
title: Configuration
description: The config.yml sections and the keys you are most likely to change.
---

Wondarr writes `/config/config.yml` on first start. Most settings are better changed in the web UI; this page lists the ones that only live in the file, plus the defaults of the rest. Restart the container after editing the file.

## Environment variables

Any key can be set from the environment as `APP__<SECTION>__<KEY>`, with the key in upper case and its underscores kept. A nested section adds another `__`. An environment value wins over `config.yml`, and the web UI shows that setting as read-only.

| `config.yml` | Environment |
|---|---|
| `search.search_on_add: false` | `APP__SEARCH__SEARCH_ON_ADD=false` |
| `server.url_base: /wondarr` | `APP__SERVER__URL_BASE=/wondarr` |
| `soulseek.search.max_searches: 30` | `APP__SOULSEEK__SEARCH__MAX_SEARCHES=30` |

This page lists the keys you are likely to change, not every internal one.

## server

| Key | Default | Meaning |
|---|---|---|
| `port` | `1077` | HTTP port (1 to 65535). |
| `bind_address` | `*` | Address to bind; `*` is all interfaces. |
| `url_base` | empty | Path prefix behind a reverse proxy, such as `/wondarr`. |
| `auth` | `forms` | `forms`, `none` or `external`. See [First run](/wondarr/getting-started/first-run/). |
| `auth_required` | `disabledForLocalAddresses` | Or `enabled`. |
| `api_key` | generated | 32 lowercase hexadecimal characters. |

## soulseek

The usual settings are in **Settings → Soulseek**; see [Soulseek](/wondarr/sources/soulseek/).

| Key | Default | Meaning |
|---|---|---|
| `mode` | `bundled` | `bundled` or `external` (your own slskd). |
| `username`, `password` | none | The Soulseek account. |
| `listen_port` | `50300` | 1024 to 65535. |
| `share_library` | `true` | Share your library. |
| `shared_folders` | `/data/music` | Folders to share. |
| `upload_slots` | `10` | Concurrent uploads. |
| `upload_speed_limit_kib` | `2000` | KiB/s; `0` is unlimited. |
| `distributed_network` | `true` | Join the distributed network. |
| `downloads_dir` | `/data/downloads/slskd` | Where slskd saves completed files. |
| `incomplete_dir` | `/data/downloads/slskd/incomplete` | Partial downloads. |
| `external.url`, `external.api_key` | none | Your slskd, in external mode. |
| `external.web_username`, `external.web_password` | none | slskd's web login, only needed to write slskd's settings. |
| `external.rescan_shares` | `false` | Ask your slskd to rescan its shares after imports. |
| `search.max_searches` | `30` | Searches allowed per `search.window_seconds`. |
| `search.window_seconds` | `240` | The window for the limit above. |
| `search.max_outstanding` | `2` | Searches at once. |
| `search.min_spacing_seconds` | `5` | Gap between two searches. |

The `soulseek.search` limits are Soulseek's own, and Wondarr will not accept values above them.

## search

| Key | Default | Meaning |
|---|---|---|
| `search_on_add` | `true` | Search at once when a monitored song is added. |
| `missing_interval_hours` | `6` | How often Missing Search runs (1 to 168). |
| `missing_batch_size` | `50` | Songs per Missing Search run (1 to 500). |
| `upgrade_interval_hours` | `24` | How often Upgrade Search runs (1 to 168). |
| `upgrade_batch_size` | `50` | Songs per Upgrade Search run (1 to 500). |
| `max_auto_attempts_per_search` | `4` | Candidates tried per search before giving up (1 to 10). |
| `max_active_downloads` | `3` | Downloads in flight at once (1 to 5). |
| `backoff_hours` | `[1, 6, 24, 72, 168]` | Wait before a song that found nothing is searched again. |
| `max_container_size_mb` | `1500` | Largest usenet post downloaded whole. Torrents are not limited. |

## import

| Key | Default | Meaning |
|---|---|---|
| `recycle_bin_path` | `/config/recycle` | Where replaced files go. |
| `recycle_bin_cleanup_days` | `7` | How long a recycled file is kept; `0` is forever (0 to 365). |
| `container_staging_path` | `/data/downloads/containers` | Where a torrent or usenet file is staged. Keep it on the same filesystem as the torrent client's downloads. |
| `fake_lossless_check` | `reject` | `reject` or `off`. See [Quality and upgrades](/wondarr/library/quality-and-upgrades/). |
| `set_permissions` | `false` | Set the Unix mode of files and folders Wondarr places. |
| `file_mode`, `folder_mode` | `0664`, `0775` | The modes, in octal, when `set_permissions` is on. |
| `remote_path_mappings` | none | A list of `host`, `remote_path`, `local_path` for download clients that report other paths. |

## youtube

See [YouTube Music](/wondarr/sources/youtube/) for every key, including `enabled` (default `false`), `search_limit` (20) and the `ytdlp` pacing settings.

## backup

| Key | Default | Meaning |
|---|---|---|
| `interval_days` | `7` | How often the scheduled backup runs (1 to 365). |
| `retention_days` | `28` | How long scheduled backups are kept (1 to 3650). |
| `folder` | `/config/backups` | Where backups are stored. |

## queue

| Key | Default | Meaning |
|---|---|---|
| `active_poll_seconds` | `10` | Queue check interval while downloading. |
| `idle_poll_seconds` | `60` | Queue check interval when idle. |
| `start_timeout_minutes` | `10` | A grab the peer never acknowledged is failed after this. |
| `remote_queue_timeout_minutes` | `30` | A grab waiting in the peer's queue is failed after this. |
| `stall_timeout_minutes` | `5` | A download that moves no bytes is failed after this. |

## reference

| Key | Default | Meaning |
|---|---|---|
| `auto_accept_threshold` | `0.90` | Confidence at which a reference file is accepted without review. |
| `duration_tolerance_ms` | `5000` | How far a file's length may differ from the recording's. |

## acoustid

| Key | Default | Meaning |
|---|---|---|
| `client_key` | none | Your AcoustID client key (get one at `acoustid.org/new-application`). Without it, downloads are verified by probe and length only. Also set under **Settings → General → Metadata services**, where it is never shown again. |
| `accept_score` | `0.7` | Fingerprint score that counts as a match. |
| `review_score` | `0.5` | Score at which a match is imported with a low-confidence badge. |
| `strict` | `false` | Fail a download that scores below `accept_score` instead of reviewing it. |
| `requests_per_second` | `3` | AcoustID allows three. |

## lastfm

| Key | Default | Meaning |
|---|---|---|
| `api_key` | none | Your Last.fm API key (get one at `last.fm/api/account/create`). Optional. With a key, a song's page shows what Last.fm knows about it (listeners, tags, a short wiki text, the artist and similar tracks), and a Last.fm import list with no key of its own reads with this one. Also set under **Settings → General → Metadata services**, where it is never shown again. Environment: `APP__LASTFM__API_KEY`. |

Wondarr asks Last.fm for at most four requests a second, keeps each answer in memory for 24 hours, and backs off for as long as Last.fm asks when it is rate limiting. A key set by an environment variable shows as read-only in the web UI.

## media

| Key | Default | Meaning |
|---|---|---|
| `ffprobe_path`, `ffmpeg_path`, `fpcalc_path` | `ffprobe`, `ffmpeg`, `fpcalc` | The bundled tools. |
| `timeout_seconds` | `120` | How long one tool may run. |
| `decode_check` | `true` | Decode a file end to end when probing it. |
| `fingerprint_length_seconds` | `120` | How much audio is fingerprinted. |

## lyrics

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Look up lyrics on LRCLIB when a song is filed. |
| `base_url` | `https://lrclib.net/` | The LRCLIB server. |
| `timeout_seconds` | `10` | How long one lookup may take. |

## log

| Key | Default | Meaning |
|---|---|---|
| `level` | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. |
| `console_format` | `json` | `json` or `text`. The log files are always JSON. |
| `retained_files` | `7` | Daily log files kept. |
| `file_size_limit_mb` | `10` | Size at which a log file rolls over. |
