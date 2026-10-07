# Architecture

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — components, pipeline, plugin interfaces, data model, scheduler, API. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

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

The core is a single long-running process (one container). Soulseek access goes through **slskd bundled in the same image and supervised by the app as a child process** (`SlskdHost`; the app renders its `slskd.yml` from its own Soulseek settings — credentials, shares, slots; §9.5; an external-slskd mode is Phase 5); torrent/usenet clients are the user's existing ones; YouTube is handled in-process via yt-dlp. Nothing else is required to run.

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
| `song` | `title`, `artist_credit` (display), `primary_artist_id`, `mb_recording_id?`, `mb_work_id?`, `isrcs` (json), `spotify_id?`, `deezer_id?`, `ytm_video_id?`, `duration_ms?`, `version_flags` (json wire names: live/remix/acoustic/instrumental/acapella/radio_edit/edit/extended/remaster/explicit/clean/cover/karaoke/demo/slowed/8d/bassboosted), `monitored`, `quality_profile_id`, `source_profile_id`, `library_id`, `added_by` (ui/api/list:{id}), `tags`. The album context and the file point at the song (`album_context.song_id`, `song_file.song_id`, both unique); there is no `file_id` column (DECISIONS 2026-09-28 build session 2 #4) |
| `song_artist` | `song_id`, `artist_id`, `role` (main/featured), `position` |
| `album_context` | `song_id`, `kind` (album/single/ep/compilation/pseudo_singles), `album_title`, `album_artist`, `album_key` (real release MBID or synthetic pseudo-album UUID; one per folder), `mb_release_group_id?`, `track_no?`, `disc_no?`, `total_tracks?`, `date` (identical per folder), `original_date?`, `label?`, `cover_url?`, `is_various_artists`, `sticky`, `pinned` |
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
| `reference_library` | `name` (unique, case-insensitive), `root_path`, `mode` (reference/adopt), `library_id?` (the managed library songs are filed under; the target when adopting), `enabled`, `last_scanned_at?`, `last_scan_message?` |
| `reference_file` | `reference_library_id`, `relative_path` (`/`-separated, unique per library), `size`, `modified_at` (UTC), `probe?` (json), `tags?` (json), `fingerprint?`, `acoust_id?`, `song_id?`, `confidence`, `state` (pending/identified/ambiguous/unmatched/adopted/unreadable/missing/skipped), `identified_by?`, `message?`, `last_seen_at`, `missing_since?` |
| `match_candidate` | `reference_file_id`, `rank` (1 = best), `identity` (json: source, recording MBID, Deezer id, title, artist credit, length, album), `score`, `reason` |
| `compact_move` | `library_id`, `song_id`, `from_path?`, `staged_path?` (where the file waits while Plex forgets its old album), `to_path?` (the planner's expected path, informative), `final_path?`, `proposed` (json: the album context the move writes), `state` (planned/staged/placed/failed), `message?` — one row per move of a Compact library run, written before each step so an interrupted run resumes |
| `slskd_state` | `version`, `logged_in`, `sharing`, `shared_dirs` (json), `restart_required`, `last_checked_at` |
| `setting` | `key`, `value` (json) — general settings, API key, auth, url base |

### 5.5 Scheduler and jobs

| Job | Default interval | Notes |
|---|---|---|
| Wanted search (missing) | every 6 h, plus immediately on add | Per-song backoff after repeated failures: 1 h → 6 h → 24 h → 72 h → weekly (capped); "search on add" (`search.search_on_add`, default on) queues a `SongSearch` per added monitored song |
| Cutoff-unmet upgrade search | every 24 h (`search.upgrade_interval_hours`) | Bounded per run (`search.upgrade_batch_size`, default 50 songs) to be polite to Soulseek; its own backoff counts only `Upgrade` runs |
| Queue poll | every 10 s (active) / 60 s (idle) | Progress + completion detection; per-source stall timeouts |
| Import scan of completed folder | on completion event + every 5 min | Catches downloads finished while the app was down |
| Import list sync | per list (default 12 h) | Adds new items; policy decides about removals |
| Reference library scan | daily + on demand | Detects new/changed files; identification pipeline; feeds the Match queue |
| Compact library | on demand | Re-plans album assignments under the `fewest_albums` policy and applies moves with the Plex scan/empty-trash sequence |
| Metadata refresh | weekly | Re-pull cover/ISRC/durations for songs missing them |
| Housekeeping | daily | Vacuum, expire blocklist entries, prune old candidates/search runs |
| Health checks | every 15 min | slskd reachable & logged in; clients reachable; root folders writable; yt-dlp up to date |
| Backup | weekly (`backup.interval_days`, default 7) | The `Backup` command: a zip of `wondarr.db` (SQLite's online backup API, never a copy of the live WAL file) and `config.yml` under `/config/backups/{scheduled,manual}`; scheduled backups older than `backup.retention_days` (default 28) are deleted, manual ones never |

The scheduler must survive restarts (state in DB), run jobs with concurrency limits per source (Soulseek: 1 search at a time, ≤ N downloads in flight; YouTube: 1 download at a time with sleep between; torrents: unlimited), and expose "Run now" for every job.

### 5.6 API (v1) sketch

Conventions mirror the *arrs: `X-Api-Key` header (also `?apikey=`), JSON, `/api/v1`, pagination via `page`/`pageSize`/`sortKey`/`sortDirection`, and a `/api/v1/system/status` endpoint that dashboards (Homarr/Homepage) and Notifiarr-style tools can probe.

```
GET    /api/v1/system/status | /health | /system/task
GET    /api/v1/log?page=&pageSize=&level=&filter=   (the app's own log, newest first; `X-Wondarr-Log-Truncated: true` when the scan stopped at its 10 MB bound)
GET    /api/v1/log/file   GET /api/v1/log/file/{name}   (text/plain)
GET    /api/v1/system/backup  | POST /api/v1/system/backup  DELETE /api/v1/system/backup/{id}
GET    /api/v1/system/backup/{id}/download
POST   /api/v1/system/backup/restore/{id} | /restore/upload   (stage the restore; answered `restartRequired`, applied on the next start)
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
POST   /api/v1/command  {name: SongSearch|MissingSearch|UpgradeSearch|CutoffUnmetSearch|ImportListSync|RescanLibrary|RefreshSong…}
GET/PUT /api/v1/qualityprofile | /qualitydefinition | /sourceprofile
GET/POST/PUT/DELETE /api/v1/source | /downloadclient | /importlist | /notification | /library
GET    /api/v1/importlist/schema | POST /api/v1/importlist/{id}/sync (202, an ImportListSync command)
POST   /api/v1/importlist/csv/preview  {sourceText, settings?} -> format, headers, row count, sample, problems
GET    /api/v1/album/lookup?term= | /album/releasegroup/{id}/releases | /album/{musicbrainz|deezer}/{id}/tracks
POST   /api/v1/album/add  {source, id, trackKeys?, libraryId?, qualityProfileId?, monitored?}  (202, an AddAlbum command)
GET    /api/v1/song/{id}/album  (the release a song is filed under, for "add the rest of this album")
POST   /api/v1/song/convert/preview | /song/convert  {songIds? | libraryId?, rule?}  (dry run; 202, a ConvertFiles command)
POST   /api/v1/library/{id}/preview  {songId}  -> rendered path
POST   /api/v1/library/{id}/scan     (adopt existing files)
GET    /api/v1/tag  …
WS/SSE /api/v1/events  (queue progress, imports, health)
```

**Ecosystem compatibility (deliberate, contract-tested).** Three tools that talk to Lidarr work against Wondarr unchanged, and `tests/Wondarr.Api.Tests/EcosystemContractTests.cs` makes exactly their calls (verified from their source on 2026-10-07): **Homepage**'s Lidarr widget (`GET /api/v1/artist`, `/wanted/missing`, `/queue/status` with `?apikey=`; it reads the artist array's length, `totalRecords`, `totalCount`); **Unpackerr** (pages `GET /api/v1/queue?page=&pageSize=&sortKey=timeleft&includeUnknownArtistItems=true` and reads Lidarr's record fields — `title`, `status`, `trackedDownloadStatus`, `protocol`, `size`, `sizeleft`, `downloadId`, `outputPath`, `artistId`, `statusMessages` — which the queue resource carries beside its own; Soulseek and YouTube items say so in `protocol`, and Unpackerr only extracts torrent and usenet downloads); **autobrr** (`GET /api/v1/system/status` as its connection test, reading `version`; `POST /api/v1/release/push`, answered with one object `{ approved, rejected, temporarilyRejected, rejections }`, or a 400 with the validation array when the title is missing). Until a torrent or usenet source exists (Phase 7) every push is rejected with the reason and the wanted song it matched; nothing is grabbed or stored (DECISIONS build session 6 #7). The API reference (Scalar) is at `/docs`, reading `/docs/v1/openapi.json`; both are served without a key (#8), every endpoint they describe still needs one. Prowlarr cannot register us as an application (its app list is hard-coded), so indexers are added by pasting Prowlarr's per-indexer Torznab/Newznab URL and API key, or by pointing at Prowlarr's `/api/v1/search` once.

**Import lists (Phase 6, ADR-0012).** A synced list is an `import_list` row of a provider's type (`csv`, `deezerPlaylist`, `deezerArtistTop` and `youtubeMusicPlaylist` now; Last.fm and ListenBrainz follow) with the provider's settings (secrets masked like a notification's), a policy, a quality profile, a library, an interval (`sync_interval_hours`, 0 = only when asked) and, for an uploaded file, its text in `source_text` — so the list is in the database and its backups. `ImportListSync` runs hourly and syncs every enabled list whose interval has passed (or one list, from "Sync now"): the provider reads the source in order; items are matched to the stored ones by the source's own id (`external_id`), new ones stored pending, known ones re-ordered (`position`), and ones gone from the source marked `removed_at` — nothing is deleted at this step. Pending items then go through the bulk add's pipeline: each is resolved by its strongest id first — recording MBID, ISRC, Deezer id — and by its text last, a text match whose length is more than 10 s from the list's going to the review screen instead; the resolved ones are added in one batch (one album-policy pass, one search). A source that cannot be read fails the sync with the reason on the list (`last_sync_message`), and the scheduled run carries on with the next list. The CSV provider reads Exportify's export by its English column names, or — Exportify writes its header in the user's language — by the `spotify:track:` URIs in the first column and the base columns' positions, and any other CSV by usual names or the user's column mapping (DECISIONS build session 7 #7, #8). The Deezer playlist provider pages a public playlist's tracks (`GET /playlist/{id}/tracks`, 100 at a time) and resolves each by its Deezer id, then its ISRC; the Deezer artist-top provider takes a link, an id or a name (the exact name wins, else the most fanned of the first five) and reads `GET /artist/{id}/top`, whose rows carry no ISRC; the YouTube Music playlist provider takes a `playlist?list=…` link or a bare id and reads InnerTube's `browse` call, paging continuations until the last page, and resolves each row by its text. After the resolve, the list's **policy** applies to the songs of items that just left the source — only a song this list added (`added_by = list:{id}`) that no item still in any list wants: `AddOnly` leaves it, `AddAndUnmonitor` unmonitors it, `Mirror` deletes it when it has no file and nothing for it is queued, and unmonitors it otherwise; a list never deletes a file (#9). Then its **playlists** are written: a Plex audio playlist named after the list, made once (`POST /playlists?type=audio&smart=0&uri=server://{machine}/com.plexapp.plugins.library/library/metadata/{keys}`) and then reconciled in place — missing tracks appended, extra and duplicate items removed, items moved into the source's order (`PUT …/items`, `DELETE …/items/{playlistItemID}`, `PUT …/items/{playlistItemID}/move?after=`) — so its id and what the user did with it in Plex survive; each track's rating key is found by a title search in the song's own section matched on the file's Plex-side path, and cached on the list item while the file stays put. A track Plex has not scanned yet joins at a later write: the hourly `ImportListSync` rewrites every list's playlists even when no list is due. An `.m3u8` (UTF-8, `#EXTINF`, paths relative to it) can be written to `<library root>/Playlists/<list name>.m3u8` as well (#10).

### 5.7 Notifications

A `notification` row is one endpoint: its provider, its settings and the events it wants (`grab`, `import`, `upgrade`, `failure`, `health`). The dispatcher subscribes to the event aggregator and does nothing on the publisher's thread but enqueue — a search or an import never waits for an endpoint, and a saturated queue drops messages instead of stalling the publisher. Sends run one at a time on the dispatcher's own loop through a named `HttpClient` that never follows redirects: a `Retry-After` of at most 60 s is waited out once, and any other failure is logged and dropped. A field marked secret is stored as it was entered but always reads back as `********`, and no log line or error message names a notification's URL, headers or credentials. The providers are Webhook, whose body keeps Lidarr's shape and its PascalCase `eventType` values (`Test`, `Grab`, `Download`, `DownloadFailure`, `Health`) so an existing *arr webhook consumer keeps working, with Wondarr's camelCase `song` object in place of Lidarr's artist and album; Discord, which posts one embed per event to a channel webhook — title and body from the message, a colour per event (blue for a grab or a test, green for an import or an upgrade, red for a failure, orange for a health warning) and fields for the song, the release and an upgrade — with the webhook URL kept secret and accepted only over https on a `discord.com`/`discordapp.com` host; and Apprise, which posts the message to an Apprise API server either through a persistent-storage configuration key or statelessly with the service URLs in the body, so one notification reaches any of the hundred-odd services Apprise speaks for.
