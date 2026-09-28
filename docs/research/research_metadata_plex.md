# Research: metadata sources, identity/fingerprinting, tagging, naming and Plex/Plexamp library requirements for a single-song *arr

> **Name:** this document predates the rename of the project from *Compilarr* to **Wondarr** (2026-09-28, `docs/DECISIONS.md` build session 2 #10); it keeps the old name as a historical record.

Date: 2026-09-28. Prepared for the design document of a self-hosted *arr-style app whose unit of "wanted" item is ONE SONG (a MusicBrainz recording).

## 0. How this was researched and how to read the citations

The sandbox egress proxy blocked most primary documentation hosts (musicbrainz.org, wiki.musicbrainz.org, picard-docs.musicbrainz.org, acoustid.org, support.plex.tv, forums.plex.tv, plex.tv, developer.spotify.com, developers.deezer.com, last.fm, discogs.com, theaudiodb.com, fanart.tv, lrclib.net, readthedocs.io, wiki.servarr.com, web.archive.org). Two channels worked: (a) raw GitHub files (`raw.githubusercontent.com`, `github.com` HTML), so wherever the documentation or the implementation lives in a public repo it was read directly; (b) web search, whose result summaries quote the blocked pages.

Each claim below is tagged:

- **[P]** primary source fetched and read directly (repo source code, README or docs source).
- **[S]** official page could not be fetched; the fact comes from search-engine extracts of that page. Treat as "very likely correct, re-verify wording before quoting in a spec".
- **[T]** third-party (blog, forum summary, client library README). Treat as indicative.
- **[U]** could not be verified at all; stated as such.

---

## 1. MusicBrainz as the canonical identity of a song

### 1.1 Entity model (which MBID identifies "a song")

Definitions (from musicbrainz.org/doc pages; [S] via search extracts of https://musicbrainz.org/doc/Recording, /doc/Track, /doc/Release, /doc/Release_Group, /doc/Work):

- **Recording**: "represents distinct audio that has been used to produce at least one released track through copying or mastering." "Each track must always be associated with a single recording, but a recording can be linked to any number of tracks."
- **Track**: "the way a recording is represented on a particular release (or, more exactly, on a particular medium)." A track has its own MBID (the *release track id*), distinct from the recording MBID.
- **Release**: a concrete product ("something you can buy, such as a CD or a digital download"); standard vs deluxe editions are different releases inside one release group.
- **Release group**: "embraces the overall concept of an album"; every release belongs to exactly one release group. Release groups carry a primary type (Album, Single, EP, Broadcast, Other) and secondary types (Compilation, Live, Remix, Soundtrack, Demo, ...) — https://musicbrainz.org/doc/Release_Group/Type [S].
- **Work**: "a distinct intellectual or artistic creation ... the composition behind the recording"; "Hurt represents the composition by Trent Reznor, regardless of whether it is performed by Nine Inch Nails, Johnny Cash or any other artist." (https://musicbrainz.org/doc/Work [S])

**Recommendation: the app's wanted item = a Recording MBID.** It is the only entity that means "this specific audio" and it is what AcoustID resolves to (see 2). Caveats the design must handle:

1. One *song* (work) has many recordings: album version, radio/single edit, live, remix, acoustic, re-recording. The MusicBrainz Style/Recording guideline (https://musicbrainz.org/doc/Style/Recording [S]) says:
   - Remasters are **not** separate recordings: "Separate recordings should not be created for remastered tracks, since remastered tracks generally feature the original recording with different mastering applied" (remastering is expressed as a release–release "remaster" relationship or an annotation).
   - Radio/single edits **are** separate recordings: "A 'radio edit' or 'single edit' may be produced by removing an intro or outro ... These edited versions should be treated as separate recordings."
   - Video recordings are always separate from audio-only recordings, even with identical audio.
   - "Recordings of different durations can be merged, as long as there is no evidence to suggest that differences in mixing or editing have caused the change in lengths."
   Consequence: a wanted recording MBID will match the 1998 original *and* the 2015 remaster (same recording), but not the radio edit. The UI should show the recording's disambiguation comment, length and the release-group primary/secondary types of the releases it appears on, so the user can pick "the album version" vs "the single edit".
2. The same recording appears on many releases (album, single, compilation, deluxe). For tagging/placement the app must *choose one release* (see 4 and 5). Sensible default: prefer the release whose release group primary type is Album and status Official, in the user's preferred country, earliest date; allow override.
3. Because Plex matches at album level (5.2), the chosen release/release-group MBID matters for Plex as much as the recording MBID.

### 1.2 Web Service v2 — lookup, search, ISRC, browse

Verified from the musicbrainzngs client source (https://raw.githubusercontent.com/alastair/python-musicbrainzngs/master/musicbrainzngs/musicbrainz.py [P]) which mirrors the official API docs, plus search extracts of https://musicbrainz.org/doc/MusicBrainz_API and /doc/MusicBrainz_API/Search [S]:

- Base: `https://musicbrainz.org/ws/2/`. Add `fmt=json` (or `Accept: application/json`) for JSON [S].
- **Lookup**: `GET /ws/2/recording/{mbid}?inc=...`. Valid `inc` for recording [P]: `artists, releases, discids, media, artist-credits, isrcs, work-level-rels, annotation, aliases` plus tags/ratings/relationship includes (`artist-rels, work-rels, url-rels, ...`). Example from the docs [S]: `https://musicbrainz.org/ws/2/recording/b9ad642e-b012-41c7-b72a-42cf4911f9ff?inc=artist-credits+isrcs+releases`. For release lookups [P]: `artists, labels, recordings, release-groups, media, artist-credits, discids, isrcs, recording-level-rels, work-level-rels, annotation, aliases`. Release-group: `artists, releases, discids, media, artist-credits, annotation, aliases`. When `releases` is included in a recording lookup, `status` and `type` query parameters filter the releases returned (e.g. `status=official&type=album`) [S — documented on the API page; not re-verified].
- **ISRC lookup**: `GET /ws/2/isrc/{ISRC}?inc=artists+releases+isrcs` — musicbrainzngs builds `_do_mb_query("isrc", isrc, includes, params)` with valid includes `["artists", "releases", "isrcs"]` [P]. Returns the recordings carrying that ISRC (one ISRC can map to several recordings, and one recording can carry several ISRCs).
- **Search**: `GET /ws/2/recording?query=<lucene>&limit=&offset=`. Recording search fields [P, musicbrainzngs VALID_SEARCH_FIELDS['recording'], identical to the docs list]: `alias, arid, artist, artistname, comment, country, creditname, date, dur, format, isrc, number, position, primarytype, qdur, recording, recordingaccent, reid, release, rgid, rid, secondarytype, status, tag, tid, tnum, tracks, tracksrelease, type, video`. Unqualified terms search the `recording` (title) field [S]. Useful query for our app: `recording:"Song Title" AND artist:"Artist" AND dur:[200000 TO 260000] AND primarytype:album AND status:official` (`dur` is milliseconds; `qdur` is quantized duration). `isrc:XXXX` search also works.
- **Browse**: e.g. `/ws/2/recording?release={release-mbid}` or `?artist={mbid}` (musicbrainzngs browse_recordings params `{"artist","release"}` [P]).
- Rate limiting (https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting [S], consistent with musicbrainzngs default "1 request per 1.0 s" [P]): "1 request per second (on average) per IP address", "50 requests per second (on average) per user agent", "300 requests per second (on average) globally"; violations get **HTTP 503**. A User-Agent with contact info is mandatory: "MyAwesomeTagger/1.2.0 ( http://myawesometagger.example.com )" or "( me@example.com )". Misbehaving UAs can be throttled specifically. musicbrainzngs refuses to run without `set_useragent("application name", "application version", "contact info ...")` [P].
- Cover Art Archive (https://musicbrainz.org/doc/Cover_Art_Archive/API [S]; server is https://coverartarchive.org): `GET /release/{mbid}` → JSON index of images (`types`, `front`, `back`, `image`, `thumbnails` with keys `"250"`, `"500"`, `"1200"`; `"small"`/`"large"` are deprecated aliases for 250/500). `GET /release/{mbid}/front` and `/front-250`, `/front-500`, `/front-1200` → **307 Temporary Redirect** to the binary on archive.org. `GET /release-group/{mbid}/front` picks "the image that is most suitable to be called the front of a release group". 404 when no release or no chosen front image; the redirected thumbnail URL may itself 404 if that size does not exist. The coverart_redirect service README [P] confirms it redirects coverartarchive.org URLs to archive.org, "taking into account MBID redirects caused by release and event merges". Practical: use `/release-group/{rgid}/front-500` for the wanted item's art, fall back to `/release/{mbid}/front-500`, then Deezer/iTunes (3).

### 1.2.1 Live verification before Phase 1 (2026-09-28) [P — observed responses]

Checked with real requests (User-Agent `Compilarr/0.1.0 ( https://github.com/wulfftech/compilarr )`, ≥ 1.2 s apart); the responses are committed as fixtures under `tests/fixtures/{musicbrainz,deezer,itunes}/`.

- **MusicBrainz.** Responses carry `X-RateLimit-Limit`/`X-RateLimit-Remaining`/`X-RateLimit-Reset`; the docs say a throttled request gets **503** and do not promise `Retry-After`, so the client must honour it when present and otherwise back off itself. Unknown ISRC or MBID → **404** `{"error":"Not Found"}`; a syntactically invalid MBID (even the nil UUID) → **400** `{"error":"Invalid mbid."}`.
- **Recording lookup caps `inc=releases` at 25 releases** (Bohemian Rhapsody returned exactly 25 of 319 official ones). The complete list comes from the **release browse** `GET /ws/2/release?recording={mbid}&status=official&inc=release-groups+media+artist-credits&limit=100&offset=N` → `{release-count, release-offset, releases[]}` where each release has `id, title, status, date, country, artist-credit[], release-group{id, title, primary-type, secondary-types[], first-release-date}, media[]{position, format, track-count}` — **browse media carry no tracks**, so the track/disc number comes from a release lookup `GET /ws/2/release/{id}?inc=recordings+artist-credits+release-groups` (`media[].tracks[]{position, number, recording{id}}`). A recording lookup with `inc=releases+media` does include the one matching track per medium (`media[].tracks[]{position, number}` plus `track-offset`), but only for its 25 releases.
- **Recording search** results have `id, score, title, length (ms, may be missing), disambiguation (may be missing), video, first-release-date, artist-credit[]{name, joinphrase (missing for a single artist), artist{id, name, sort-name}}, releases[]` (a sample, often 1) — **no `isrcs`** (those need a lookup with `inc=isrcs`). A search for "Get Lucky" by Daft Punk returns eight score-100/90 hits including a 40-second snippet, a DJ-intro version and several remixes, so the top MB score alone does not pick the canonical recording.
- **Cover Art Archive.** `HEAD /release/{mbid}/front-500` and `/release-group/{rgid}/front-500` → **307** with `Location: https://archive.org/download/mbid-…_thumb500.jpg` when art exists, **404** when not. No rate limit is documented.
- **Deezer** (`api.deezer.com`, no auth). **Field queries return nothing in 2026**: `q=artist:"daft punk" track:"get lucky"` (any casing, with or without `strict=on`, `/search/track` too) → `{"data":[],"total":0}`, while `q=daft punk get lucky` returns 74 hits; so search with a plain query and filter client-side. Search hits include `id, title, title_short, title_version, isrc, duration (s), rank, explicit_lyrics, preview, artist{id,name}, album{id,title,cover_xl}`. `/track/isrc:{isrc}` works but may return any release carrying the ISRC (GBUM71029604 → a 2025 compilation, with an empty `preview`). Errors come as **HTTP 200** with `{"error":{"type":"DataException","message":"no data","code":800}}`. `preview` URLs are signed (`hdnea=exp=…`) and expire after about 30 minutes. `/album/{id}` has `record_type` (album/single/ep/compile), `release_date`, `upc`, `cover_xl` (1000×1000). Quota: 50 requests / 5 s per IP with error code 4 "Quota limit exceeded" according to third-party reports (not in Deezer's own reachable docs).
- **iTunes Search API.** `search?term=&entity=song&limit=&country=` → `results[]{trackId, trackName, artistName, collectionName, trackTimeMillis, previewUrl, artworkUrl100, releaseDate, trackNumber, discNumber}`; artwork is documented at 100×100 only (the `100x100bb` → `600x600bb` rewrite is community knowledge). `lookup?id=` works; ISRC lookup is not documented (returns `resultCount: 0`). About 20 calls per minute; excess → **403** (Apple forums).

### 1.3 Self-hosting / caching

- **Mirror**: metabrainz/musicbrainz-docker (README [P]): full mirror needs "CPU: 16 threads (or 2 without indexed search)", "RAM: 16 GB (or 4 without indexed search)", "Disk Space: 350 GB (or 100 without indexed search)". Replication requires a MetaBrainz access token (`admin/configure add replication-token`), can run as cron (`admin/configure add replication-cron`, default "every day at 3 am UTC"); "Search indexes are not included in replication. You will have to rebuild search indexes regularly" (rebuild ~4.5 h or download ~60 GB pre-built). The Live Data Feed docs [S] say hourly replication packets keep a mirror "about an hour off sync"; non-commercial users get a free token, commercial use needs a MetaBrainz agreement.
- **Lidarr metadata proxy** as the reference architecture: Lidarr's cloud request builder [P, `src/NzbDrone.Common/Cloud/LidarrCloudRequestBuilder.cs`] targets `https://api.lidarr.audio/api/v0.4/{route}`. Routes used by SkyHookProxy.cs [P]: `artist/{foreignArtistId}`, `album/{foreignAlbumId}`, `search?type=artist|album|all&query=&artist=&includeTracks=1`, `search/fingerprint` (POST of recording ids from fingerprints), `recent/artist?since=`, `recent/album?since=`; Lidarr caches "ChangedAlbums" for 30 minutes. The server (Lidarr/LidarrAPI.Metadata, default branch `develop` [P]) sits on a MusicBrainz PostgreSQL replica plus Solr, and its providers [P, `lidarrmetadata/provider.py`] are `MusicbrainzDbProvider`, `SolrSearchProvider`, `FanArtTvProvider`, `TheAudioDbProvider`, `SpotifyProvider`/`SpotifyAuthProvider`, `WikipediaProvider`; cache TTLs come from `CONFIG.CACHE_TTL['cloudflare']`. Servarr's FAQ [S] notes a MusicBrainz edit "is only visible in Lidarr once it has propagated through all three caches" (hours+). Self-hosted replacements exist (NC1107/lidarr-metadata-provider, statichum/brainzmash-hearring-aid [T]). Lesson for our design: a **local cache/proxy layer keyed by MBID with TTLs**, plus optional pointing at a self-hosted mirror, rather than hitting musicbrainz.org per request.

---

## 2. AcoustID / Chromaprint for verifying a downloaded file

### 2.1 fpcalc

From chromaprint `src/cmd/fpcalc.cpp` help text [P]: `-length SECS` "Restrict the duration of the processed input audio (default 120)", `-chunk SECS`, `-algorithm NUM` "(default 2)", `-overlap`, `-ts`, `-raw`, `-signed`, `-json` "Print the output in JSON format", `-text`, `-plain` "Print the just the fingerprint in text format", `-format`, `-rate`, `-channels`, `-version`. So `fpcalc -json file.flac` → `{"duration": 251.32, "fingerprint": "AQADtEm..."}`; the duration reported is the full file duration even though only the first 120 s are fingerprinted. fpcalc needs FFmpeg libraries to decode (Chromaprint README [P]). Chromaprint "is designed to identify near-identical audio" and trades precision for search speed [P].

### 2.2 AcoustID lookup API

Official page (https://acoustid.org/webservice) is blocked; facts below come from the server source (acoustid/acoustid-server `acoustid/api/v2/__init__.py` and `acoustid/api/errors.py` [P]) and client READMEs:

- `GET/POST https://api.acoustid.org/v2/lookup?client=<app key>&duration=<int seconds>&fingerprint=<fp>&meta=<list>` (+ optional `trackid=<acoustid uuid>` instead of a fingerprint, `format=json|xml|jsonp`). `client` is the **application API key** (register the app at acoustid.org) [P: `values.get("client")`]; a separate *user* key is only needed for submissions.
- `meta` values [P]: `recordings, recordingids, releases, releaseids, releasegroups, releasegroupids, tracks, compress, usermeta, sources, isrcs` (space/plus separated).
- Response [P]: `results: [{"id": <acoustid uuid>, "score": <0..1>, "recordings": [{"id": <recording mbid>, "title", "duration", "artists", "isrcs", "releasegroups"...}]}]`. Limits [P]: `MAX_FINGERPRINT_QUERIES_PER_REQUEST = 20`, `MAX_TRACK_QUERIES_PER_REQUEST = 100`, `MAX_RESULTS_PER_FINGERPRINT_QUERY = 10`. Rate limiting uses application, global and per-IP buckets [P]; the published limit is **3 requests/second** (pyacoustid README: "The module internally performs thread-safe API rate limiting to 3 queries per second whenever the Web API is called" [P]). Over-limit → error 14 "rate limit exceeded", **HTTP 429** [P]; also 13 "service currently unavailable" (503), 16 "only HTTPS requests allowed", 18 "fingerprint not found" (404), 19 "request too large" (413), 4 "invalid API key".
- Submission: `POST /v2/submit` with `client`, `user` (user API key), `fingerprint.N`, `duration.N`, `mbid.N` (plus bitrate/fileformat); pyacoustid `submit()` [P].

### 2.3 Scores and thresholds

- Score is "a float between 0 and 1" (pyacoustid `parse_lookup_result` [P]). It is the similarity between the query fingerprint and the stored AcoustID fingerprint, *not* a probability that the MB recording is correct; one AcoustID can be linked to several recordings (Picard's AcoustID tutorial, blocked, [S]).
- Typical practice: Picard/beets accept matches with score ≥ ~0.5 and rank by score. beets `chroma` plugin [P] "turns Acoustid fingerprint matches into autotagger candidates by resolving them through the musicbrainz plugin" and stores `acoustid_id` and `acoustid_fingerprint` fields, writing them to files if `import.write` is on. A precise numeric threshold used by beets (`CHROMA` score cutoff) could not be re-verified [U]; design should make the threshold configurable (suggest: accept ≥0.7 when the wanted recording MBID is among the returned recordings; treat 0.5–0.7 as "needs review"; also cross-check |duration − MB length| ≤ ~5 s).
- **Verification logic for our app**: compute fingerprint → lookup with `meta=recordingids` (cheap) → success if the wanted recording MBID (or any recording in the same "same-audio" class, e.g. after a MB merge) is present with score ≥ threshold. Because remasters share the recording MBID (1.1), a remaster will verify as the wanted recording, which is usually desired.

### 2.4 Failure modes

- Only the first 120 s are fingerprinted; very short files (< ~10–15 s) produce weak/no fingerprints — Chromaprint changelog notes short files were "ignored in version 1.4" and fixed later [T, GitHub issues]. Files shorter than the lookup's minimum may return no results.
- Transcodes (MP3/AAC/Opus at normal bitrates) fingerprint fine — that is the design goal ("near-identical audio").
- Speed/pitch changes break matching: a documented case where "a playback speed difference of about 2.5%" between a file and a YouTube rip "prevented fingerprint matching until the speed was corrected" [T]. Some YouTube uploads are pitch-shifted or sped up to dodge Content ID, so YouTube-sourced audio is the main risk.
- Different *mixes* (radio edit, remix, live) are different fingerprints and different MB recordings — good (they should fail verification).
- Different *masters* usually match (same recording) but very loud/limited remasters or heavy EQ can drop the score; do not use a hard 0.9+ cutoff.
- Intros/silence/DJ talk-over at the start shift the fingerprinted window; consider a second lookup on a chunk from the middle (`-chunk`/`-length` combos) as a fallback [design suggestion].
- AcoustID coverage: obscure/new tracks may have no AcoustID at all → fall back to metadata heuristics (duration, ISRC from source, title/artist) and mark "unverified".

### 2.5 Bindings per language

- Python: **pyacoustid** (beetbox/pyacoustid [P]) — `acoustid.match(apikey, path)`, `fingerprint_file(path)` (uses libchromaprint or `fpcalc`; `force_fpcalc`), `lookup(apikey, fp, duration, meta=[...])`, `parse_lookup_result`, `submit`; built-in 3 req/s limiter; decoding via audioread.
- .NET: **AcoustID.NET** (wo80/AcoustID.NET [P]) — managed Chromaprint port ("AcoustID fingerprint calculation" + `LookupService`/`SubmitService`); `AcoustID.Configuration.ClientKey = "APIKEY"`; resampler code derived from FFmpeg (LGPL 2.1). Audio decoding is up to you (NAudio/ffmpeg).
- Go: **alexgorbatchev/gochromaprint** [P] — "A pure Go port of Chromaprint", "Bit-for-bit Compatible ... including default Algorithm 2", "Zero CGO"; you feed interleaved 16-bit PCM (decode with ffmpeg or a Go decoder); ships a drop-in `fpcalc`. Alternatives: go-fingerprint/gochroma (cgo bindings) [T], jo-hoe/chromaprint (wraps the fpcalc binary) [T].
- Node: **parshap/node-fpcalc** [P] — wraps the `fpcalc` binary (`command` option, default `"fpcalc"` on PATH). No mature pure-JS Chromaprint found.
- Any language: shelling out to `fpcalc -json` is the simplest and is what Picard/beets/Lidarr do.

---

## 3. Supplementary metadata / discovery sources (status as of Sept 2026)

| Source | Auth | Rate limit | Best use for us |
|---|---|---|---|
| **Spotify Web API** | OAuth (PKCE/auth-code) for user data; client-credentials for catalog | Development mode: **5 authenticated users**, owner must have **Premium**, Client-ID cap (1, raised to 25 per account on 2026-07-23), shared per-account quota with `429` reason `QUOTA_EXCEEDED` [S/T] | Importing a user's own playlists / liked songs (with ISRC) — *only for the app owner + ≤4 users*; **not** viable as a public discovery backend |
| **Deezer API** | None for catalog reads; user auth tokens no longer issued to individuals | ~50 requests / 5 s per IP [T] | ISRC lookup (`/track/isrc:{ISRC}`), search, 30-s previews, cover art up to `cover_xl` (1000×1000), BPM/gain fields |
| **iTunes Search API** | None | "approximately 20 calls per minute (subject to change)" [P] | Search + 30-s previews + artwork up to 3000×3000 (rewrite URL token) |
| **Last.fm** | API key (free); user auth for scrobbling | TOS 4.4: ≤5 req/s per IP averaged over 5 min [S]; must cache chart/similar data ≥1 week [S] | Loved tracks / top tracks / scrobble history import; tags; listeners/playcount |
| **ListenBrainz** | User token (free, open) | Header-driven (`X-RateLimit-*`), 429 on excess; "never make more than ONE call per second" per client [P] | Open alternative to Last.fm: listens, loved (feedback), playlists, recommendations, MBID-native |
| **Discogs** | Token or OAuth (free) | 60/min authenticated, 25/min unauthenticated, per IP; unique User-Agent required [S] | Release-level metadata (labels, catalog numbers, credits), images only with auth; not song-level |
| **TheAudioDB** | API key; free public key `123` (older docs: `2`) [S] | 30 req/min on the free key; higher via Patreon ($8 tier) key [S] | Artist/album/track thumbs, artist fanart, bios; endpoints keyed by MBID (`artist-mb.php?i=`, `album-mb.php?i=`, `track-mb.php?i=`) [S] |
| **fanart.tv** | project `api_key` required, optional personal `client_key` [P] | not published [U] | Artist backgrounds (1920×1080, 4K), `artistthumb`, `musiclogo/hdmusiclogo`, `musicbanner`, `albumcover`, `cdart` — keyed by MusicBrainz artist MBID / release-group MBID [P] |
| **LRCLIB** | None ("no need for an API key or any kind of registering") [S] | "generous"; 429 + `Retry-After` must be honoured; advice: sequential requests with 200–500 ms gaps for library scans [S] | Free plain + synced (LRC) lyrics by title/artist/album/duration |
| **Genius** | OAuth access token | not published | Song/artist metadata and annotations; **lyrics are not in the API** and scraping violates Genius ToS [S/T] |

Details and citations:

**Spotify.** 2024-11-27 blog "Introducing some changes to our Web API" (https://developer.spotify.com/blog/2024-11-27-changes-to-the-web-api [S]): new apps and apps still in development mode lost `related-artists`, `recommendations`, `audio-features`, `audio-analysis`, `featured-playlists`, `category playlists`, 30-second `preview_url`s and Spotify-owned editorial playlists; apps already in extended quota mode kept them. 2025-04-15 "Updating the Criteria for Web API Extended Access" [S]: extended quota requires a legally registered business, 250 k MAU, launched service; since 2025-05-15 only organisations may apply. 2026-02-06 (TechCrunch, and Spotify's quota-modes doc quoted in GitHub issue Osasuwu/like-current-song#122 [P]): "Up to 5 authenticated Spotify users can use an app that is in development mode" (was 25), "The app owner must have a Spotify Premium account for apps in development mode to function", "Developers will be limited to one Development Mode Client ID", enforcement from 2026-03-09. 2026-07-23 blog "Web API quota updates for Development Mode" [S]: per-account shared quota, client-ID cap raised to 25, `429` with reason `QUOTA_EXCEEDED`; one third-party summary also reports search `limit` max reduced 50→10 [T, unverified]. What still works [S]: `GET /search` (`q=isrc:USUM71703861` filter syntax; also `track:`, `artist:`, `album:`, `year:`, `upc:`), track/album/artist lookups, `GET /me/tracks` (scope `user-library-read`), `GET /me/playlists` + `GET /playlists/{id}/items` (`playlist-read-private`, `playlist-read-collaborative`), track objects carry `external_ids.isrc` (sometimes empty). Design consequence: Spotify import is a **bring-your-own-client-ID, owner-only** feature; never a shared backend.

**Deezer.** `https://api.deezer.com` [P, deezer-python client]; track object fields [P]: `id, title, title_short, title_version, isrc, link, duration, track_position, disk_number, rank, release_date, explicit_lyrics, explicit_content_lyrics, explicit_content_cover, preview (30-s MP3 URL), bpm, gain, available_countries, contributors, md5_image, artist, album`; album objects expose `cover, cover_small (56px), cover_medium (250px), cover_big (500px), cover_xl (1000px)` [T]. `GET /track/isrc:{ISRC}` is an undocumented but widely used endpoint ("returns one track with the given ISRC. However, in some cases multiple tracks have the same ISRC" — Deezer community [T]). Search: `/search?q=` with advanced syntax `artist:"..." track:"..."` plus `dur_min/dur_max/bpm_min/bpm_max` [P]. Rate limit ≈ "50 requests per 5 seconds" [T]. **Status 2026**: Deezer "has disabled the possibility of creating new accesses enabling the use of their API by private individuals" (Deezer developer FAQ, [S]); existing tokens keep working and the unauthenticated catalog endpoints still respond [S/T]. Treat as best-effort enrichment/discovery, not a dependency.

**iTunes Search API.** Apple docs (developer.apple.com archive [P]): `https://itunes.apple.com/search?term=&country=&media=music&entity=song&limit=&lang=&explicit=`, and `https://itunes.apple.com/lookup?id|amgArtistId|amgAlbumId|upc|isbn|amgVideoId` — **no ISRC lookup** [P]. "The Search API is limited to approximately 20 calls per minute (subject to change)" [P]. Results include `artworkUrl60`, `artworkUrl100`, `previewUrl` ("30-second preview"), `trackId`, `collectionId`, `artistId`, `trackTimeMillis` [P]; no ISRC in results [P]. Artwork trick: replace the size token in `artworkUrl100` (`.../100x100bb.jpg`) with `3000x3000bb.jpg`; beets PR #7045 [P] switched from the legacy `100000x100000-999` token (now HTTP 400 on Apple's CDN) to `3000x3000bb`, "the standard modern Apple Music bounding box token" that "requests the native master resolution up to 3000×3000px".

**Last.fm.** Methods `track.getInfo` (artist+track or mbid; returns duration, listeners, playcount, album, toptags, wiki, `userloved` when `username` given), `user.getLovedTracks`, `user.getRecentTracks`, `user.getTopTracks` [S]. TOS (https://www.last.fm/api/tos [S]): "you will not make more than 5 requests per originating IP address per second, averaged over a 5 minute period"; error 29 "Rate Limit Exceeded"; cache similar-artist and chart data ≥ one week. Note Last.fm MBIDs are frequently missing/stale — match by name+MBID fallback.

**ListenBrainz.** Docs source [P, metabrainz/listenbrainz-server docs/users/api/index.rst]: `Authorization: Token <user token>`; headers `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset-In`, `X-RateLimit-Reset`; 429 on excess; token-authenticated requests may get higher limits; mandatory User-Agent "Application name/<version> ( contact-url )" or requests "may be blocked without further notice". Endpoints: `/1/user/{name}/listens`, `/1/feedback/user/{name}/get-feedback` (loved = score 1), `/1/playlist/...`, plus recording-MBID-centric metadata. Good MBID-native import source.

**Discogs** (https://www.discogs.com/developers [S]): "Requests are throttled by the server by source IP to 60 per minute for authenticated requests, and 25 per minute for unauthenticated requests"; moving 60-s window; must send a unique User-Agent; image URLs require authentication. Song-level use is weak (no recording identity); mainly for label/catalogue enrichment.

**TheAudioDB** [S]: free key `123` limited to 30 req/min (429 when exceeded, wait a minute); private keys via Patreon; MBID-keyed endpoints; fields such as `strArtistThumb`, `strArtistFanart`, `strAlbumThumb`, `strTrackThumb`.

**fanart.tv** (JS client README [P]; official docs blocked): `GET https://webservice.fanart.tv/v3/music/{artist-mbid}?api_key=&client_key=`, `/v3/music/albums/{release-group-mbid}`, `/v3/music/labels/{label-mbid}`; "Required: Your fanart.tv API key", "Optional: Personal key for faster updates". Image types `artistbackground (1920x1080)`, `artist4kbackground`, `artistthumb`, `musiclogo`, `hdmusiclogo`, `musicbanner`, `albums.{rgid}.albumcover|cdart`. Plex's own Fanart-TV agent bundle exists (plexinc-agents/Fanart-TV.bundle [T]).

**LRCLIB** [S, extracts of https://lrclib.net/docs; client READMEs P]: `GET https://lrclib.net/api/get?track_name=&artist_name=&album_name=&duration=` ("You must provide the track title and the artist name. Providing the album name and the track's duration in seconds is recommended and improves the matching precision"), `GET /api/get-cached` (same params, cache only), `GET /api/get/{id}`, `GET /api/search?q=` or `?track_name=&artist_name=&album_name=`, `POST /api/request-challenge` + `POST /api/publish` (proof-of-work token, `X-Publish-Token`). Response fields: `id, trackName (alias name), artistName, albumName, duration, instrumental, plainLyrics, syncedLyrics` (+ `lyricsfile` raw YAML in newer versions [T]). Identify your client with `User-Agent` (or `X-User-Agent` / `Lrclib-Client` when the UA header can't be set). 429 + `Retry-After` must be honoured; ignoring it "may result in a temporary ban". Content is contributed under a permissive/public-domain style licence (verify on lrclib.net before shipping) [U on exact licence text]. This is the obvious synced-lyrics source; write `.lrc` sidecars for Plex (5.4).

**Genius** [S/T]: "The Genius API doesn't offer lyrics"; lyricsgenius scrapes HTML ("this feature isn't officially supported by the Genius API"); "Genius terms currently expressly prohibit scraping/data-extraction". Use only for metadata/annotations if at all.

---

## 4. Tagging one song across MP3 / FLAC / M4A / Opus

### 4.1 The tag set (Picard convention)

Picard's mapping table (https://picard-docs.musicbrainz.org/en/appendices/tag_mapping.html; source read at metabrainz/picard-docs `appendices/tag_mapping.rst` [P]; corroborated by picard/formats/id3.py, mp4.py, vorbis.py [P]). Columns: ID3v2.3 | ID3v2.4 | Vorbis (FLAC/OGG/Opus) | MP4 (M4A/AAC/ALAC) | ASF.

| Picard tag | ID3v2.3 | ID3v2.4 | Vorbis comment | MP4 atom | ASF |
|---|---|---|---|---|---|
| title | TIT2 | TIT2 | TITLE | ©nam | Title |
| artist | TPE1 | TPE1 | ARTIST | ©ART | Author |
| albumartist | TPE2 | TPE2 | ALBUMARTIST | aART | WM/AlbumArtist |
| album | TALB | TALB | ALBUM | ©alb | WM/AlbumTitle |
| tracknumber / totaltracks | TRCK (`n/total`) | TRCK | TRACKNUMBER / TRACKTOTAL (also TOTALTRACKS) | trkn | WM/TrackNumber |
| discnumber / totaldiscs | TPOS (`n/total`) | TPOS | DISCNUMBER / DISCTOTAL (also TOTALDISCS) | disk | WM/PartOfSet |
| date | TYER + TDAT | TDRC | DATE | ©day | WM/Year |
| originaldate | TORY (year only) | TDOR | ORIGINALDATE | n/a | WM/OriginalReleaseTime |
| originalyear | n/a | n/a | ORIGINALYEAR | n/a | WM/OriginalReleaseYear |
| genre | TCON | TCON | GENRE | ©gen | WM/Genre |
| isrc | TSRC | TSRC | ISRC | ----:com.apple.iTunes:ISRC | WM/ISRC |
| **musicbrainz_recordingid** (recording MBID) | UFID:http://musicbrainz.org | UFID:http://musicbrainz.org | **MUSICBRAINZ_TRACKID** | ----:com.apple.iTunes:MusicBrainz Track Id | MusicBrainz/Track Id |
| **musicbrainz_trackid** (release-track MBID) | TXXX:MusicBrainz Release Track Id | same | MUSICBRAINZ_RELEASETRACKID | ----:com.apple.iTunes:MusicBrainz Release Track Id | MusicBrainz/Release Track Id |
| musicbrainz_albumid (release MBID) | TXXX:MusicBrainz Album Id | same | MUSICBRAINZ_ALBUMID | ----:com.apple.iTunes:MusicBrainz Album Id | MusicBrainz/Album Id |
| musicbrainz_artistid | TXXX:MusicBrainz Artist Id | same | MUSICBRAINZ_ARTISTID | ----:com.apple.iTunes:MusicBrainz Artist Id | MusicBrainz/Artist Id |
| musicbrainz_albumartistid | TXXX:MusicBrainz Album Artist Id | same | MUSICBRAINZ_ALBUMARTISTID | ----:com.apple.iTunes:MusicBrainz Album Artist Id | MusicBrainz/Album Artist Id |
| musicbrainz_releasegroupid | TXXX:MusicBrainz Release Group Id | same | MUSICBRAINZ_RELEASEGROUPID | ----:com.apple.iTunes:MusicBrainz Release Group Id | MusicBrainz/Release Group Id |
| musicbrainz_workid | TXXX:MusicBrainz Work Id | same | MUSICBRAINZ_WORKID | ----:com.apple.iTunes:MusicBrainz Work Id | MusicBrainz/Work Id |
| acoustid_id | TXXX:Acoustid Id | same | ACOUSTID_ID | ----:com.apple.iTunes:Acoustid Id | Acoustid/Id |
| acoustid_fingerprint | TXXX:Acoustid Fingerprint | same | ACOUSTID_FINGERPRINT | ----:com.apple.iTunes:Acoustid Fingerprint | Acoustid/Fingerprint |
| compilation | TCMP | TCMP | COMPILATION | cpil | WM/IsCompilation |
| lyrics | USLT:description | USLT:description | LYRICS | ©lyr | WM/Lyrics |
| replaygain_track_gain / _peak / album_gain / album_peak | TXXX:REPLAYGAIN_TRACK_GAIN etc. | same | REPLAYGAIN_TRACK_GAIN etc. | ----:com.apple.iTunes:REPLAYGAIN_TRACK_GAIN etc. | REPLAYGAIN_TRACK_GAIN |
| r128_track_gain / r128_album_gain | n/a | n/a | R128_TRACK_GAIN / R128_ALBUM_GAIN (Opus only) | n/a (Picard lists r128 as unsupported for MP4 [P]) | n/a |
| barcode | TXXX:BARCODE | same | BARCODE | ----:com.apple.iTunes:BARCODE | WM/Barcode |
| label | TPUB | TPUB | LABEL | ----:com.apple.iTunes:LABEL | WM/Publisher |
| catalognumber | TXXX:CATALOGNUMBER | same | CATALOGNUMBER | ----:com.apple.iTunes:CATALOGNUMBER | WM/CatalogNo |
| releasestatus | TXXX:MusicBrainz Album Status | same | RELEASESTATUS | ----:com.apple.iTunes:MusicBrainz Album Status | MusicBrainz/Album Status |
| releasetype | TXXX:MusicBrainz Album Type | same | RELEASETYPE | ----:com.apple.iTunes:MusicBrainz Album Type | MusicBrainz/Album Type |
| media | TMED | TMED | MEDIA | ----:com.apple.iTunes:MEDIA | WM/Media |
| releasecountry | TXXX:MusicBrainz Album Release Country | same | RELEASECOUNTRY | ----:com.apple.iTunes:MusicBrainz Album Release Country | MusicBrainz/Album Release Country |
| script | TXXX:SCRIPT | same | SCRIPT | ----:com.apple.iTunes:SCRIPT | WM/Script |
| artists (multi-valued) | TXXX:ARTISTS | same | ARTISTS | ----:com.apple.iTunes:ARTISTS | — |
| embedded cover | APIC | APIC | METADATA_BLOCK_PICTURE (Ogg/Opus, base64 FLAC picture block); FLAC uses native PICTURE block | covr (JPEG/PNG only [P]) | WM/Picture |

Key gotchas (all [P] from Picard source):
- **Naming trap**: the Vorbis/MP4/ASF field literally called "Track Id" (`MUSICBRAINZ_TRACKID`, `MusicBrainz Track Id`) holds the *recording* MBID; the release-track MBID is `MUSICBRAINZ_RELEASETRACKID` / `MusicBrainz Release Track Id`; in ID3 the recording MBID lives in a `UFID` frame with owner `http://musicbrainz.org`, not in TXXX. Navidrome's and Jellyfin's readers both follow exactly this convention (navidrome `resources/mappings.yaml` [P]: `musicbrainz_recordingid` aliases `ufid:http://musicbrainz.org, musicbrainz_trackid, ----:com.apple.itunes:musicbrainz track id`; Jellyfin AudioFileProber.cs [P]: `MusicBrainzRecording` from `MUSICBRAINZ_TRACKID`/UFID, `MusicBrainzTrack` from `MUSICBRAINZ_RELEASETRACKID`).
- **ID3v2.3 vs 2.4**: v2.3 has no TDRC/TDOR; Picard writes TYER(+TDAT)/TORY and truncates non-full dates (`v[:4] if len(v) < 10`), and joins multi-values with a configurable separator (`id3v23_join_with`, default "/"). v2.4 keeps full ISO dates and native multi-values but some players (older iTunes/car stereos) prefer v2.3. Plex reads both. Recommend writing **ID3v2.4 with UTF-8** by default, with a v2.3 option.
- MP4 has no originaldate atom (Picard has none [P]); store it as a freeform `----:com.apple.iTunes:originaldate` if wanted (non-standard).
- `compilation` is a flag (TCMP "1", cpil true, COMPILATION=1); Plex does *not* read TCMP ([T], forum) — Plex uses Album Artist = "Various Artists" instead (5.1).
- Opus: ReplayGain tags are not the standard; use `R128_TRACK_GAIN` (see 6). Picard allows `r128_*` only for Vorbis-comment formats [P].
- Cover art in Ogg/Opus goes in `METADATA_BLOCK_PICTURE` (base64 FLAC PICTURE block) [P]; MP4 `covr` accepts only JPEG/PNG [P].

### 4.2 Recommended tag payload for one downloaded song

Minimum: title, artist, artists, albumartist, album, tracknumber(/total), discnumber(/total), date, originaldate/originalyear, genre (optional), isrc, musicbrainz_recordingid, musicbrainz_trackid, musicbrainz_albumid, musicbrainz_releasegroupid, musicbrainz_artistid, musicbrainz_albumartistid, releasetype, releasestatus, acoustid_id (+ optionally acoustid_fingerprint), compilation when Various Artists, embedded front cover (≤ ~1200 px JPEG), lyrics (USLT/LYRICS unsynced; synced go to a `.lrc` sidecar), ReplayGain (or R128 for Opus) if the user enables loudness scanning. Everything above is expressible in all four formats except originaldate on MP4 and r128 outside Opus.

### 4.3 Tag libraries by language (read + write across MP3/FLAC/M4A/Opus)

- **Python — mutagen** (quodlibet/mutagen README [P]): "ASF, FLAC, MP4, Monkey's Audio, MP3, Musepack, Ogg Opus, Ogg FLAC, Ogg Speex, Ogg Theora, Ogg Vorbis, True Audio, WavPack, OptimFROG, and AIFF"; full ID3v2.2/2.3/2.4 read, v2.3/2.4 write; APEv2; Python 3.10+. MP4 freeform keys `----:mean:name` [T]. Reference implementation used by Picard/beets. **Solid.**
- **Python — music-tag**: thin wrapper over mutagen [T]. 
- **.NET — ATL (z440.atl.core)** (Zeugma440/atldotnet README [P]): "reads ID3v2.2, ID3v2.3 and ID3v2.4 tags, but only writes ID3v2.3 tags and ID3v2.4"; read/write Vorbis comments in OGG, OPUS, FLAC; MP4 atoms for M4A; embedded pictures; "Unsynchronized and synchronized lyrics using the LRC, SRT or ID3v2 format"; .NET Standard 2.1 / .NET 6+. **Solid, fully managed.** TagLib-Sharp (mono/taglib-sharp [P]) also covers mp3/flac/m4a/ogg/opus ("aac, aiff, ape, dsf, flac, m4a, ... mp3, ogg, oga, wav, wma, wv") and is "API stable" but slower-moving.
- **Go**: dhowden/tag is **read-only** [P]; bogem/id3v2 = ID3v2.3/2.4 only (MP3) [P]; go-flac/go-flac (+flacpicture, flacvorbis) = FLAC blocks only [P]. Full read/write across all four: **deluan/go-taglib** (TagLib compiled to WASM run via wazero — "portable Go audio metadata read/write", no cgo [P/T]; used by Navidrome) and the newer pure-Go **cabbagekobe/tunetag** [P] ("MP3, FLAC, MP4 / M4A, WAV, AIFF / AIFC, Ogg Vorbis / Opus, APEv2, raw AAC, and ASF / WMA" read and write, "no cgo, no bundled WASM", "Approaching feature-complete for v1"). Go is now viable; go-taglib is the battle-tested choice.
- **Node**: music-metadata is **read-only** [P] (but reads everything incl. LRC/SYLT/USLT); node-id3 is MP3/ID3 only (has TXXX/UFID/USLT/APIC frames [P]); **taglib-wasm** (CharlesWiltgen/taglib-wasm [P]) gives read/write for mp3, m4a, flac, ogg, opus, wav in Node/Deno/Bun/browsers with a raw-frame "escape hatch" for TXXX/UFID; music-tag-native (napi-rs + lofty) [T]. Node needs a WASM/native bridge for full write support.
- **Rust — lofty** (Serial-ATA/lofty-rs README [P]): read/write ID3v2 (MP3, AIFF, WAV), Vorbis Comments (FLAC, Ogg Vorbis, Opus, Speex), iTunes-style ilst (MP4), APE; only exotic combos (ID3v2 inside FLAC/APE) are read-only. **Solid.**

Assessment: Python (mutagen), Rust (lofty) and .NET (ATL) have first-class native read+write across all four formats; Go and Node are fine via TagLib-in-WASM (go-taglib / taglib-wasm) or the young pure-Go tunetag.

---

## 5. Plex Music / Plexamp library requirements

### 5.1 Folder layout, singles, compilations, tags vs filenames

Official "Adding Music Media From Folders" (https://support.plex.tv/articles/200265296-adding-music-media-from-folders/ [S]): "Content should have each artist in their own directory, with each album as a separate subdirectory within it: `Music/ArtistName/AlbumName/TrackNumber - TrackName.ext`"; multi-disc: "prepend the disc number to the front of the track number, so track two on disc three would be `302 - TrackName.ext`", and "it is important for multi-disc albums to have the correct disc number set in the embedded tags". The library's content location is the `/Music` root.

"Identifying Music Media Using Embedded Metadata" (https://support.plex.tv/articles/200381093-identifying-music-media-using-embedded-metadata/ [S]): "If your content already has (accurate) embedded metadata, then Plex will use that information to help matching in a default install"; "Plex will expect that you have embedded metadata tags for Track, Album, and Artist (and potentially Album Artist) for each track"; "the Album Title tag must be identical in all files for the album, and the Album Artist tag must be identical in all files for the album. For albums with several artists, the Album Artist tag should be set to 'Various Artists'." Third-party naming guide (Jziadi README [P]) adds: "If the Album Artist tag is not present, then Plex will try to treat the file as though it is a part of a Various Artists album", and gives the tagless fallback layout `Music/ArtistName - AlbumName/TrackNumber - TrackName.ext`.

So: **Plex Music groups by embedded tags first** (Album Artist + Album, with track/disc numbers); folder/file names are the fallback when tags are missing. Community observation (Plex forum, [S]): without track numbers Plex may create "one new album per song". The Plex scanner does not honour the iTunes `TCMP` flag [T]; compilations are recognised only via `Album Artist = Various Artists`.

Singles: Plex has no special "single" file layout — a single is just an album folder with one file. In the Plex Music agent's metadata, albums carry a type/subformat (e.g. `single`, `ep`, `album;compilation`, `album;live`, `album;demo`) and Plexamp's artist page groups `single`/`ep` under **"Singles & EPs"**, `album;compilation` under "Compilations", `album;live` under "Live Albums" [S — Plex forum/metadata discussion]. That classification comes from the online match (MusicBrainz release-group types via the Plex Music agent), not from a local tag, so a library that is *only* matched from local tags will show every single as an "album". Verified community pain points: many one-track albums clutter the album grid; recommended workarounds seen in threads [T]: (a) keep the real single/album name and rely on Plex Music matching so they land in "Singles & EPs"; (b) tag an artist's loose tracks as album "Singles" (or "<Artist> – Singles") with `albumartist=<artist>`, sequential track numbers, so they collapse into one pseudo-album per artist; (c) use playlists/collections instead of albums. No official Plex guidance on singles was found [U].

### 5.2 Agents, MusicBrainz IDs, "Prefer local metadata"

- Agents (https://support.plex.tv/articles/200241558-agents/ [S]): "Plex Music is the default agent for music libraries and pulls in data from a variety of sources, including AllMusic and MusicBrainz data"; "Personal Media Artists/Albums agent is designed to recognize albums that have not been released commercially ... Metadata embedded in most music file formats will be read and used if Local Media Assets is enabled." **Local Media Assets** must be enabled (and high in the agent order) for embedded tags, sidecar art and lyrics to be used [S].
- Library advanced options (Upgrading Music Libraries / Library articles [S]): **"Prefer local metadata"** ("prioritizes local embedded tags over online information, though you should only enable this preference if you absolutely know that you need to do so"); **Genres**: None / Plex Music (default) / Embedded Tags; **Album Art**: Plex Music Only / Local Files Only / Both (default); "Popular tracks"; "Sonic analysis" (5.6). There is no equivalent local-only switch for *artist* art [S, forum]. Note: the older per-agent "Use embedded tags"-style checkboxes belonged to the legacy agent system; the current (2019+) "Plex Music" library exposes the options above [S].
- MusicBrainz IDs: Plex's **Fix Match** accepts an MBID: "When matching an Artist, enter the Artist's MBID in the 'Title' field. To match an Album, enter the MBID for the Release (not the Release Group) in the 'Album' field." (https://support.plex.tv/articles/correcting-your-music-content-matches/ [S]). Whether the scanner/agent *automatically* uses embedded `MUSICBRAINZ_ALBUMID` etc. is **not officially documented**; forum threads ask for it (e.g. "Plex Music Scanner & Matching with Embedded Musicbrainz Tags", "Support Musicbrainz IDs in .plexmatch files for audio" — the latter a feature request implying `.plexmatch` does not cover music today) [S/T]. One detailed third-party write-up (nailz1000/music-library-rescue, [P] read) claims that with "Prefer local metadata" on, Plex uses the embedded MusicBrainz Album ID as a grouping key (conflicting IDs within a folder split albums; "clear it. Plex then falls back to album-name + artist") and compares the `date` tag "as a whole string" (so `2008` vs `2008-09-29` split an album). Treat as **[T, plausible, unverified]**. Design rule: write consistent MB IDs and identical date strings across every file you place in the same album folder; this is harmless if Plex ignores them and helpful if it does not.

### 5.3 Artwork

Official "Adding Local Lyrics and Artwork for Music files" (https://support.plex.tv/articles/215916117-adding-local-lyrics/ [S]): local artwork can come from embedded art or from image files in the album folder such as `cover.jpg` / `folder.jpg`; artist-level art goes in the artist folder ("Artist-related artwork should be placed in the 'artist' folder, while album-related artwork goes in the 'album' folder"). Filenames repeatedly reported as working [S/T]: album `cover.jpg`, `folder.jpg` (also `poster.jpg`), artist `artist.jpg` (plus `folder.jpg`/`poster.jpg`; one guide lists `artist-poster.jpg`/`artist-background.jpg` — [U]). Requires Local Media Assets; Album Art option must be "Local Files Only" or "Both". Plex caches art; replacing a file may need a metadata refresh [T]. Sizes: no official limits found [U]; community/Plex UI conventions are square ≥ 1000 px for covers and 16:9 (e.g. 1920×1080) for artist backgrounds [S]. Embedded front cover in every file is the most robust option for a one-file-per-folder library; also write `cover.jpg` next to it.

### 5.4 Lyrics

Same article [S]: Plex reads sidecar lyrics "in the same directory as the corresponding music track ... named identically, aside from the file extension", formats **`.lrc`** (timed) and **`.txt`** (plain), e.g. `01 - Come With Me Tonight.mp3` + `.lrc`. LyricFind (https://support.plex.tv/articles/215238778-automatic-lyrics-from-lyricfind/ [S]) supplies lyrics online for Plex Pass. Plexamp shows synced lyrics; forum threads ("Plexamp Support for Local Lyrics", "Lyrics priority") report Plexamp preferring LyricFind and sometimes not showing local LRC files — behaviour changed across versions [T]. Embedded USLT/LYRICS are read by Local Media Assets as unsynced lyrics [S]. Recommendation: write both an embedded unsynced lyric tag and a `.lrc` sidecar from LRCLIB.

### 5.5 Playlists (.m3u import) and how the app can push playlists

Plex has an undocumented but stable endpoint used by python-plexapi [P, `plexapi/playlist.py`]: `POST /playlists/upload?sectionID={library section key}&path={server-side absolute path to .m3u}` — docstring: "Can only create playlists from m3u files in a music library" and it overwrites a playlist previously created from that same file. The m3u must reference files by **paths as the Plex server sees them** (same mount paths as the library) [T, multiple tools]. Alternative supported route: create/modify playlists via `POST /playlists?type=audio&title=&uri=server://{machineId}/com.plexapp.plugins.library/library/metadata/{ratingKeys}` and `PUT /playlists/{id}/items?uri=...` (python-plexapi `Playlist.create`/`addItems`) — requires looking up each track's ratingKey (search `/library/sections/{id}/all?type=10&title=` or by file path via `/library/sections/{id}/all?...`). Both need `X-Plex-Token`. Plex has no UI "scan playlists folder" for music m3u files [U — none found].

### 5.6 Library scan trigger and token

"Plex Media Server URL Commands" (https://support.plex.tv/articles/201638786-plex-media-server-url-commands/ [S]) and python-plexapi [P]: `GET http://server:32400/library/sections/{id}/refresh?path=<URL-encoded folder>&X-Plex-Token=<token>` scans only that folder ("This command will return immediately, but the scan will still be running"); omit `path` for a full scan; `?force=1` re-downloads metadata; `PUT /library/sections/{id}/analyze` runs analysis. Lidarr's PlexServerProxy.cs [P] does exactly this: `GET library/sections` (filter type "artist") then `GET library/sections/{sectionId}/refresh?path=`, sending `X-Plex-Token`, `X-Plex-Client-Identifier`, `X-Plex-Product`, `X-Plex-Version`, `X-Plex-Platform`, `X-Plex-Device-Name`; 401 → auth error. Token: https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/ [S] (browser-derived tokens are temporary; apps should obtain a token via the plex.tv PIN/OAuth flow with a stable `X-Plex-Client-Identifier`). The `path` must be the path **as mounted on the Plex server** — expose a path-mapping setting like Lidarr's "Path Mappings".

### 5.7 Formats and transcoding

"What media formats are supported?" (https://support.plex.tv/articles/204377253-what-media-formats-are-supported/ [S]): music containers `MP3, M4A, MP4 (audio-only), FLAC, OGG, WAV, WV, APE` (+AIFF), codecs lossy `MP3, AAC, Opus, OGG Vorbis, WMA, AC3, E-AC3, DTS, MP2`, lossless `FLAC, ALAC, WAV (PCM), AIFF, WavPack, APE`. Whether a client direct-plays depends on the app: the server transcodes when the client can't decode the codec/container or the user's streaming-quality cap is lower than the bitrate [S]. Plexamp specifics: Plexamp direct-plays FLAC/ALAC/MP3/AAC on all platforms (Plexamp is built on its own player) and transcodes to Opus/AAC when the user sets a mobile quality cap [T]; `.opus` files in `.ogg`/`.opus` containers are listed as supported server-side, but a Plex Labs thread ("Opus transcode... and then whats best at Plexamp/Android client?") shows Opus is mainly used as a *transcode target* and direct-play of Opus sources is device-dependent — **[U] treat Opus sources as "may transcode"**. `.webm` audio is not a music-library container [U, not listed]. Safe choices for a Plexamp library: FLAC (lossless) and MP3/AAC-in-M4A (lossy).

### 5.8 Plexamp features that depend on server-side analysis (not tags)

- **Sonic Analysis** (https://support.plex.tv/articles/sonic-analysis-music/ [S]): "Your Plex Media Server can perform a 'sonic analysis' of your local music files to catalog detailed characteristics about the actual music itself ... sonically similar artists/albums/tracks, play a Track Radio, or even suggest specific mixes for you"; **Plex Pass required for the server admin**; enable "Settings > Server > Library > Analyze audio tracks for sonic features" (scheduled task, or continuous); "can take days" for big libraries, minutes for a new album. Powers Sonic Adventure ("pick a start track and a destination track"), Guest DJs (DJ Stretch/Contempo/Groupie), Mixes For You, Track/Artist Radio, Mood Radio [S/T]. Runs on the server against whatever formats the server can decode; no per-format restriction was found [U].
- **Loudness analysis / Loudness leveling** (https://support.plex.tv/articles/200289526-library/ [S]): "Letting the server analyze your audio files for loudness information allows apps to level (normalize) the loudness and perform smart transitions between tracks"; "Enable Loudness Analysis is a premium feature and requires an active Plex Pass"; Plex uses EBU R128 [S]. **Plex/Plexamp do not read ReplayGain tags** (forum "Plexamp ignoring replay gain metadata?", "Does Plex support ReplayGain?" [S/T]); Plexamp uses "server-computed ReplayGain 2 values with -18 LUFS reference" [T]. A Sept-2026 report says the refreshed Plexamp "can now perform loudness analysis directly on a device when a Plex server has not already analyzed a track" [T, thedesk.net; not fetched]. **Sweet Fades** (analysis-driven crossfade points), gapless playback (album playback), crossfade, preamp/EQ, "silence compression" are Plexamp player features; Sweet Fades and loudness leveling need Plex Pass [S/T].
- Moods/styles/genres shown in Plexamp come from the Plex Music agent's online match (AllMusic-style moods), not from file tags (only `genre` can be forced to embedded tags) [S].

**What the app must do for Plex to "match perfectly"**: (1) `Artist/Album/NN - Title.ext` layout with a real album folder per release (even for one track); (2) consistent tags — `albumartist`, `album`, `title`, `artist`, `tracknumber`, `discnumber`, `date` identical across files of the same album; `albumartist=Various Artists` for compilations; (3) MB IDs + ISRC embedded (helps future-proofing and Fix Match); (4) embedded front cover + `cover.jpg`; (5) `.lrc` sidecar; (6) trigger `/library/sections/{id}/refresh?path=` for the album folder; (7) optionally push an m3u via `/playlists/upload` for the wanted-list/playlist. ReplayGain tags are irrelevant to Plex (but useful for other players) — Plex's own loudness/sonic analysis runs server-side and needs Plex Pass.

---

## 6. Loudness / ReplayGain (short)

- Tools: **rsgain** (complexlogic/rsgain [P]): FLAC, MP3, M4A, OGG, Opus, WAV, WMA, APE, WavPack etc.; writes `REPLAYGAIN_TRACK_GAIN`/`_PEAK` (+ album) — ID3 TXXX for MP3, freeform `----:com.apple.iTunes:REPLAYGAIN_*` for MP4, Vorbis comments for FLAC/OGG; Opus modes `-o d|r|s|t|a` ("d: Write standard ReplayGain tags, set header output gain to 0", "r: Write R128_*_GAIN tags", "s: same as r plus target forced to -23 LUFS", "t/a: write track/album gain to header output gain"); clipping protection `-c p` (default, positive gains only); `rsgain easy /music` scans a library with per-format defaults. **loudgain** (Moonbase59/loudgain [P]): same tag set at "-18 LUFS, 'dB' units, uppercase tags" (RG 2.0); Opus "nailed to -23 LUFS by design" with `R128_TRACK_GAIN` in "ASCII Q7.8" and no unit; `-s e` adds `REPLAYGAIN_TRACK_RANGE`/`REPLAYGAIN_REFERENCE_LOUDNESS`; MP3 uses TXXX not RVA2. **ffmpeg**: `ebur128` filter measures integrated loudness (LUFS), LRA (LU), true/sample peak (dBFS), default `target=-23` [P, f_ebur128.c]; `loudnorm` normalises (defaults `I=-24`, `LRA=7`, `TP=-2`; two-pass with `measured_I/LRA/TP/thresh`, `print_format=json`) [P, af_loudnorm.c] — loudnorm *changes audio*, so for a tagging app use `ebur128` to measure and write gain = target − measured (RG2 target −18 LUFS; Opus −23 LUFS).
- Opus rules (RFC 7845 §5.2.1, not fetchable; corroborated by rsgain/loudgain/zoog [P]): header "output gain" is always applied by decoders; `R128_TRACK_GAIN`/`R128_ALBUM_GAIN` are Q7.8 fixed-point dB relative to −23 LUFS and "MUST be applied in addition to the output gain"; ReplayGain tags "are used in Ogg Vorbis, but not Opus". Players converting Opus R128 to RG2 add +5 dB.
- Who honours what: **Plex/Plexamp: no** (server analysis only, 5.8). **Navidrome: yes** — reads `replaygain_track_gain/peak`, `replaygain_album_gain/peak` (TXXX / Vorbis / `----:com.apple.itunes:` aliases) and `r128_track_gain`/`r128_album_gain` (`resources/mappings.yaml` [P]) and applies them in its web player/Subsonic clients [T]. **Jellyfin: yes** — `AudioFileProber.cs` [P] reads `REPLAYGAIN_TRACK_GAIN` into `audio.NormalizationGain` and `REPLAYGAIN_ALBUM_GAIN` as album fallback (strips "dB"); no R128/LUFS scan in that file. So writing RG2 tags (and R128 for Opus) costs little and helps every non-Plex player; make it optional.

---

## 7. Naming conventions to support as templates

### 7.1 Lidarr (Servarr/Wiki `lidarr/naming-guide.md` + Lidarr source [P])

Defaults (`src/NzbDrone.Core/Organizer/NamingConfig.cs` [P]): Standard Track Format `{Album Title} ({Release Year})/{Artist Name} - {Album Title} - {track:00} - {Track Title}`; Multi-Disc Track Format `{Album Title} ({Release Year})/{Medium Format} {medium:00}/{Artist Name} - {Album Title} - {track:00} - {Track Title}`; Artist Folder Format `{Artist Name}`; `RenameTracks=false`, `ReplaceIllegalCharacters=true`. There is no album-folder template; the album folder is built into the track format.

Tokens (naming guide + `frontend/.../NamingModal.js` [P]):
- Artist: `{Artist Name}`, `{Artist NameThe}`, `{Artist CleanName}`, `{Artist CleanNameThe}`, `{Artist NameFirstCharacter}`, `{Artist Disambiguation}`, `{Artist Genre}`, `{Artist MbId}`.
- Album: `{Album Title}`, `{Album TitleThe}`, `{Album CleanTitle}`, `{Album CleanTitleThe}`, `{Album Type}` ("Album, Single, EP, Compilation, Live, Remix, Soundtrack"), `{Album Disambiguation}`, `{Album Genre}`, `{Album MbId}`, `{Release Year}`.
- Track: `{Track Title}`, `{Track CleanTitle}`, `{Track ArtistName}`, `{Track ArtistCleanName}`, `{Track ArtistNameThe}`, `{Track ArtistCleanNameThe}`, `{Track ArtistMbId}`, `{track:0}`, `{track:00}`, `{track:000}`.
- Medium: `{medium:0}`, `{medium:00}`, `{Medium Name}`, `{Medium Format}`.
- Quality/media: `{Quality Full}`, `{Quality Title}`, `{Quality Proper}`, `{MediaInfo AudioCodec}`, `{MediaInfo AudioChannels}`, `{MediaInfo AudioBitRate}`, `{MediaInfo AudioBitsPerSample}`, `{MediaInfo AudioSampleRate}`.
- Other: `{Release Group}`, `{Custom Formats}`, `{Custom Format:Name}`, `{Original Title}`, `{Original Filename}`; truncation `{Album Title:150}`; casing/separator modifiers as in other *arrs (`{Artist.Name}`, `{artist_name}` etc. [T]).

### 7.2 Picard (`picard/const/defaults.py` [P])

Default file naming script:
```
$if2(%albumartist%,%artist%)/
$if(%albumartist%,%album%/,)
$if($gt(%totaldiscs%,1),$if($gt(%totaldiscs%,9),$num(%discnumber%,2),%discnumber%)-,)
$if($and(%albumartist%,%tracknumber%),$num(%tracknumber%,2) ,)
$if(%_multiartist%,%artist% - ,)
%title%
```
i.e. `AlbumArtist/Album/[D-]NN Title` and, for files without an album artist (non-album tracks), just `Artist/Title`. Variables are `%tag%` (any tag from 4.1 plus hidden `%_multiartist%`, `%_extension%` ...), functions `$if`, `$if2`, `$num`, `$gt`, `$and`, `$replace`, `$left`, `$upper`, etc. Windows-incompatible characters `* : < > ? | "` are replaced with `_` (`DEFAULT_WIN_COMPAT_REPLACEMENTS`), cover file default name `cover` (`DEFAULT_COVER_IMAGE_FILENAME`), duplicate numbering `{title} ({count})`.

### 7.3 beets (`docs/reference/config.rst`, `pathformat.rst` [P])

Defaults: `default: $albumartist/$album%aunique{}/$track $title`; `singleton: Non-Album/$artist/$title`; `comp: Compilations/$album%aunique{}/$track $title` — `singleton`/`comp` are shorthand for queries `singleton:true` / `comp:true`. Fields: `$title, $artist, $albumartist, $album, $track, $disc, $year, $genre, $length, $bitrate, $format, $mb_trackid, $mb_albumid, $mb_artistid, $mb_releasetrackid, $mb_releasegroupid, $isrc ...`; functions `%lower{}`, `%upper{}`, `%title{}`, `%left{}`, `%right{}`, `%if{cond,true,false}`, `%asciify{}`, `%aunique{}`, `%first{}`, `%ifdef{}`; `$$` for a literal `$`. Default `replace` rules: `[\\/]`→`_`, `^\.`→`_`, `[\x00-\x1f]`→`_`, `[<>:"\?\*\|]`→`_`, `\.$`→`_`, `\s+$`→``, `^\s+`→``, `^-`→`_`.

### 7.4 Suggested template language for the new app

Offer Lidarr-style `{Token}` syntax (familiar to *arr users) with a superset of tokens, and ship four presets matching the required layouts:

- Plexamp style: `{Artist Name}/{Album Title}/{track:00} - {Track Title}` (multi-disc: `{medium:0}{track:00} - {Track Title}` per Plex's "302" convention, or `Disc {medium:0}/…`).
- Flat: `{Artist Name} - {Track Title}` (optionally `{Artist Name} - {Track Title} [{Release Year}]`).
- Per artist: `{Artist Name}/{Artist Name} - {Track Title}`.
- Per artist + album: `{Artist Name}/{Album Title} ({Release Year})/{track:00} - {Track Title}`.

Extra tokens worth adding for single songs: `{Recording MbId}`, `{Release MbId}`, `{ReleaseGroup MbId}`, `{ISRC}`, `{Album Type}` (Single/EP/Album), `{Album Artist Name}` vs `{Track ArtistName}`, `{Release Year}`/`{Original Year}`, `{Duration}`, `{MediaInfo AudioCodec}`; keep beets-style `%aunique{}`-like disambiguation for same-titled albums; apply Picard/beets replacement rules for illegal characters; provide `The`-suffix and `CleanName` variants like Lidarr.

---

## 8. Design implications (condensed)

1. Identity = MusicBrainz **recording MBID**; store alongside it the chosen **release MBID + release-group MBID + release-track MBID + ISRC(s)**, because tagging/placement/Plex matching need a release context and Plex Fix Match wants the *Release* MBID.
2. Search flow: text → MB recording search (`recording: artist: dur: primarytype:album status:official`) → show disambiguation/length/types → user picks recording (+ release). ISRC from Spotify/Deezer imports → `/ws/2/isrc/{isrc}` → recording(s).
3. Verification: `fpcalc -json` → AcoustID `lookup?meta=recordingids` → accept if wanted recording MBID present with score ≥ configurable threshold (default ~0.7; review 0.5–0.7) and duration within tolerance; remasters pass, edits/remixes fail.
4. Tag with the Picard convention (4.1), ID3v2.4 by default, embed cover + MB IDs + ISRC + AcoustID id; write `.lrc` from LRCLIB and `cover.jpg`; optional RG2/R128 via ffmpeg `ebur128` (Plex ignores it; Navidrome/Jellyfin use it).
5. Plex integration: Artist/Album/NN - Title layout, consistent album tags, `albumartist=Various Artists` for compilations, path mapping, `refresh?path=` after each import, optional m3u push via `/playlists/upload`; document that singles will appear as one-track albums unless Plex Music matches them (then they show under "Singles & EPs"), and offer the "<Artist> – Singles" pseudo-album option.
6. External APIs: cache everything by MBID with TTLs (Lidarr-style proxy), mandatory descriptive User-Agent, 1 req/s to MusicBrainz, 3 req/s to AcoustID, 20/min to iTunes, honour 429/Retry-After (LRCLIB, ListenBrainz, Spotify); treat Spotify (5-user dev mode, Premium) and Deezer (no new tokens for individuals) as optional owner-only importers, and prefer ListenBrainz/Last.fm/LRCLIB/CAA/fanart.tv/TheAudioDB which remain open.

---

## Appendix A. Source list

Primary [P] (fetched):
- https://raw.githubusercontent.com/alastair/python-musicbrainzngs/master/musicbrainzngs/musicbrainz.py (MB includes, search fields, isrc endpoint, rate limit, UA)
- https://github.com/metabrainz/musicbrainz-docker (README: mirror/replication)
- https://github.com/metabrainz/coverart_redirect (README)
- https://github.com/acoustid/chromaprint/blob/master/src/cmd/fpcalc.cpp ; README
- https://github.com/acoustid/acoustid-server/blob/master/acoustid/api/v2/__init__.py ; acoustid/api/errors.py ; acoustid/config.py
- https://github.com/beetbox/pyacoustid (README) ; https://github.com/beetbox/beets docs/plugins/chroma.rst, docs/reference/config.rst, docs/reference/pathformat.rst ; PR #7045
- https://github.com/wo80/AcoustID.NET ; https://github.com/alexgorbatchev/gochromaprint ; https://github.com/parshap/node-fpcalc
- https://github.com/metabrainz/picard-docs/blob/main/appendices/tag_mapping.rst (rendered at https://picard-docs.musicbrainz.org/en/appendices/tag_mapping.html)
- https://github.com/metabrainz/picard picard/formats/id3.py, mp4.py, vorbis.py, picard/const/defaults.py
- https://github.com/navidrome/navidrome/blob/master/resources/mappings.yaml ; https://github.com/jellyfin/jellyfin MediaBrowser.Providers/MediaInfo/AudioFileProber.cs ; https://github.com/jellyfin/jellyfin.org docs/general/server/media/music.md
- https://github.com/quodlibet/mutagen ; https://github.com/Zeugma440/atldotnet ; https://github.com/mono/taglib-sharp ; https://github.com/Serial-ATA/lofty-rs ; https://github.com/cabbagekobe/tunetag ; https://github.com/deluan/go-taglib ; https://github.com/dhowden/tag ; https://github.com/bogem/id3v2 ; https://github.com/go-flac/go-flac ; https://github.com/Borewit/music-metadata ; https://github.com/Zazama/node-id3 ; https://github.com/CharlesWiltgen/taglib-wasm
- https://github.com/Lidarr/Lidarr src/NzbDrone.Core/MetadataSource/SkyHook/SkyHookProxy.cs, src/NzbDrone.Common/Cloud/LidarrCloudRequestBuilder.cs, src/NzbDrone.Core/Notifications/Plex/Server/PlexServerProxy.cs, src/NzbDrone.Core/Organizer/NamingConfig.cs, frontend/src/Settings/MediaManagement/Naming/NamingModal.js ; https://github.com/Servarr/Wiki lidarr/naming-guide.md, lidarr/settings.md ; https://github.com/Lidarr/LidarrAPI.Metadata (README, lidarrmetadata/provider.py, api.py)
- https://github.com/pkkid/python-plexapi plexapi/library.py (update/refresh/analyze), plexapi/playlist.py (/playlists/upload)
- https://github.com/complexlogic/rsgain ; https://github.com/Moonbase59/loudgain ; https://github.com/FrancisRussell/zoog ; https://github.com/FFmpeg/FFmpeg libavfilter/af_loudnorm.c, libavfilter/f_ebur128.c
- https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/ (Searching.html, LookupExamples.html, UnderstandingSearchResults.html)
- https://github.com/browniebroke/deezer-python src/deezer/resources/track.py, src/deezer/client.py
- https://github.com/metabrainz/listenbrainz-server docs/users/api/index.rst
- https://github.com/fanart-tv/fanart.tv-api (README) ; https://github.com/notigorwastaken/lrclib-api ; https://github.com/Dr-Blank/lrclibapi
- https://github.com/Osasuwu/like-current-song/issues/122 (quotes Spotify quota-modes doc)
- https://github.com/Jziadi/Plex-File-Naming-Folder-Structure ; https://github.com/nailz1000/music-library-rescue docs/01-how-plex-groups-music.md ; https://github.com/schellenberg/lyric-grabber-for-plex ; https://github.com/DocDocDocDocDoc/PlexPlaylistImporter

Official pages cited via search extracts [S] (blocked in sandbox):
- MusicBrainz: https://musicbrainz.org/doc/MusicBrainz_API , /doc/MusicBrainz_API/Search , /doc/MusicBrainz_API/Search/RecordingSearch , /doc/MusicBrainz_API/Rate_Limiting , /doc/Recording , /doc/Track , /doc/Release , /doc/Release_Group , /doc/Release_Group/Type , /doc/Work , /doc/Style/Recording , /doc/Cover_Art_Archive/API , /doc/Live_Data_Feed
- AcoustID: https://acoustid.org/webservice , https://acoustid.org/chromaprint
- Plex: https://support.plex.tv/articles/200265296-adding-music-media-from-folders/ , /articles/200381093-identifying-music-media-using-embedded-metadata/ , /articles/215916117-adding-local-lyrics/ , /articles/215238778-automatic-lyrics-from-lyricfind/ , /articles/200241558-agents/ , /articles/upgrade-music-libraries-new-metadata-system/ , /articles/correcting-your-music-content-matches/ , /articles/sonic-analysis-music/ , /articles/200289526-library/ , /articles/204377253-what-media-formats-are-supported/ , /articles/201638786-plex-media-server-url-commands/ , /articles/204059436-finding-an-authentication-token-x-plex-token/ ; Plex forum threads: /t/plex-music-scanner-matching-with-embedded-musicbrainz-tags/865310 , /t/support-musicbrainz-ids-in-plexmatch-files-for-audio/885300 , /t/plexamp-ignoring-replay-gain-metadata/724442 , /t/does-plex-support-replaygain/220084 , /t/opus-transcode-and-then-whats-best-at-plexamp-android-client/905151 , /t/single-appears-in-album-section-after-scan/941958 , /t/multiple-groups-of-various-artists-is-there-a-way-to-consolidate/854851 , /t/plexamp-support-for-local-lyrics/910814
- Spotify: https://developer.spotify.com/blog/2024-11-27-changes-to-the-web-api , /blog/2025-04-15-updating-the-criteria-for-web-api-extended-access , /blog/2026-07-23-web-api-quota-updates , /documentation/web-api/concepts/quota-modes , /documentation/web-api/reference/get-users-saved-tracks ; https://techcrunch.com/2026/02/06/spotify-changes-developer-mode-api-to-require-premium-accounts-limits-test-users/
- Deezer: https://developers.deezer.com/api/track , https://support.deezer.com/hc/en-gb/articles/360011538897-Deezer-FAQs-For-Developers , https://en.deezercommunity.com/features-feedback-44/api-search-for-all-tracks-by-isrc-74109
- Last.fm: https://www.last.fm/api/tos , https://www.last.fm/api/show/track.getInfo ; Discogs: https://www.discogs.com/developers ; TheAudioDB: https://www.theaudiodb.com/free_music_api ; LRCLIB: https://lrclib.net/docs ; Genius: https://docs.genius.com/ , https://lyricsgenius.readthedocs.io/en/stable/how_it_works.html
- Picard: https://picard-docs.musicbrainz.org/en/tutorials/acoustid.html , https://picard-docs.musicbrainz.org/en/tutorials/naming_script.html ; Servarr: https://wiki.servarr.com/lidarr/faq , https://wiki.servarr.com/lidarr/settings#track-naming

Unverified / could not confirm [U]: exact beets/Picard numeric AcoustID score cutoffs; Plex automatic use of embedded MB IDs; official Plex artwork filename list and size limits; Plexamp Opus direct-play matrix; any Plex "playlists folder" scan; fanart.tv rate limits; LRCLIB licence wording; the reported Spotify search-limit reduction (50→10).

### 2.3 Verified 2026-09-29: fpcalc 1.6 and the AcoustID lookup

Sources: `github.com/acoustid/chromaprint` `src/cmd/fpcalc.cpp` (v1.6.0; v1.6.1 of 2026-07-28 is a decoder heap-overflow fix), `acoustid.org/webservice`, `acoustid-server` `acoustid/api/v2/__init__.py` + `api/errors.py`, `pyacoustid`.

- `fpcalc -json FILE` → `{"duration": 251.32, "fingerprint": "AQAD…"}` (duration `%.2f` seconds of the input as decoded; fingerprint compressed base64). `-length N` (default 120) caps from the **start**; there is **no offset option**, so the middle-window retry (MATCHING_ENGINE §6.5) pipes `ffmpeg -ss S -t 120 -i FILE -f wav -` into `fpcalc -json -` or uses `-chunk`. Errors go to stderr (`ERROR: …`), exit code 2 (3 when some output was produced).
- `lookup`: `client`, `duration` (**integer seconds**, truncated), `fingerprint`, `meta` (whitespace-separated: `recordingids`, `recordings`, `releasegroups`, `compress`), `format=json`; POST with a gzip body is recommended for long fingerprints; max **3 req/s**. OK: `{"status":"ok","results":[{"id":"<acoustid>","score":0.97,"recordings":[{"id":"<mbid>"}]}]}` (`meta=recordings` adds `title`, `duration`, `artists[{id,name}]`); no match → `results: []`; a result without recordings omits `recordings`. Errors: `{"status":"error","error":{"code":N,"message":"…"}}` — 2 missing parameter, 3 invalid fingerprint, 4 invalid API key, 8 invalid duration, 13 service unavailable (503), **14 rate limit (HTTP 429)**, 18 not found (404), 19 too large (413). No User-Agent requirement (a descriptive one is sent anyway).
