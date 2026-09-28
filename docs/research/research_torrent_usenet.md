# Research: acquiring a SINGLE SONG from Torrent and Usenet sources

Date: 2026-09-28. Scope: design input for a single-song *arr-style app (Soulseek primary, YouTube secondary, torrents/usenet as additional sources).

Method note: the sandbox egress proxy blocks torznab.github.io, sabnzbd.org, nzbget.com, prowlarr.com, wiki.servarr.com, deluge.readthedocs.io, rtorrent-docs.readthedocs.io, libtorrent.org, bittorrent.org, scenerules.org, wikipedia.org, interviewfor.red, redacted.sh, orpheus.network, forums.sabnzbd.org and techsono.com. Everything below was therefore verified against the same material hosted on GitHub (source code, docs repos, wikis). Where a claim could not be verified from a reachable source it is marked **UNVERIFIED**.

---

## 0. Key conclusions

1. **Track-level search does not exist in the Torznab/Newznab ecosystem in practice.** The spec defines `t=music` with `track=` but of 557 Prowlarr Cardigann definitions, 392 advertise `music-search`, 376 of those support only `[q]`, and **zero** declare `track`. Prowlarr's C# Redacted/Orpheus definitions map only `artist`, `album`, `year` (and free-text `q`). The only real track-level search is the Gazelle `filelist=` browse parameter (Redacted/Orpheus and other Gazelle sites), which Prowlarr does not expose; you must call the tracker's own JSON API for it.
2. **Selective single-file download is well supported by every major torrent client API** (qBittorrent `filePrio`, Transmission `files-wanted/unwanted`, Deluge `file_priorities`, rTorrent `f.priority.set`, aria2 `--select-file`). Universal pattern: add stopped/paused -> read file list -> set unwanted files to priority 0 -> start -> wait until the torrent's *finished* state (all wanted pieces) -> import the one file. Magnets require a metadata-fetch step first (qBittorrent has `stopCondition=MetadataReceived` for exactly this; Deluge has `core.prefetch_magnet_metadata`).
3. **Partial downloads never make you a seeder on Gazelle trackers.** libtorrent reports `left` over *all* pieces (skipped files count as "left"), Ocelot files any peer with `left > 0` as a leecher and only records a snatch when `left == 0`. So a one-track grab costs ratio (only the bytes actually fetched, plus neighbouring pieces) and can never earn seeding credit. RED/OPS rule text on partial seeding could not be fetched (blocked) - **UNVERIFIED**.
4. **Usenet cannot do true single-track fetches in the general case.** NZB is per-file XML so you can strip `<file>` elements (or use SABnzbd `delete_nzf` / NZBGet `FileDelete`/`FilePause`), but music posts are commonly RAR-packed and/or obfuscated (hash-named files whose real names live only in par2/RAR headers), so the wanted track is unidentifiable before download. Practical rule: download the whole post, unpack, keep one track; only attempt per-file selection when the NZB's `<file subject>` names contain plausible per-track audio filenames.
5. **Lidarr will not import a partial album from a new download** (`NoMissingOrUnmatchedTracksSpecification` rejects "Has missing tracks"); single-track monitoring was closed "not planned" (issue #826). No existing *arr tool does album-fetch-then-keep-one-track; the new app must own that logic (and matching must be done on file name + size only, since torrents carry no duration/bitrate).

---

## 1. Torznab / Newznab music search

### 1.1 The `t=music` function (spec)

Newznab Web API reference (mirrored inside the Torznab spec repo; rendered at https://torznab.github.io/spec-1.3-draft/external/newznab/api.html, source: https://github.com/torznab/torznab.github.io/blob/master/spec-1.3-draft/external/newznab/api.html), MUSIC-SEARCH section:

> "t=music - Music-Search function, must always be 'music'. apikey=xxx ... Optional Parameters: limit=123 ... album=xxxx Album title (URL/UTF-8 encoded). Case insensitive. artist=xxxx Artist name ... label=xxxx Publisher/Label name ... **track=xxxx Track name (URL/UTF-8 encoded). Case insensitive.** year=xxxx Four digit year of release. genre=123 List of music genre id's to search delimited by ','. See CAPS for available genres. cat=xxx ... o=xml ... attrs=xxx ... extended=1 ... del=1 ... maxage=123 ... offset=50"

Example in the spec: `GET http://servername.com/api?t=music&apikey=xxx&album=Groovy&extended=1`, and the example item carries `newznab:attr` values `artist`, `album`, `publisher`, `year`, `tracks` (value `"track one|track two|track three"`), `coverurl`, `review`, plus `category` 3000/3010 and `size`.

The NNTmux copy of the spec (https://github.com/NNTmux/newznab-tmux/blob/master/docs/newznab_api_specification.txt, section 2.5 MUSIC-SEARCH) lists the same: `t=music (alias t=audio)`, optional `q`, `album, artist, label, track, year, genre`, plus `group, cat, limit, offset, maxage, minsize, maxsize, attrs, extended, del, sort`.

The nZEDb copy (https://github.com/nZEDb/nZEDb/blob/dev/docs/newznab_api_specification.txt) does **not** document a music function at all and its caps example shows `<audio-search available="no" supportedParams=""/>` - i.e. real usenet indexers vary; always read caps.

### 1.2 Caps advertisement

Torznab spec v1.3 (https://github.com/torznab/torznab.github.io/blob/master/spec-1.3-draft/torznab/Specification-v1.3.html): function list includes "music - Search query with music specific query params and filtering." The caps example uses the *audio-search* element name:

```xml
<searching>
   <search available="yes" supportedParams="q" />
   <tv-search available="yes" supportedParams="q,rid,tvdbid,season,ep" />
   <movie-search available="no" supportedParams="q,imdbid,genre" />
   <audio-search available="no" supportedParams="q" />
   <book-search available="no" supportedParams="q" />
</searching>
```

The spec text explains: "clients use the supportedParams list to determine whether efficient 'indexed' queries are available or that the client must use the generic q free text search capability."

Jackett emits **both** `music-search` and `audio-search` elements with identical attributes, with the code comment "inconsistent but apparently already used by various newznab indexers (see #1896)" (https://github.com/Jackett/Jackett/blob/master/src/Jackett.Common/Models/TorznabCapabilities.cs; its `MusicSearchParam` enum is `Q, Album, Artist, Label, Track, Year, Genre`). Prowlarr does the same: `IndexerCapabilities.cs` serialises `<music-search available=... supportedParams=...>` and `<audio-search ...>` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Indexers/IndexerCapabilities.cs). A client should therefore accept either element name.

### 1.3 Prowlarr's implementation of the music params

- `IndexerCapabilities.cs`: `public enum MusicSearchParam { Q, Album, Artist, Label, Year, Genre, Track }`; `MusicSearchAvailable => MusicSearchParams.Count > 0`; `SupportedMusicSearchParams()` always emits `q` then adds `album`, `artist`, `label`, `track`, `year`, `genre` when declared.
- `NewznabController.cs` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Api.V1/Indexers/NewznabController.cs): routes `[HttpGet("{id:int}/api")]` and `[HttpGet("/api/v1/indexer/{id:int}/newznab")]`; `t=caps` returns `indexer.GetCapabilities().ToXml()`; `"search"`, `"tvsearch"`, `"music"`, `"book"`, `"movie"` all call `_releaseSearchService.Search(request, new List<int> { indexerDef.Id }, false)`.
- `ReleaseSearchService.cs` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/IndexerSearch/ReleaseSearchService.cs): `"music" => MusicSearch(...)` which sets `searchSpec.Artist = request.artist; searchSpec.Album = request.album; searchSpec.Label = request.label; searchSpec.Genre = request.genre; searchSpec.Track = request.track; searchSpec.Year = request.year;` (SearchTerm, Categories, Limit, Offset, MinAge, MaxAge, MinSize, MaxSize come from the base `Get<TSpec>`).
- So Prowlarr *accepts* `track=` on `/{id}/api?t=music` and passes it to the indexer definition - but see 1.5: no definition consumes it.

### 1.4 Newznab audio category IDs

Prowlarr `NewznabStandardCategory.cs` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Indexers/NewznabStandardCategory.cs):

| Id | Name |
|---|---|
| 3000 | Audio |
| 3010 | Audio/MP3 |
| 3020 | Audio/Video |
| 3030 | Audio/Audiobook |
| 3040 | Audio/Lossless |
| 3050 | Audio/Other |
| 3060 | Audio/Foreign |

`Audio.SubCategories.AddRange(new List<IndexerCategory> { AudioMP3, AudioVideo, AudioAudiobook, AudioLossless, AudioOther, AudioForeign })`. The Newznab spec's own table (torznab repo copy) lists only 3000, 3010, 3020, 3030, 3040 and states category ranges `3000-3999 Audio`, `9000-99999 Reserved`, `100000- Custom: Site specific category range. Defined in CAPS.` The nZEDb copy lists `3999 Audio/Other` and `3060 Audio/Foreign` - so treat 3050 vs 3999 "Other" as indexer-dependent and always map via caps. Indexer-specific categories (e.g. Redacted "Music" = tracker cat 1) are mapped by Prowlarr onto these standard IDs (`caps.Categories.AddCategoryMapping(1, NewznabStandardCategory.Audio, "Music")` in Redacted.cs/Orpheus.cs).

### 1.5 Which indexers support which music params (empirical, from Prowlarr's definition repo)

Cloned https://github.com/Prowlarr/Indexers (definitions/v1..v11, 557 YAML files) on 2026-09-28:

- 392 definitions declare a `music-search` mode.
- Distribution of declared param sets: `[q]` x376; `[q, artist]` x6 (btetree, losslessclub, opencd, themixingbowl, trancetraffic, tribalmixes); `[q, genre]` x5; `[q, album, artist]` x3 (hqmusic, indietorrents, metaltracker); `[q, year, genre]` x1; `[q, artist, album, genre]` x1 (pandacd).
- **0 definitions declare `track`.**
- Public general trackers: `1337x.yml`, `thepiratebay.yml`, `torrentleech.yml`, `mixtapetorrent.yml` all declare `music-search: [q]` only.
- Music-focused definitions present (description contains music/audio): btetree (public, bootleg FLAC), dimeadozen, hqmusic, indietorrents, jpopsuki, losslessclub, metalguru, metaltracker, mixtapetorrent (public), musopia, nipponsei (public), opencd, pandacd (public), romanianmetaltorrents, themixingbowl, trancetraffic, vc-lib, zappateers, plus many general trackers with music categories (52bt, uindex, noname-club, kinozal, etc.).

C# definitions in Prowlarr core:
- **Redacted** (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Indexers/Definitions/Redacted.cs): `MusicSearchParams = { Q, Artist, Album, Year }`; builds `ajax.php?action=browse&order_by=time&order_way=desc` with `artistname`, `groupname`, `year`, `searchstr`, `filter_cat[n]`; auth via `Authorization: <apikey>` header; download URL `/ajax.php?action=download&id=<id>[&usetoken=1]` with freeleech-token modes Never/Preferred/Required; result mapping sets `Files = torrent.FileCount`, `Size`, `Seeders`, `Peers`, `DownloadVolumeFactor = isFreeLeech ? 0 : 1`, `Guid/InfoUrl` = torrent page.
- **Orpheus** (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Indexers/Definitions/Orpheus.cs): same `{ Q, Artist, Album, Year }` -> `artistname`, `groupname`, `year`, `searchstr`.
- Generic Gazelle base (`GazelleRequestGenerator.cs`): maps Artist->`artistname`, Album->`groupname`, Label->`recordlabel`, free text->`searchstr`; does **not** map Track, Year or Genre and does not use `filelist`.
- **RuTracker** (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Indexers/Definitions/RuTracker.cs): `MusicSearchParams = { Q }` only (1314 category mappings, audio ones map to Audio/AudioAudiobook etc.).

### 1.6 Real track-level search: Gazelle `filelist`

Gazelle (the software behind Redacted, Orpheus and other music trackers) exposes a browse parameter that matches against the file names inside each torrent:

- WhatCD Gazelle JSON API wiki (https://github.com/WhatCD/Gazelle/wiki/JSON-API-Documentation): `ajax.php?action=browse&searchstr=<Search Term>` with params "searchstr, page, taglist, tags_type, order_by, order_way, filter_cat, freetorrent, vanityhouse, scene, haslog, releasetype, media, format, encoding, artistname, **filelist**, groupname, recordlabel, cataloguenumber, year, remastertitle, remasteryear, remasterrecordlabel, remastercataloguenumber". Per-torrent response fields include `torrentId, editionId, artists, remastered, remasterYear, remasterCatalogueNumber, remasterTitle, media, encoding, format, hasLog, logScore, hasCue, scene, vanityHouse, fileCount, time, size, snatches, seeders, leechers, isFreeleech, isNeutralLeech, isPersonalFreeleech, canUseToken`.
- OPSnet Gazelle docs (https://github.com/OPSnet/Gazelle/blob/master/docs/07-API.md) list the same params "as in advanced search", `filelist` included.
- Implementation: `classes/torrentsearch.class.php` (https://github.com/WhatCD/Gazelle/blob/master/classes/torrentsearch.class.php) - `'filelist' => 1` is an allowed field and `search_filelist($Term)` does `$this->SphQL->where_match($SearchString, 'filelist', false)` (Sphinx full-text match on the file list).
- `ajax.php?action=torrent&id=<Torrent Id>` returns `fileList` as `"filename{{{filesize}}}|||nextfile{{{size}}}"`, e.g. `"01-logistics-fear_not.flac{{{38139451}}}|||02-logistics-timelapse.flac{{{39346037}}}"` - i.e. you can get the exact file name and byte size of every track **before** downloading, which is what you need to pick a file index.

Consequence for the design: for Gazelle trackers, bypass Prowlarr for the search step (call `ajax.php?action=browse&filelist=<track title>&artistname=<artist>` with the user's API key, which is what Prowlarr itself does in Redacted.cs) or add a "Gazelle direct" indexer type; keep Prowlarr for everything else.

### 1.7 Prowlarr's native search API (usable by any client with the API key)

- OpenAPI (https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Api.V1/openapi.json): `GET /api/v1/search` query params `query` (string), `type` (string), `indexerIds` (int[]), `categories` (int[]), `limit` (int), `offset` (int). Security schemes: `X-Api-Key` header or `apikey` query parameter. Also declared: `GET /{id}/api`, `GET /api/v1/indexer/{id}/newznab`, `GET /{id}/download`, `GET /api/v1/indexer/{id}/download`.
- `SearchController.cs` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Api.V1/Search/SearchController.cs) builds `new NewznabRequest { q = payload.Query, t = payload.Type, ... limit, offset }` - so `type=music` plus a free-text `query` is a t=music search across the chosen indexers (the structured `artist=`/`album=` fields are only available through the per-indexer `/{id}/api?t=music&artist=...` route). Results are cached 30 minutes (`_remoteReleaseCache.Set(..., TimeSpan.FromMinutes(30))`) and `DownloadUrl` is rewritten to a Prowlarr proxy link.
- `ReleaseResource` fields (https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Api.V1/Search/ReleaseResource.cs): `Guid, Age, AgeHours, AgeMinutes, Size (long), Files (int?), Grabs (int?), IndexerId, Indexer, SubGroup, ReleaseHash, Title, SortTitle, ImdbId, TmdbId, TvdbId, TvMazeId, PublishDate, CommentUrl, DownloadUrl, InfoUrl, PosterUrl, IndexerFlags, Categories, MagnetUrl, InfoHash, Seeders (int?), Leechers (int?), Protocol (DownloadProtocol), FileName, DownloadClientId (int?)`.
- `files` availability: Gazelle definitions set `Files = FileCount`; 197 of 557 Cardigann YAMLs define a `files:` field; 91 define `infohash:`/`magnet:` fields. Treat `files` as optional.
- Grab: `POST /api/v1/search` with `{ "guid": ..., "indexerId": ... }` looks the release up in the 30-minute cache and calls `_downloadService.SendReportToClient(...)`, i.e. it pushes to a download client configured **in Prowlarr** (returns 404 "Couldn't find requested release in cache, try searching again" if expired). For our app it is simpler to fetch `downloadUrl` ourselves: `DownloadMappingService.ConvertToProxyLink` produces `{serverUrl}/{indexerId}/download?apikey={apikey}&link={encrypted}&file={name}` (https://github.com/Prowlarr/Prowlarr/blob/develop/src/NzbDrone.Core/Download/DownloadMappingService.cs) which returns the .torrent/.nzb bytes (or redirects to a magnet).
- Auth (https://github.com/Prowlarr/Prowlarr/blob/develop/src/Prowlarr.Http/Authentication/ApiKeyAuthenticationHandler.cs): key read from the `apikey` query param, then the `X-Api-Key` header, then `Authorization: Bearer`. None of the search endpoints require the caller to be a registered "Application"; Prowlarr's Applications feature only syncs indexers *into* Sonarr/Radarr/Lidarr.
- Torznab attributes worth reading from `/{id}/api` results (Torznab spec attribute table): `size`, `category`, `guid`, `files` ("Number of files in the release"), `grabs`, `seeders`, `leechers`, `peers`, `infohash`, `magneturl`, `seedtype`, `minimumratio`, `minimumseedtime`, `downloadvolumefactor` ("Freeleech would be 0.0"), `uploadvolumefactor`.

---

## 2. Selective file download from a torrent, per client

### 2.1 qBittorrent Web API

Versions (from `src/webui/webapplication.h`): 4.6.7 -> API **2.9.3**; 5.0.0 -> API **2.11.2**; 5.1.0 -> API **2.11.4**; master (Sep 2026) -> API 2.16.2.

Rename in 5.0 (verified in source, not fully reflected in the wiki):
- 4.6.7 `serialize_torrent.cpp` states: `pausedUP`, `pausedDL`; 5.0.0: `stoppedUP`, `stoppedDL` (https://github.com/qbittorrent/qBittorrent/blob/release-5.0.0/src/webui/api/serialize/serialize_torrent.cpp). Full 5.x state list (master): `error, missingFiles, uploading, stoppedUP, queuedUP, stalledUP, checkingUP, forcedUP, downloading, metaDL, forcedMetaDL, stoppedDL, queuedDL, stalledDL, checkingDL, forcedDL, checkingResumeData, moving, unknown`.
- 5.0.0 `torrentscontroller.cpp` has `stopAction()`/`startAction()` (endpoints `/api/v2/torrents/stop` and `/api/v2/torrents/start`) and reads the add parameter `stopped` (`parseBool(params()[u"stopped"_s])`); 4.6.x used `/torrents/pause`, `/torrents/resume` and `paused`. The 5.0 wiki page (https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-5.0)) still shows `paused` and `pausedUP` in places, and its state filter list already says `stopped` - so send both `paused` and `stopped` on add and accept both state spellings.

Add (`POST /api/v2/torrents/add`, multipart or form): `urls` (newline separated, magnets ok), `torrents` (raw .torrent, repeatable), `savepath`, `category`, `tags`, `skip_checking`, `paused`/`stopped`, `root_folder` (legacy), `rename`, `autoTMM`, `sequentialDownload`, `firstLastPiecePrio` (wiki), plus from source: `contentLayout` (`Original | Subfolder | NoSubfolder`, https://github.com/qbittorrent/qBittorrent/blob/master/src/base/bittorrent/torrentcontentlayout.h), `stopCondition` (`None | MetadataReceived | FilesChecked`, https://github.com/qbittorrent/qBittorrent/blob/master/src/base/bittorrent/torrent.h; present in 4.6.7 and 5.0.0 controllers), `addToTopOfQueue`, `inactiveSeedingTimeLimit`, and in master only a `filePriorities` comma list applied at add time (not in 5.0.0/5.1.0 - feature-detect).

File list: `GET /api/v2/torrents/files?hash=<hash>[&indexes=0|3|7]` -> array of `{ index (since 2.8.2), name (relative path), size (bytes), progress (0..1), priority, is_seed, piece_range [first,last], availability }`. Priority values: `0` Do not download, `1` Normal, `6` High, `7` Maximal.

Set priorities: `POST /api/v2/torrents/filePrio` with `hash`, `id` (file ids separated by `|`), `priority`.

Torrent list fields for completion (`GET /api/v2/torrents/info?hashes=`): `state`, `progress` (0..1), `amount_left` (bytes), `completed` (bytes), `size` = "Total size (bytes) of files selected for download", `total_size` = "Total size (bytes) of all file in this torrent (including unselected ones)", `content_path`, `save_path`, `category`. Generic properties (`/torrents/properties`): `completion_date`, `total_wasted`, `pieces_have`, `pieces_num`.

Completion semantics: qBittorrent's `isFinished()` is `state == lt::torrent_status::finished || state == seeding` (https://github.com/qbittorrent/qBittorrent/blob/master/src/base/bittorrent/torrentimpl.cpp), and libtorrent's *finished* means all **wanted** pieces are done. So once the selected file is complete the torrent flips to an `*UP` state (`uploading`/`stalledUP`/`stoppedUP`) and `progress` reaches 1.0 even though other files were skipped. Per-file confirmation: `files` -> `progress == 1` / `is_seed == true` for the wanted index.

Recipe (qBittorrent):
1. `torrents/add` with `stopped=true&paused=true`, `category=<app>`, `savepath=...`, `contentLayout=Original`; for magnets add `stopCondition=MetadataReceived` instead (torrent starts, fetches metadata, then stops itself).
2. Poll `torrents/info` until the hash appears and (magnet) state leaves `metaDL`; then `torrents/files?hash=`.
3. Choose the file index by name/size; `filePrio` all other ids to `0`, wanted to `7`.
4. `torrents/start?hashes=`.
5. Poll until `state` in {`uploading`,`stalledUP`,`queuedUP`,`forcedUP`,`stoppedUP`,`pausedUP`} or file `progress==1`; import `save_path/name`.

### 2.2 Transmission RPC

Spec: https://github.com/transmission/transmission/blob/main/docs/rpc-spec.md.
- `torrent-add` arguments: `filename` ("filename or URL of the .torrent file" - magnets go here), `metainfo` ("base64-encoded .torrent content"), `download-dir`, `paused` ("if true, don't start the torrent"), `files-wanted` / `files-unwanted` (arrays of file indices), `priority-high` / `priority-low` / `priority-normal`, `labels`, `bandwidthPriority`. "Either filename or metainfo must be included." Response: `torrent-added` (or `torrent-duplicate`) with `id`, `name`, `hashString`.
- `torrent-set` also takes `files-wanted` / `files-unwanted` (empty array = all files) and the priority arrays.
- `torrent-get` fields: `files[]` (`name`, `length`, `bytesCompleted`, `beginPiece`, `endPiece`), `fileStats[]` (`bytesCompleted`, `wanted`, `priority`), `wanted[]`, `priorities[]`, `percentDone`, `percentComplete`, `metadataPercentComplete`, `status` (0 stopped, 1 check queued, 2 checking, 3 download queued, 4 downloading, 5 seed queued, 6 seeding), `isFinished`, `leftUntilDone`, `sizeWhenDone`, `haveValid`, `magnetLink`, `downloadDir`.
- Magnet caveat (from source): `rpcimpl.cc` applies `files-unwanted`/`files-wanted` to the *constructor* (`ctor.set_files_wanted(...)`), and `tr_torrent::init` applies them via `ctor.init_torrent_wanted(*this)` right after `on_metainfo_updated()` which sizes `files_wanted_` from the metainfo (https://github.com/transmission/transmission/blob/main/libtransmission/torrent.cc). When metadata later arrives for a magnet (`tr_torrent::set_metainfo` -> `on_metainfo_updated()`), `files_wanted_` is rebuilt fresh and the ctor is gone - i.e. **file selection passed at add time is not honoured for magnets**; you must wait for `metadataPercentComplete == 1` and then call `torrent-set` with `files-unwanted`. Because a magnet added with `paused: true` does not fetch metadata (Transmission ticket #4808 "Make it possible to choose what files to download after getting magnet metadata" - only the search snippet was reachable, **UNVERIFIED** detail), the practical sequence is: add magnet *started*, poll `metadataPercentComplete`, immediately `torrent-set files-unwanted=[all others]` (accepting that a few pieces of other files may already be in flight), or better: resolve the magnet to a .torrent elsewhere (Prowlarr proxy download, or the tracker's download link) and use `metainfo` + `files-unwanted` in the single `torrent-add` call, which is fully atomic.
- Completion: `isFinished`/`leftUntilDone == 0`/`status 5|6`; per file `fileStats[i].bytesCompleted == files[i].length`. Transmission's `leftUntilDone` only counts wanted files (torrent.cc sums `bytes_left` over files where `files_wanted_.file_wanted(i)`).

### 2.3 Deluge (daemon RPC and deluge-web JSON)

- `core.add_torrent_file(filename, filedump, options)` - "filedump: A base64 encoded string of the torrent file contents" -> torrent_id; `core.add_torrent_magnet(uri, options)`; `core.set_torrent_options(torrent_ids, options)`; `core.set_torrent_file_priorities(torrent_id, priorities)` is **deprecated**: "Use set_torrent_options with 'file_priorities'"; `core.get_torrent_status(torrent_id, keys, diff=False)`; `core.pause_torrent`, `core.resume_torrent`; `core.prefetch_magnet_metadata(magnet, timeout=30)` - "Download magnet metadata without adding to Deluge session. Used by UIs to get magnet files for selection before adding to session ... Returns (torrent_id, metadata)" (https://github.com/deluge-torrent/deluge/blob/develop/deluge/core/core.py).
- `TorrentOptions` (https://github.com/deluge-torrent/deluge/blob/develop/deluge/core/torrent.py): "file_priorities (list of int): The priority for files in torrent, range is [0..7] however only [0, 1, 4, 7] are normally used and correspond to [Skip, Low, Normal, High]"; also `add_paused`, `download_location`, `sequential_download`, `prioritize_first_last_pieces`, `name`, `mapped_files`. `set_file_priorities` returns early `if not self.has_metadata` (priorities on a magnet without metadata are ignored). Status keys: `files`, `file_priorities`, `file_progress`, `is_finished`, `state`, `progress`.
- deluge-web: `POST /json` with `{"method": "...", "params": [...], "id": n}`; methods `auth.login`, `web.connect`, `web.get_torrent_info(filename)` -> `{name, files_tree, info_hash}`, `web.get_magnet_info(uri)`, `web.add_torrents([{path, options}])` (options may include `file_priorities`), `web.download_torrent_from_url(url, cookie)`, `web.get_torrent_files(torrent_id)`, `web.update_ui(keys, filter_dict)`, `web.get_torrent_status(torrent_id, keys)` (https://github.com/deluge-torrent/deluge/blob/develop/deluge/ui/web/json_api.py).
- Recipe: `.torrent`: `core.add_torrent_file(name, b64, {"add_paused": true, "download_location": ..., "file_priorities": [0,0,4,0...]})` then `core.resume_torrent`; magnet: `core.prefetch_magnet_metadata` -> build priorities from the returned metadata -> `core.add_torrent_file` with the fetched metadata, or add magnet paused, poll `has_metadata`/`files`, then `set_torrent_options({"file_priorities": [...]})`.

### 2.4 rTorrent XML-RPC (and ruTorrent)

rTorrent command reference (https://github.com/rtorrent-community/rtorrent-docs/blob/master/docs/include-cmd-items.rst, rendered at rtorrent-docs.readthedocs.io):
- `load.raw = <binary metafile>, [cmd...]` / `load.raw_start` - "Load a metafile passed as base64 data ... As with load.normal, raw loads them stopped"; `load.start` variants start immediately. Per-item commands can be appended to the load call (e.g. `d.directory.set=...`, `d.custom1.set=<label>`).
- `f.priority` / `f.priority.set = <infohash>:f<index>, <priority>`: "There are 3 possible priorities for files: 0 off - Do not download this file. **Note that the file can still show up if there is an overlapping chunk with a file that you do want to download.** 1 normal - Download this file normally. 2 high - Prioritize requesting chunks for this file above normal files." "They can also be called directly, but you need to pass <infohash>:f<index> as the first argument. Index counting starts at 0, the array size is d.size_files."
- `d.update_priorities = <hash>`: "After a scripted change to priorities using f.priority.set, this command **must** be called."
- `f.multicall = <infohash>, <pattern>, f.path=, f.size_bytes=, f.completed_chunks=, f.size_chunks=, f.priority=` to list files with sizes/progress; `d.start`/`d.stop`; `d.complete` ("Indicates whether an item is complete (100% done)") - note libtorrent-rakshasa defines `is_done()` as `completed_chunks() == size_chunks()` over **all** chunks (https://github.com/rakshasa/libtorrent/blob/master/src/torrent/data/file_list.h), so with skipped files use `d.wanted_chunks` ("will not count chunks from files prioritized as 'off'") vs `d.completed_chunks`, or per-file `f.completed_chunks == f.size_chunks`, to detect completion.
- ruTorrent is just a web front-end over this XML-RPC (its own HTTP RPC is `plugins/httprpc/action.php` proxying the same commands) - not separately verified.

### 2.5 aria2 (and others)

aria2 manual (https://github.com/aria2/aria2/blob/master/doc/manual-src/en/aria2c.rst): `--select-file=INDEX...` "Set file to download by specifying its index. You can find the file index using the --show-files option." Critical note: "**In multi file torrent, the adjacent files specified by this option may also be downloaded. This is by design, not a bug. A single piece may include several files or part of files, and aria2 writes the piece to the appropriate files.**" Also `--bt-metadata-only`, `--bt-save-metadata` ("Save metadata as .torrent file. This option has effect only when BitTorrent Magnet URI is used"), RPC `aria2.addTorrent(base64)`, `aria2.getFiles(gid)`, `aria2.changeOption(gid, {"select-file": ...})`, `aria2.tellStatus`. Flood (web UI over rTorrent/qBittorrent/Transmission) inherits the underlying client's behaviour - not separately verified.

### 2.6 The magnet problem

File list is unknown until the info-dictionary is fetched from peers/DHT. Per client:
- qBittorrent: add with `stopCondition=MetadataReceived` (then `torrents/files`, `filePrio`, `start`); or add running and poll `state != metaDL` while accepting some initial piece downloads.
- Transmission: metadata is not fetched while paused (**UNVERIFIED**, see 2.2), and add-time `files-unwanted` is not applied to magnets (verified in source); poll `metadataPercentComplete` then `torrent-set`.
- Deluge: `core.prefetch_magnet_metadata` gives you the metadata before adding; `set_file_priorities` is a no-op until `has_metadata`.
- rTorrent: magnets are supported via `load.start` of the `magnet:` URI (creates a `.meta` file); priorities can only be applied after metadata - poll `d.size_files > 0` (**UNVERIFIED** detail beyond the docs' TODO entries).
- Best practice for this app: prefer indexers that return a `.torrent` (Prowlarr proxies `downloadUrl` -> bytes) or an `infohash` you can resolve via a metadata-fetch helper (aria2 `--bt-metadata-only --bt-save-metadata`, Deluge prefetch), so selection is applied atomically at add time.

### 2.7 Piece alignment: you will get bits of neighbouring files

- rTorrent docs (2.4) and aria2 (2.5) both document that skipped files still get partially written when they share a piece with a wanted file.
- libtorrent (`torrent_handle.hpp`, https://github.com/arvidn/libtorrent/blob/RC_2_0/include/libtorrent/torrent_handle.hpp): "Whenever a file priority is changed, all other piece priorities are reset to match the file priorities" and "If a file has its priority set to 0 *after* it has already been created, it will not be moved into the partfile." Priorities: `dont_download{0}`, `low_priority{1}`, `default_priority{4}`, `top_priority{7}` (download_priority.hpp). libtorrent keeps the overlap bytes of priority-0 files in a `.parts` part-file rather than creating the neighbour file, which is why qBittorrent shows `total_wasted`/`size` vs `total_size`.
- BEP 47 padding files (https://github.com/bittorrent/bittorrent.org/blob/master/beps/bep_0047.rst): "synthetic files inserted into the file list to let the following file start at a piece boundary"; `attr` contains `p`; clients "don't need to write the padding files to disk and should also avoid requesting byte-ranges covering their contents". v2/hybrid torrents (and many modern v1 torrents from Gazelle uploads made with recent clients) are piece-aligned, so the overhead is zero; legacy v1 torrents cost at most 2 extra pieces (one on each side of the wanted file).
- Implication: expect the import step to find only the wanted file complete; other files may exist as 0-byte placeholders or not at all (part-file). Import by file index/name, never by "everything in the folder".

### 2.8 Ratio, seeding and H&R implications of partial downloads on private trackers

Verified mechanics:
- BEP 3 (https://github.com/bittorrent/bittorrent.org/blob/master/beps/bep_0003.rst): `left` is "The number of bytes this peer still has to download".
- libtorrent computes the announced `left` from **all** pieces regardless of file priority: `req.left = value_or(bytes_left(), 16*1024)` and `bytes_left() = total_size - have_pieces * piece_length ...` (https://github.com/arvidn/libtorrent/blob/RC_2_0/src/torrent.cpp). Thus a torrent with skipped files always announces `left > 0`.
- libtorrent implements BEP 21 partial seeds: `if (e == event_t::none && is_finished() && !is_seed()) e = event_t::paused;` - every announce while "finished but not seed" carries `event=paused`. BEP 21 (https://github.com/bittorrent/bittorrent.org/blob/master/beps/bep_0021.rst): "A partial seed is a peer that is incomplete without downloading anything more. This happens for multi file torrents where users only download some of the files ... it MUST send an event=paused parameter in every announce while it is a partial seed."
- Ocelot, Gazelle's tracker (https://github.com/WhatCD/Ocelot/blob/master/worker.cpp): `if (left > 0) { peer_it = tor.leechers.find(peer_key); ... }` - any peer with `left > 0` is stored in the leecher list; `completed_torrent = (left == 0)` on `event=completed`; and `peer_is_visible` returns `(p->left == 0 || u->can_leech())`. Ocelot ignores `event=paused`, so partial seeds are plain leechers.
- Consequences: (a) the tracker still counts every byte you download toward your ratio (`downloaded` param), (b) you are never recorded as a snatcher/seeder of that torrent, so no seeding bonus/seed-time credit is possible, (c) you can upload to other leechers the pieces you have, which does count as upload, (d) on trackers with explicit hit-and-run rules (RED and OPS are generally described as ratio-based without H&R timers - **UNVERIFIED**, rules pages blocked), a partial download is typically not a "snatch" at all; on trackers that enforce H&R from the leech list this could look like an abandoned download. **RED/OPS wording on partial seeding and whether it is penalised could not be retrieved (redacted.sh, orpheus.network and interviewfor.red are blocked).** Design should surface per-indexer policy as a user setting (allow partial / require full download / prefer freeleech; Prowlarr's Redacted definition can force `usetoken=1` for freeleech tokens).

---

## 3. Usenet

### 3.1 NZB format

NZB spec (Newzbin, mirrored at https://github.com/sabnzbd/sabnzbd.github.io/blob/master/wiki/extra/nzb-spec.html, rendered https://sabnzbd.org/wiki/extra/nzb-spec): `<nzb>` -> `<head><meta type="title|tag|category|password">` -> `<file poster= date= subject=>` -> `<groups><group>` + `<segments><segment bytes= number=>MessageID</segment>`. "`<file>` Represents a list of messageids that make up a file"; "subject - A slightly munged copy of the article's subject. The segment counter (xx/yy) usually found at the end, is replaced with (1/yy). You can use the yy to confirm all segments are present."; "Since this is an XML format, clients are encouraged to use an XML parser to process it". The only file-name information in an NZB is whatever the poster put in the subject; there is no per-file size except the sum of `segment bytes` (yEnc-encoded article sizes, ~ +2-3%).

So an NZB **can** be trimmed to the wanted `<file>` elements before submission (it is just XML), provided you can tell which `<file>` is the track.

### 3.2 SABnzbd API (docs: https://github.com/sabnzbd/sabnzbd.github.io/blob/master/wiki/configuration/5.2/api.html, rendered https://sabnzbd.org/wiki/configuration/5.2/api)

- Add: `api?mode=addurl&name=<url>&nzbname=&cat=*&script=Default&priority=-100&pp=-1` -> `{"status": true, "nzo_ids": ["SABnzbd_nzo_kyt1f0"]}`; `mode=addfile`: "Upload NZB using POST multipart/form-data. In your form, set the value of the field mode to addfile; the file data should be in the field name or the field nzbfile"; `mode=addlocalfile&name=<path>`. Params: `nzbname`, `password`, `cat` (`*` = Default; list via `mode=get_cats`), `script`, `priority`: `-100` Default (of category), `-2` Paused, `-1` Low, `0` Normal, `1` High, `2` Force; `pp`: `-1` Default, `0` None, `1` +Repair, `2` +Repair/Unpack, `3` +Repair/Unpack/Delete.
- Queue: `mode=queue&start=&limit=&cat=&priority=&search=&nzo_ids=` (statuses `Downloading, Queued, Paused, Propagating, Fetching`); job actions `mode=queue&name=pause|resume|delete(&del_files=1)|purge|priority|change_cat|change_script|change_opts|rename&value=NZO_ID`.
- **Per-file**: `mode=get_files&value=NZO_ID` -> `{"files": [{"status": "finished|active|queued", "mbleft", "mb", "age", "bytes", "filename", "nzf_id"}]}` ("The status indicates if a file was finished, in the process of being downloaded (active) or will only be downloaded when necessary (queued, like .par2 files)"); `mode=queue&name=delete_nzf&value=NZO_ID&value2=NZF_ID,NZF_ID2` "Remove file(s) using nzo_id of the job and nzf_id of the file(s). Returns the nzf_ids of removed file."; `move_nzf_bulk` to reorder. Note the doc's own example job has obfuscated names: `93a4ec7c37752640deab48dabb46b164.par2`, `...01`.
- History: `mode=history&start=&limit=&cat=&search=&nzo_ids=&failed_only=0`; statuses `Completed, Failed, Queued, QuickCheck, Verifying, Repairing, Fetching, Extracting, Moving, Running`; slot fields include `nzo_id, status, storage (final folder), path, fail_message, stage_log, completed, category, bytes/size, pp ("D"/"R"/"U"), nzb_name, download_time`. History actions: `retry`, `retry_all`, `delete`, `mark_as_completed`.
- Relevant switches (https://github.com/sabnzbd/sabnzbd.github.io/blob/master/wiki/configuration/5.2/switches.html): "Post-Process only verified jobs - Only try to unpack jobs that passed the verification stage. If turned off, all jobs will be marked as Completed and moved to the Complete folder even if they are incomplete."; "Abort jobs that cannot be completed - If on, when it becomes clear during download that a job can never be repaired, it will be aborted and sent to the History as failed."; "Enable SFV-based checks - If no par2 files are available, use .sfv files (if present) to verify files."; "Deobfuscate final filenames - If filenames of (large) files in the final folder look obfuscated or meaningless (like 19399393.ext or timmof.mkv) they will be renamed to the job name." Special settings: `enable_par_cleanup` ("Disabling this will also force all par2 files to be downloaded"), `process_unpacked_par2`, `quick_check_ext_ignore`. SABnzbd's deobfuscation module (https://github.com/sabnzbd/sabnzbd/blob/develop/sabnzbd/deobfuscate_filenames.py): "Will check in the completed job folder if maybe there are par2 files, for example 'rename.par2', and use those to rename the files."
- Behaviour when you strip par2 files from the NZB / delete them via `delete_nzf`: no verification is possible; with "Post-Process only verified jobs" **on** the job will not be unpacked; with it **off** SABnzbd unpacks/moves anyway. With `pp=0` nothing is repaired or unpacked (you get raw files - fine for plain audio files, useless for RAR sets). There is no "download only selected files" option other than deleting the unwanted `nzf_id`s after adding paused (`priority=-2`), then resuming.

### 3.3 NZBGet JSON-RPC / XML-RPC (docs: https://github.com/nzbgetcom/nzbget/tree/develop/docs/api, rendered at nzbget.com/documentation/api/)

- `append(Filename, Content, Category, Priority, AddToTop, AddPaused, DupeKey, DupeScore, DupeMode, AutoCategory, PPParameters)` -> `NZBID` (>0) - "Content" is base64 NZB or a URL; priorities `-100 very low, -50 low, 0 normal, 50 high, 100 very high, 900 force` (APPEND.md).
- `listgroups(NumberOfLogEntries)` fields include `NZBID, NZBName, NZBFilename, Kind, URL, DestDir, FinalDir, Category, FileSizeLo/Hi/MB, RemainingSizeLo/Hi/MB, PausedSizeLo/Hi/MB, FileCount, RemainingFileCount, RemainingParCount, MaxPriority, ActiveDownloads, Status (QUEUED, PAUSED, DOWNLOADING, FETCHING, PP_QUEUED, LOADING_PARS, VERIFYING_SOURCES, REPAIRING, VERIFYING_REPAIRED, RENAMING, UNPACKING, MOVING, POST_UNPACK_RENAMING, POST_DOWNLOAD_RENAMING, EXECUTING_SCRIPT, PP_FINISHED), Health, CriticalHealth, DupeKey/Score/Mode, Parameters` (LISTGROUPS.md).
- `listfiles(0, 0, NZBID)` -> per file `ID, NZBID, Subject, Filename ("Filename parsed from subject. It could be incorrect since the subject not always correct formated. After the first article for file is read, the correct filename is read from article body."), FilenameConfirmed (bool), DestDir, FileSizeLo/Hi, RemainingSizeLo/Hi, Paused, PostTime, ActiveDownloads, Progress (0..1000)` (LISTFILES.md).
- `editqueue(Command, Param, IDs)` (v18+ signature) commands include **`FilePause`, `FileResume`, `FileDelete`, `FilePauseAllPars`, `FilePauseExtraPars`, `FileReorder`, `FileSplit`**, `GroupPause/Resume/Delete/FinalDelete/SetCategory/SetPriority/SetName`, `HistoryDelete/FinalDelete/Return/Process/Redownload` etc. "File-commands (FileXXX) need ID of file. All other commands need NZBID." (EDITQUEUE.md).
- `history(Hidden)` items carry `Status` such as `SUCCESS/ALL`, `FAILURE/UNPACK`, `ParStatus`, `UnpackStatus`, `MoveStatus`, `ScriptStatus`, `FinalDir/DestDir` (HISTORY.md).
- Config (https://github.com/nzbgetcom/nzbget/blob/develop/nzbget.conf): `ParCheck=auto|always|force|manual` ("Manual - par-check is skipped. One par2-file is always downloaded ..."), `ParScan`, `ParRename=yes` ("restores original file names using information stored in par2-files"), `RarRename=yes` ("restores original file names using information stored in rar-files ... useful for downloads not having par2-files"), `DirectRename` ("rename files during downloading ... works only for healthy downloads"). The `POST_DOWNLOAD_RENAMING` status is literally "renaming excessively obfuscated downloaded non-archive files".
- Per-file selection recipe: `append(..., AddPaused=true)` -> `listfiles(0,0,NZBID)` -> `editqueue("FileDelete" or "FilePause", "", [ids of unwanted files])` (plus `FilePauseExtraPars`) -> `editqueue("GroupResume", "", [NZBID])`.

### 3.4 Obfuscation and RAR packing (why per-file selection often fails)

- Posting tools obfuscate on purpose: ngPost (https://github.com/mbruel/ngPost) offers "full obfuscation of the Article Header: the Subject will be a UUID (as the msg-id) and a random Poster will be used", `--gen_name` "generate random RAR name", `-x/--obfuscate` "obfuscate the subjects of the articles ... CAREFUL you won't find your post if you lose the nzb file".
- Both major clients therefore ship par2/rar-based renaming (SABnzbd deobfuscate module, NZBGet `ParRename`/`RarRename`/`POST_DOWNLOAD_RENAMING`), and NZBGet documents that the subject-derived `Filename` is unreliable until the first article body is read (`FilenameConfirmed`).
- For an obfuscated post the NZB gives you N files named like `93a4ec7c...par2`, `....01`; which one is "track 07" is unknowable before download. For a RAR set the tracks are inside the archive volumes, so every volume is needed anyway (multi-volume RAR cannot be partially extracted without all volumes containing the entry; in practice you need the whole set to reach a given member). **Prevalence of RAR-packing for music on usenet could not be quantified from a reachable source** (the SABnzbd forum thread "The recent problem of obfuscated filenames in rar sets" and the Techsono FAQ were blocked) - treat "whole album" as the default assumption and per-file as an opportunistic optimisation.
- Heuristic for opportunistic per-file selection: parse each `<file subject>` for a quoted filename (`"05 - Song.flac"` / `"Artist - 05 - Song.mp3"`) with an audio extension and a plausible size (sum of `segment bytes`); if every audio file resolves and there are no `.rar/.r00/.7z/.zip/.001` files, keep only the wanted `<file>` plus (optionally) `.par2` files, submit with `pp=0` (SABnzbd) / `ParCheck=manual` or `FilePauseAllPars` (NZBGet). Otherwise submit the full NZB with normal post-processing and extract one track after unpack.

---

## 4. Practical single-track strategy and prior art

### 4.1 What existing tools do

- **Lidarr is album-centric.** Servarr wiki FAQ (https://github.com/Servarr/Wiki/blob/master/lidarr/faq.md): "Lidarr filters what to track per artist through a Metadata Profile ... The default profile includes only Studio albums"; "A single isn't a 'short album.' Singles are their own release-group type in MusicBrainz." Import troubleshooting (https://github.com/Servarr/Wiki/blob/master/lidarr/import-troubleshooting.md): "Single tracks, partial releases, streaming-service rips, and per-track purchases are common plugin outputs that the core matcher wasn't designed to handle ... Manual import is the expected path for these cases". Code: `NoMissingOrUnmatchedTracksSpecification` rejects a new download that "is missing tracks" (`Decision.Reject("Has missing tracks")`) or has unmatched extra files (https://github.com/Lidarr/Lidarr/blob/develop/src/NzbDrone.Core/MediaFiles/TrackImport/Specifications/NoMissingOrUnmatchedTracksSpecification.cs); `MoreTracksSpecification` rejects releases with fewer tracks than the existing one. Lidarr grabs whole-album releases and validates the release size against the album's total duration ("Release Rejected: Album duration is 0, unable to validate size until it's available", troubleshooting.md). Feature request "Download/monitor single songs?" (https://github.com/Lidarr/Lidarr/issues/826, opened 2019-05-31) is closed as not planned/duplicate.
- **Headphones** (https://github.com/rembo10/headphones): "an automated music downloader for NZB and Torrent" supporting SABnzbd, NZBGet, Transmission, uTorrent, Deluge, Blackhole - album-based (the README does not describe per-track download).
- **Soularr** (https://github.com/mrusse/soularr): "reads all of your 'wanted' albums/artists from Lidarr and downloads them using Slskd"; its config is album-level (`album_prepend_artist`, `use_most_common_tracknum`, `minimum_filename_match_ratio`, `allowed_filetypes = flac 24/192,flac 16/44.1,flac,mp3 320,mp3`); no torrent/usenet involvement.
- **Tubifarry** (https://github.com/TypNull/Tubifarry): "a plugin for Lidarr that adds multiple music sources" (YouTube, Slskd/Soulseek, Spotify catalog as indexer, Lucida/DABmusic/T2Tunes/Subsonic web clients) - still bound by Lidarr's album import rules above.
- **beets** has no downloader; nothing found that does "fetch album via torrent/usenet, then keep one track" as a product feature. **No prior art located** - this is new logic.

### 4.2 Recommended pipeline for this app

1. **Search**: Soulseek/YouTube first. For torrents: Prowlarr `GET /api/v1/search?type=music&query=<artist> <album|track>&categories=3000,3010,3040` (plus per-indexer `/{id}/api?t=music&artist=&album=` when caps list `artist`/`album`), and Gazelle-direct `filelist=<track>&artistname=<artist>` where the user has a Gazelle API key. For usenet: same Prowlarr call with usenet indexers (`protocol == "usenet"`), expecting whole-album NZBs.
2. **Release ranking**: parse quality from the title (section 5), prefer `files` small (singles/EPs) or Gazelle `fileCount`, prefer freeleech (`downloadvolumefactor == 0`), high seeders, `.torrent` over magnet, and (usenet) age/grabs.
3. **Torrent path**: download `.torrent` bytes via Prowlarr proxy URL -> parse info dict locally (bencode) to get file names/sizes **before** touching the client -> if a file matches the wanted recording (title fuzzy match + track number + size within the expected bitrate*duration window), add to client with only that index wanted (2.1-2.5) -> poll finished -> import the one file -> apply seeding policy (keep partial-seeding, or remove).
4. **Usenet path**: trimmed-NZB opportunistic mode (3.4) else whole-album download with normal pp -> after `Completed`/`SUCCESS`, scan `storage`/`FinalDir` for audio files -> pick by tags (`TIT2`/`TITLE`, track number) or filename -> import one -> delete the rest (or keep as a cache for future single-song requests from the same album).
5. **Matching without duration/bitrate**: torrents only give name+size; usenet only subject+approx size. Use the MusicBrainz recording length to derive an expected byte-size window per quality tier (bitrate x duration; Lidarr's `QualityDefinition` `MinSize`/`MaxSize`/`PreferredSize` values are the per-quality size budgets it uses for the same check, with `MaxSize = null` for lossless), pick the file whose name fuzzy-matches the title/track number and whose size falls in the window, then verify after download with real tag/duration inspection (ffprobe/mutagen); reject and retry the next release if the file doesn't match.

### 4.3 Cost/benefit: whole-album-then-extract vs selective file

| | Selective file (torrent) | Whole album (torrent) | Whole post (usenet) |
|---|---|---|---|
| Bytes | ~1 track + <=2 boundary pieces | full album | full album (+par2 overhead) |
| Ratio cost (private) | minimal, but never counted as seeder/snatch (Ocelot `left>0`) | full, but you can seed to ratio | n/a (usenet has no ratio) |
| Seeding credit | none (partial seed = leecher) | normal | n/a |
| Pre-download identification | needs file names (torrent metadata / Gazelle `fileList`); magnets need metadata fetch | none needed | obfuscated posts: impossible; plain posts: subject parsing |
| Post-processing | import 1 file | pick 1 of N files, optionally cache the rest | par2 repair + unrar, then pick 1 of N |
| Client support | qBt/Transmission/Deluge/rTorrent/aria2 all support | all | SABnzbd `delete_nzf`, NZBGet `FileDelete`/`FilePause` only for plain posts |
| Failure modes | wrong file chosen (name-only matching); tracker policy on partial downloads (**UNVERIFIED**) | disk/time; H&R obligations for full snatch | RAR sets; missing par2 if trimmed |

Recommendation: implement selective-file as the default for torrents when the release is a `.torrent` or Gazelle result (names known), fall back to whole-album for magnets without metadata and for all usenet posts; keep an "album cache" so a second song from the same album is free.

---

## 5. Torrent/usenet metadata and release-name parsing

### 5.1 What a torrent tells you

Torrent info-dict / client file APIs expose only **path/name and byte length** per file (qBittorrent `files` -> `name`, `size`; Transmission `files` -> `name`, `length`; Deluge `files`; rTorrent `f.path`, `f.size_bytes`) plus piece ranges. No duration, bitrate, tags or checksums of audio content. Gazelle's `fileList` gives the same name+size pre-download. Everything else (format, bitrate, source) must be inferred from the **release title** or the tracker's structured fields (`format`, `encoding`, `media`, `hasLog`, `hasCue` in Gazelle browse results).

### 5.2 Naming conventions

Scene rules (scenerules.org) were not reachable; the following is taken from Lidarr's parser comments/tests, which mirror real-world names (https://github.com/Lidarr/Lidarr/blob/develop/src/NzbDrone.Core/Parser/Parser.cs and https://github.com/Lidarr/Lidarr/blob/develop/src/NzbDrone.Core.Test/ParserTests/QualityParserFixture.cs):
- Scene-style: `Artist-Album-Source-Year-GROUP` ("ex. Dani_Sbert-Togheter-WEB-2017-FURY"), `Artist-Album-Version-Source-Year-GROUP` ("ex. Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM"), e.g. `Migos-No_Label_II-CD-FLAC-2014-FORSAKEN`, `Kid_Cudi-Entergalactic-WEBFLAC-2022-NACHOS`, `Kid_Cudi-Entergalactic-24BIT-WEBFLAC-2022-NACHOS`, `Foghat-Foghat_Live-24-192-WEB-FLAC-REMASTERED-2016-OBZEN`, `Zeynep_Erbay-Flashlights_On_Love-WEB-2022-BABAS`. Lidarr's regexes: `^(?<artist>.+?)[-](?<album>.+?)[-](?<source>\d?CD|WEB).+?(?<releaseyear>\d{4})` and the Version variant.
- P2P-style: `Artist - Album (Year) [FLAC]`, `Artist - Album (2017) MP3 192kbps`, `Sia - This Is Acting (Standard Edition) [2016-Web-MP3-V0(VBR)]`, `Linkin Park - Studio Collection 2000-2012 (2013) [WEB FLAC24-44.1]`, `[TR24][OF] Good Charlotte - Generation Rx - 2018 (Pop-Punk | Alternative Rock)`.
- RuTracker style: `(Genre) [Source]? Artist - Album - Year` and `Artist - Discography 1990-2010`.
- Lidarr's `ReportAlbumTitleRegex` list (in order): ruTracker discography; Artist - Discography (two years / end year); Artist Discography; ruTracker Artist - Album - Year; Artist-Album-Version-Source-Year; Artist-Album-Source-Year; Artist - Album (Year) strict; Artist - Album (Year); Artist - Album - Year [x]; Artist - Album [x]/(x); Artist - Album Year; hyphen-no-space variants; Artist - Year - Album.

### 5.3 Lidarr quality detection (QualityParser.cs, https://github.com/Lidarr/Lidarr/blob/develop/src/NzbDrone.Core/Parser/QualityParser.cs)

- `CodecRegex` (IgnoreCase): `\b(?:(?<MP1>MPEG Version \d(.5)? Audio, Layer 1|MP1)|(?<MP2>MPEG Version \d(.5)? Audio, Layer 2|MP2)|(?<MP3VBR>MP3.*VBR|MPEG Version \d(.5)? Audio, Layer 3 vbr)|(?<MP3CBR>MP3|MPEG Version \d(.5)? Audio, Layer 3)|(?<FLAC>(web)?flac(?:24(?:[-._ ]?bit)?)?|TR24)|(?<WAVPACK>wavpack|wv)|(?<ALAC>alac)|(?<WMA>WMA\d?)|(?<WAV>WAV|PCM)|(?<AAC>M4A|M4P|M4B|AAC|mp4a|MPEG-4 Audio(?!.*alac))|(?<OGG>OGG|OGA|Vorbis))\b|(?<APE>monkey's audio|[\[|\(].*\bape\b.*[\]|\)])|(?<OPUS>Opus Version \d(.5)? Audio|[\[|\(].*\bopus\b.*[\]|\)])`
- `BitRateRegex`: groups `B096|B128|B160(q5)|B192(q6)|B224(q7)|B256(itunes plus, q8)|B320(q9)|B500(q10)|VBRV0|VBRV2` matching `NNN kbps`, bare `NNN`, bracketed `[..NNN..]`, `V0`, `V2`.
- `SampleSizeRegex`: `\b(?:(?<S24>24[-._ ]?bit|flac24(?:[-._ ]?bit)?|tr24|24-(?:44|48|96|192)|[\[\(].*24bit.*[\]\)]))\b`; `WebRegex`: `\b(?<web>WEB)(?:\b|$|[ .])`.
- Mapping: MP3VBR + V0 -> `MP3_VBR` (MP3-VBR-V0), + V2 -> `MP3_VBR_V2`; MP3CBR + 96/128/160/192/256/320 -> `MP3_096..MP3_320`; FLAC + S24 -> `FLAC_24` else `FLAC`; ALAC(+24) -> `ALAC`/`ALAC_24`; WAVPACK, APE, WMA, WAV; AAC + 192/256/320 -> `AAC_192/256/320` else `AAC_VBR`; OGG/OPUS + 160/192/224/256/320/500 -> `VORBIS_Q5..Q10`; unknown codec + 192/256/320/VBR -> MP3 equivalents. `ParseQuality(name, desc, fileBitrate, fileSampleSize)` also accepts real file bitrate when tags are available (`FindQuality(codec, bitrate, sampleSize)` maps every MP3 CBR rate 8..320, Opus by ranges <130/<180/<205/<240/<290).

### 5.4 Lidarr quality definitions (Quality.cs, https://github.com/Lidarr/Lidarr/blob/develop/src/NzbDrone.Core/Qualities/Quality.cs)

Ids/names: 0 Unknown, 1 MP3-192, 2 MP3-VBR-V0, 3 MP3-256, 4 MP3-320, 5 MP3-160, 6 FLAC, 7 ALAC, 8 MP3-VBR-V2, 9 AAC-192, 10 AAC-256, 11 AAC-320, 12 AAC-VBR, 13 WAV, 14 OGG Vorbis Q10, 15 Q9, 16 Q8, 17 Q7, 18 Q6, 19 Q5, 20 WMA, 21 FLAC 24bit, 22 MP3-128, 23 MP3-96, 24 MP3-80, 25 MP3-64, 26 MP3-56, 27 MP3-48, 28 MP3-40, 29 MP3-32, 30 MP3-24, 31 MP3-16, 32 MP3-8, 33 MP3-112, 34 MP3-224, 35 APE, 36 WavPack, 37 ALAC 24bit (MP3-8..MP3-128-ish are marked "For Current Files Only").

`DefaultQualityDefinitions` in ascending weight (Weight, GroupName, GroupWeight):
1 Unknown | 2-10 MP3-8, -16, -24, -32, -40, -48, -56, -64, -80 "Trash Quality Lossy" (2) | 11 MP3-96, 12 MP3-112, 13 MP3-128, 14 Vorbis Q5 & MP3-160 "Poor Quality Lossy" (3) | 15 MP3-192, Vorbis Q6, AAC-192, WMA; 16 MP3-224 "Low Quality Lossy" (4) | 17 Vorbis Q7; 18 MP3-VBR-V2, MP3-256, Vorbis Q8, AAC-256 "Mid Quality Lossy" (5) | 19 MP3-VBR-V0, AAC-VBR; 20 MP3-320, Vorbis Q9, AAC-320; 21 Vorbis Q10 "High Quality Lossy" (6) | 22 FLAC, ALAC, APE, WavPack; 23 FLAC 24bit, ALAC 24bit "Lossless" (7) | 24 WAV (8).
Size budgets (`MinSize`/`MaxSize`/`PreferredSize`, in Lidarr's kbps-style units used for release-size validation): e.g. MP3-192 `MaxSize 210, Preferred 95`; MP3-320 `MaxSize 350, Preferred 195`; lossless `MaxSize null, Preferred 895`.

---

## 6. Source index (all URLs used)

Specs: torznab spec HTML in https://github.com/torznab/torznab.github.io (spec-1.3-draft/torznab/Specification-v1.3.html; spec-1.3-draft/external/newznab/api.html); https://github.com/NNTmux/newznab-tmux/blob/master/docs/newznab_api_specification.txt; https://github.com/nZEDb/nZEDb/blob/dev/docs/newznab_api_specification.txt; BEP 3/21/47 at https://github.com/bittorrent/bittorrent.org/tree/master/beps.
Prowlarr: IndexerCapabilities.cs, NewznabStandardCategory.cs, NewznabRequest.cs (404 at fetched path - fields taken from ReleaseSearchService usage), ReleaseSearchService.cs, Prowlarr.Api.V1/Indexers/NewznabController.cs, Prowlarr.Api.V1/Search/SearchController.cs & ReleaseResource.cs, Prowlarr.Api.V1/openapi.json, Prowlarr.Http/Authentication/ApiKeyAuthenticationHandler.cs, NzbDrone.Core/Download/DownloadMappingService.cs, Indexers/Definitions/{Redacted,Orpheus,RuTracker}.cs, Definitions/Gazelle/GazelleRequestGenerator.cs (all under https://github.com/Prowlarr/Prowlarr/tree/develop/src); https://github.com/Prowlarr/Indexers.
Jackett: https://github.com/Jackett/Jackett/blob/master/src/Jackett.Common/Models/TorznabCapabilities.cs.
Gazelle: https://github.com/WhatCD/Gazelle/wiki/JSON-API-Documentation; https://github.com/OPSnet/Gazelle/blob/master/docs/07-API.md; https://github.com/WhatCD/Gazelle/blob/master/classes/torrentsearch.class.php; https://github.com/WhatCD/Gazelle/blob/master/sections/torrents/browse.php; https://github.com/WhatCD/Ocelot/blob/master/worker.cpp.
qBittorrent: https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-5.0); wiki 4.1; src/webui/webapplication.h, src/webui/api/torrentscontroller.cpp, src/webui/api/serialize/serialize_torrent.cpp (branches release-4.6.7, release-5.0.0, release-5.1.0, master); src/base/bittorrent/torrent.h, torrentcontentlayout.h, torrentimpl.cpp.
Transmission: https://github.com/transmission/transmission/blob/main/docs/rpc-spec.md; libtransmission/rpcimpl.cc, torrent.cc, torrent-ctor.cc, file-piece-map.cc; https://github.com/transmission/transmission/issues/787.
Deluge: https://github.com/deluge-torrent/deluge/blob/develop/deluge/core/core.py, deluge/core/torrent.py, deluge/ui/web/json_api.py.
rTorrent: https://github.com/rtorrent-community/rtorrent-docs/blob/master/docs/include-cmd-items.rst; https://github.com/rakshasa/libtorrent/blob/master/src/torrent/data/file_list.h.
aria2: https://github.com/aria2/aria2/blob/master/doc/manual-src/en/aria2c.rst.
libtorrent: https://github.com/arvidn/libtorrent/blob/RC_2_0/include/libtorrent/download_priority.hpp, torrent_handle.hpp, src/torrent.cpp, docs/manual.rst.
SABnzbd: https://github.com/sabnzbd/sabnzbd.github.io (wiki/configuration/5.2/api.html, switches.html, special.html, wiki/extra/nzb-spec.html, wiki/faq.html); https://github.com/sabnzbd/sabnzbd/blob/develop/sabnzbd/deobfuscate_filenames.py.
NZBGet: https://github.com/nzbgetcom/nzbget/tree/develop/docs/api (APPEND.md, EDITQUEUE.md, LISTFILES.md, LISTGROUPS.md, HISTORY.md); daemon/remote/XmlRpc.cpp; nzbget.conf.
ngPost: https://github.com/mbruel/ngPost.
Lidarr: Quality.cs, Parser/QualityParser.cs, Parser/Parser.cs, NzbDrone.Core.Test/ParserTests/QualityParserFixture.cs, MediaFiles/TrackImport/Specifications/{NoMissingOrUnmatchedTracks,MoreTracks}Specification.cs (https://github.com/Lidarr/Lidarr/tree/develop/src); https://github.com/Lidarr/Lidarr/issues/826; https://github.com/Servarr/Wiki/blob/master/lidarr/faq.md, import-troubleshooting.md, troubleshooting.md.
Others: https://github.com/rembo10/headphones; https://github.com/mrusse/soularr; https://github.com/TypNull/Tubifarry.
Blocked (not used): torznab.github.io, sabnzbd.org, nzbget.com, prowlarr.com, wiki.servarr.com, deluge.readthedocs.io, rtorrent-docs.readthedocs.io, scenerules.org, en.wikipedia.org, interviewfor.red, redacted.sh, orpheus.network, forums.sabnzbd.org, techsono.com, trac.transmissionbt.com, mp3scene.info.
