# YouTube / YouTube Music as a secondary single-song source — research notes

> **Name:** this document predates the rename of the project from *Compilarr* to **Wondarr** (2026-09-28, `docs/DECISIONS.md` build session 2 #10); it keeps the old name as a historical record.

Date: 2026-09-28 (re-verified 2026-10-05 for Phase 4; see §0.1). Prepared for the design doc of a Soulseek-first, single-song *arr-style app.
Every claim carries a URL. "Primary" = project source code, READMEs, wiki markdown, changelogs, PyPI metadata.
"Secondary" = third-party blogs/search snippets. Items marked **UNVERIFIED** could not be checked from this sandbox.

## 0.1 Re-verification for Phase 4 (2026-10-05, researcher agent)

Facts that changed or were pinned before the Phase 4 specs were written (all verified directly from source):

- **InnerTube search is keyless.** ytmusicapi's current `main` sends **no API key and no `X-Goog-Api-Key` header for unauthenticated search** — the key constant (`AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30`, `constants.py`) is appended only for browser-cookie auth. The old WEB_REMIX key in §2 below (`…JCCTLW7cUh-AqyM`) is stale. Sources: https://raw.githubusercontent.com/sigma67/ytmusicapi/main/ytmusicapi/constants.py , `helpers.py`, `ytmusic.py`.
- **Request shape:** `POST https://music.youtube.com/youtubei/v1/search?alt=json`, body `{"context":{"client":{"clientName":"WEB_REMIX","clientVersion":"1.<YYYYMMDD>.01.00","hl":"en"},"user":{}},"query":…,"params":…}` — `clientVersion` is computed at runtime, not hardcoded. Headers: the Firefox 88 UA, `accept: */*`, `content-type: application/json`, `origin: https://music.youtube.com`, cookie `SOCS=CAI`; no Referer. The `X-Goog-Visitor-Id` ytmusicapi scrapes from the homepage is **not required for search**.
- **`params` constants (current):** songs = `EgWKAQIIAWoMEA4QChADEAQQCRAF`; videos = `EgWKAQIQAWoMEA4QChADEAQQCRAF`; songs + ignore_spelling = `EgWKAQIIAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D`. Source: `parsers/search.py`.
- **Response path:** `contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[]` → `musicShelfRenderer` (top result: `musicCardShelfRenderer`, videoId/videoType under `onTap`). Per item: videoId under `overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchEndpoint.videoId`; **musicVideoType at the same playNavigationEndpoint + `watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType`**; title = flexColumns[0] runs[0].text; artists = flexColumns[1] runs with a browseEndpoint; duration = the last run matching `^(\d+:)*\d+:\d+$`. **No `duration_seconds` in raw responses** — it is computed from the `M:SS` text. Sources: `mixins/search.py`, `parsers/search.py`, `navigation.py`.
- **Playlist browse (verified 2026-10-07 for Phase 6, fixtures under `tests/fixtures/ytmusic/`).** `POST https://music.youtube.com/youtubei/v1/browse?alt=json` with the same WEB_REMIX context and headers as search; the body is `{"context":…,"browseId":"VL<playlistId>"}` for the first page and `{"context":…,"continuation":"<token>"}` for every page after. Page 1 keeps the rows under `contents.twoColumnBrowseResultsRenderer.secondaryContents.sectionListRenderer.contents[0].musicPlaylistShelfRenderer.contents[]`; a continuation answer keeps them under `onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems[]`; in both, a trailing `continuationItemRenderer.continuationEndpoint.continuationCommand.token` names the next page. Per row (`musicResponsiveListItemRenderer`): title = flexColumns[0] runs; artist credit = flexColumns[1] runs joined, the runs with a `UC…` browseId being the artists; album = flexColumns[2] runs (often empty); duration = the fixedColumns[0] run as `M:SS` or `H:MM:SS`; videoId = `playlistItemData.videoId`. A row without a videoId is an unavailable video.
- **yt-dlp:** still 2026.08.19 (latest PyPI); Deno still the only-by-default runtime, min 2.3.0 (the pip `deno` extra pins `deno>=2.6.6`); `yt-dlp-ejs==0.8.0` in the default extra. Sources: https://pypi.org/pypi/yt-dlp/json , https://github.com/yt-dlp/yt-dlp/wiki/EJS .
- **bgutil PO-token provider:** v2.0.1 (2026-10-02), HTTP server still port 4416, custom URL via `--extractor-args "youtubepot-bgutilhttp:base_url=http://…"`. Source: https://pypi.org/pypi/bgutil-ytdlp-pot-provider/json .
- **Error strings (exact, for the taxonomy):** bot check = `"Sign in to confirm you're not a bot"` (stderr `ERROR: [youtube] <id>: Sign in to confirm you're not a bot. <hint>`); rate limit = `"This content isn't available, try again later"`; geo = `"The uploader has not made this video available in your country"`; age gate fatal = `"Login details are needed to download this content"` (warnings mention `age-restricted`); private/unavailable = `"This video is private"`, `"Video unavailable"`, `"This video does not exist"`. Exit codes: extraction failures → 1; cancelled → 101; usage → 2. Sources: `yt_dlp/extractor/youtube/_video.py`, `_base.py`, `yt_dlp/__init__.py`.
- **YoutubeDLSharp is stale:** NuGet 1.2.0, option set last synced to yt-dlp ~3 years ago, repo slow-moving → **not used**; Wondarr shells out to yt-dlp directly (`DECISIONS.md` build session 5 #1). Sources: https://api.nuget.org/v3-flatcontainer/youtubedlsharp/index.json , https://github.com/Bluegrams/YoutubeDLSharp .

---

## 0. How this was researched and what could NOT be reached

Reachable: `raw.githubusercontent.com` (all source/README/wiki markdown quoted below was pulled verbatim),
`github.com` pages via the fetch tool (issue *bodies*, release pages, `releases.atom` feeds), PyPI JSON API,
GitHub repository-search metadata (pushed_at, archived flags).

Blocked by the sandbox egress proxy (so quotes from these come from search-engine snippets or are marked UNVERIFIED):
`ytmusicapi.readthedocs.io`, `support.plex.tv`, `forums.plex.tv`, `www.plex.tv`, `www.youtube.com` (ToS),
`support.google.com` (YouTube Help), `en.wikipedia.org`, `rfc-editor.org`/`ietf.org`, `opus-codec.org`,
`wiki.xiph.org`, `wiki.hydrogenaud.io`, `eff.org`, `github.blog`, `wiki.servarr.com`, `hotio.dev`,
distributor FAQs (routenote, emubands, imusician, amuse, symdistro), `web.archive.org`, `r.jina.ai`, GitHub REST API
(so **issue-thread comments by yt-dlp maintainers were not readable**; only issue bodies were).

---

## 1. yt-dlp in 2026

### 1.1 Release cadence and platform requirements
- Latest release: **2026.08.19** (PyPI upload 2026-08-19; `requires_python >=3.10`). Sources: https://pypi.org/pypi/yt-dlp/json ,
  https://github.com/yt-dlp/yt-dlp/releases.atom (entries 2026.08.19, 2026.07.04, 2026.06.09).
- 2026 releases so far (from `Changelog.md`, https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/Changelog.md):
  2026.01.29, 01.31, 02.04, 02.21, 03.03, 03.13, 03.17, 06.09, 07.04, 08.19 (10 releases in ~8 months, bursty around YouTube breakage).
  2025 had 27 releases (01.12 … 12.08). Practical rule: **auto-update yt-dlp at least monthly; expect emergency releases.**
- Python: required >=3.10 since 2025.10.22 ("Python 3.9 has reached its end-of-life"); *recommended* 3.11 since 2026.07.04
  (https://github.com/yt-dlp/yt-dlp/releases/tag/2026.07.04).
- 2026 security items worth knowing (from release notes summaries, https://github.com/yt-dlp/yt-dlp/releases): `--write-link` sanitisation
  (CVE-2026-55404), cookie leak with curl downloader (CVE-2026-50019), aria2c manifest RCE (CVE-2026-50574, HLS/DASH via aria2c removed),
  2026.06.09 changed `--exec` placeholder conversion (`%(...)s` -> `%(...)q`). Keep yt-dlp current.

### 1.2 External JavaScript runtime (EJS) — required since 2025.11.12
- Timeline (primary, Changelog.md / issues):
  - 2025-09-23 announcement #14404: "the built-in JS interpreter will soon be insufficient … you'll need to have Deno (or another supported
    JavaScript runtime) installed to keep YouTube downloads working as normal … The JavaScript runtime requirement will only apply to
    downloading from YouTube." https://github.com/yt-dlp/yt-dlp/issues/14404
  - 2025.10.22 "stopgap release with a TEMPORARY partial fix … Some formats may still be unavailable, especially if cookies are passed".
  - **2025.11.12: "An external JavaScript runtime is now required for full YouTube support"** + "[Implement external n/sig solver] (#14157)".
    Announcement: https://github.com/yt-dlp/yt-dlp/issues/15012 ; wiki: https://github.com/yt-dlp/yt-dlp/wiki/EJS
- Runtimes (EJS wiki, current): **Deno (recommended, enabled by default, min 2.3.0 since 2026.06.09)**; Node (min 22, disabled by default);
  QuickJS / QuickJS-NG (disabled); Bun (deprecated, 1.2.11–1.3.14). "Only 'deno' is enabled by default … for security reasons."
  Deno "Code is run with restricted permissions (e.g, no file system or network access)"; Bun "No permission restrictions".
- Flags (README, verbatim): `--js-runtimes RUNTIME[:PATH]` ("Supported runtimes are (in order of priority …): deno, node, quickjs, bun.
  Only "deno" is enabled by default"), `--no-js-runtimes`, `--remote-components ejs:npm|ejs:github` ("currently not needed if you are using
  an official executable or have the requisite version of the yt-dlp-ejs package installed"), extractor arg `youtube-ejs:jitless=true`.
  Python API keys: `js_runtimes` (dict, e.g. `{'deno': {'path': '/path/to/deno'}}`), `remote_components` (list) — YoutubeDL.py docstring.
- The JS scripts ship as the `yt-dlp-ejs` PyPI package (0.8.0, 2026-03-17; https://pypi.org/pypi/yt-dlp-ejs/json), bundled in official
  binaries and pulled in by `pip install -U "yt-dlp[default]"` (EJS wiki). "yt-dlp may bump the minimum version on updates without warning".
- Without a runtime (source, `_video.py`): warning "YouTube extraction without a JS runtime has been deprecated, and some formats may be
  missing" and the default client set collapses to `_DEFAULT_JSLESS_CLIENTS = ('visionos',)`.
- Packaging shortcut for containers: **`pip install deno`** — "Python redistribution of Deno binaries" (2.9.7, 2026-09-17,
  https://pypi.org/pypi/deno/json) and yt-dlp 2026.01.29 "Support Deno installed via Python package (#15614)". spotDL does the same job
  itself (`spotdl/utils/deno.py` downloads deno from `dl.deno.land`, and its Docker image ships Deno since 4.5.1) —
  https://raw.githubusercontent.com/spotDL/spotify-downloader/master/spotdl/utils/deno.py

### 1.3 Audio-only formats on YouTube / YouTube Music
Community itag table (secondary but the most complete): https://gist.github.com/MartinEesmaa/2f4b261cb90a47e9c41ba115a011a4aa

| itag | container/codec | nominal rate | notes |
|---|---|---|---|
| 139 | mp4 / AAC-HE v1 | 48 kbps | |
| **140** | mp4 (m4a) / AAC-LC | 128 kbps | universal |
| 141 | mp4 / AAC-LC | 256 kbps | **Premium only** (YT Music) |
| 249 / 250 | webm / Opus | ~50 / ~70 kbps VBR | |
| **251** | webm / Opus | ~128–160 kbps VBR | universal; yt-dlp typically reports ~130–160k |
| 774 | webm / Opus | ~256 kbps VBR | **YouTube Music Premium only** |
| 256/258/327/338/773 | AAC 5.1 / Opus ambisonic / IAMF | – | rare |
| `*-drc` | same | same | "DRC" loudness-processed twins; yt-dlp names them `140-drc`, `251-drc` and ranks them half a quality step lower (`'quality': q(quality) - bool(fmt_stream.get('isDrc')) / 2`, `_video.py`) |

gytmdl's list agrees: free 139/140/249/250/251, premium 141/774, default itag 140 (https://pypi.org/project/gytmdl/).

Premium (256k) reality, from yt-dlp source/changelog (primary):
- `_DEFAULT_CLIENTS = ('visionos', 'web')`, `_DEFAULT_AUTHED_CLIENTS = ('web_embedded', 'tv_downgraded', 'web')`,
  `_DEFAULT_PREMIUM_CLIENTS = ('web_creator', 'tv_downgraded', 'web')` ("# Premium does not require POT (except for subtitles)") —
  https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/youtube/_video.py
- `web_music` client (`INNERTUBE_HOST: music.youtube.com`, WEB_REMIX) has `GvsPoTokenPolicy(required=True, … not_required_for_premium=True)`
  — https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/youtube/_base.py
- Changelog: 2025.06.30 "youtube: Fix premium formats extraction (#13586)"; 2025.07.21 "Do not require PO Token for premium accounts (#13640)".
- README: "If logged-in cookies are passed … `web_creator,tv_downgraded,web` is used for premium accounts. The `web_music` client is added for
  `music.youtube.com` URLs when logged-in cookies are used … Some clients, such as `web_creator` and `web_music`, require a `po_token` for
  their formats to be downloadable." Also: `use_ad_playback_context` — "Do NOT use this when passing premium account cookies … loss of premium formats".
- Open reports of 141/774 disappearing (bodies only; maintainer replies unreadable here): #12455 (2025-02-23), #12891 (2025-04-11),
  #13835 (2025-07-25, *YouTube Music* Premium not detected as Premium, though 774 downloads once a PO token is supplied), #14208 (2025-08-31,
  "since 2025.08.11 … formats 141 and 774 are no longer available" with premium cookies). https://github.com/yt-dlp/yt-dlp/issues/14208
  **Design takeaway: treat 256k as opportunistic; never make the pipeline depend on it.**
- Which 256k is "better" (#9724, body): Opus 774 "Have a 20khz lowpass filter, and … are always resampled to 48khz"; AAC 141 lacks those but
  shows "larger 'holes' in the audio at high frequencies". Closed without a documented decision. https://github.com/yt-dlp/yt-dlp/issues/9724

Default selection behaviour (primary):
- `-x` with no `-f` sets `opts.format = 'bestaudio/best'` (`yt_dlp/__init__.py` L594–596).
- Default sort: `lang,quality,res,fps,hdr:12,vcodec,channels,acodec,size,br,asr,proto,ext,hasaud,source,id`; `acodec` order
  `flac/alac > wav/aiff > opus > vorbis > aac > mp4a > mp3 …` (README "Sorting Formats"). 140 and 251 share the same YouTube `quality` tier,
  so `bestaudio` resolves to **251 (Opus)** in practice. To force AAC: `-f "ba[ext=m4a]/ba"`, or preset
  `-t aac` = `-f 'ba[acodec^=aac]/ba[acodec^=mp4a.40.]/ba/b' -x --audio-format aac`; `-t mp3` = `-f 'ba[acodec^=mp3]/ba/b' -x --audio-format mp3`.
  spotDL uses `bestaudio[ext=m4a]/bestaudio/best` (m4a output) and `bestaudio[ext=webm]/bestaudio/best` (opus output).

### 1.4 Recommended audio-extraction options (README text, verbatim where quoted)
- `-x, --extract-audio` — "Convert video files to audio-only files (requires ffmpeg and ffprobe)".
- `--audio-format FORMAT` — "currently supported: best (default), aac, alac, flac, m4a, mp3, opus, vorbis, wav".
- `--audio-quality QUALITY` — "Insert a value between 0 (best) and 10 (worst) for VBR or a specific bitrate like 128K (default 5)".
  Only applied when actually transcoding (see postprocessor logic below).
- `--embed-metadata` ("Also embeds chapters/infojson if present unless --no-embed-chapters/--no-embed-info-json"), `--embed-thumbnail`
  (+ `--convert-thumbnails jpg` for player compatibility), `--parse-metadata [WHEN:]FROM:TO`, `--replace-in-metadata FIELDS REGEX REPLACE`.
- Default tag mapping (README "MODIFYING METADATA"): `title <- track or title`; `artist <- artist, artists, creator, creators, uploader or
  uploader_id`; `album <- album or series`; `album_artist`; `track <- track_number`; `date <- upload_date`; `genre`; `disc`.
  Override with `meta_` fields, e.g. `--parse-metadata "%(our_title)s:%(meta_title)s"` — "Any value set to the `meta_` field will overwrite all default values".
- Output-template music fields: `track`, `track_number`, `artists`/`artist`, `album`, `album_artists`/`album_artist`, `genre`, `release_year`.
  For programmatic path capture use `--print after_move:filepath` and `-J`/`--dump-single-json` (README).
- Thumbnail embedding backend (`postprocessor/embedthumbnail.py`): mutagen for mp3/flac/ogg/opus/m4a (OggOpus, MP4 `covr`), AtomicParsley
  fallback for m4a, ffmpeg for mkv/mka; thumbnails must be jpg/png (webp is converted).
- **ATV description parsing (primary, `_video.py`)**: when a description ends with "Auto-generated by YouTube." yt-dlp applies
  `(?P<track>…) · (?P<artist>…)\n+(?P<album>…)\n+(?:℗\s*(?P<release_year>\d{4}))?(?:.+?\nReleased on\s*:\s*(?P<release_date>…))?(?:.+?\nArtist\s*:\s*(?P<clean_artist>…))?`
  and fills `track`, `artists`, `album`, `release_date`, `release_year`. i.e. Art-Track uploads carry machine-readable metadata out of the box.

FFmpegExtractAudioPP semantics (`yt_dlp/postprocessor/ffmpeg.py`, primary):
```
ACODECS = {'mp3': ('mp3','libmp3lame',()), 'aac': ('m4a','aac',('-f','adts')), 'm4a': ('m4a','aac',('-bsf:a','aac_adtstoasc')),
           'opus': ('opus','libopus',()), 'vorbis': ('ogg','libvorbis',()), 'flac': ('flac','flac',()), 'alac': ('m4a',None,('-acodec','alac')), 'wav': ...}
if target_format == 'best' and information['ext'] in COMMON_AUDIO_EXTS: skip ("already in a common audio format")   # m4a/mp3/opus/ogg/flac/...
if filecodec == 'aac' and target_format in ('m4a','best'): copy into .m4a                                            # "Lossless, but in another container"
elif target_format == 'best' or target_format == filecodec: copy into ACODECS[filecodec] ext                       # webm/opus -> .opus, no re-encode
else: transcode with ACODECS[target_format] (+ libfdk_aac if available) and _quality_args
```
`COMMON_AUDIO_EXTS` = `('aiff','alac','flac','m4a','mka','mp3','ogg','opus','wav')` + wma (`yt_dlp/utils/_utils.py`); `webm` is not in it, so
`-x` on itag 251 always yields a **lossless remux to `.opus`**, and `-x` on itag 140 leaves the `.m4a` untouched.

Suggested CLI (single track, Opus kept lossless, our own tags applied afterwards):
```
yt-dlp -f "bestaudio[acodec=opus]/bestaudio/best" -x --audio-format opus \
  --embed-thumbnail --convert-thumbnails jpg --embed-metadata \
  --sleep-requests 0.75 --sleep-interval 10 --max-sleep-interval 20 --retries 5 \
  --cookies /config/youtube-cookies.txt \
  --extractor-args "youtubepot-bgutilhttp:base_url=http://bgutil:4416" \
  -o "%(id)s.%(ext)s" --print after_move:filepath -- "https://music.youtube.com/watch?v=<videoId>"
```
Python API equivalent (README "EMBEDDING YT-DLP", primary): `from yt_dlp import YoutubeDL; with YoutubeDL(opts) as ydl: ydl.download(URLS)`;
`info = ydl.extract_info(URL, download=False)` then `ydl.sanitize_info(info)` ("we do not guarantee the return value … to be json serializable").
Option keys: `format`, `outtmpl`, `postprocessors=[{'key':'FFmpegExtractAudio','preferredcodec':'m4a'}]`, `cookiefile`, `cookiesfrombrowser`,
`sleep_interval_requests`, `sleep_interval`, `max_sleep_interval`, `ratelimit`, `retries`, `proxy`, `source_address`, `extractor_args`,
`js_runtimes`, `remote_components`, `postprocessor_args`. `devscripts/cli_to_api.py` translates CLI flags to these keys. yt-dlp is
"callable from any programming language" as a CLI too; if the app is not Python, shell out with `-J`/`--print` (README advises not to parse normal stdout).

### 1.5 Bot check, PO tokens, cookies, client selection, rate limits
- **PO token** (wiki, primary): "a parameter that YouTube requires to be sent with requests from some clients. Without it, requests for the
  affected clients' format URLs may return HTTP Error 403, or result in your account or IP address being blocked." Generated by BotGuard
  (web) / DroidGuard / iOSGuard. Three contexts: `gvs` (streaming URLs), `player`, `subs`. Enforcement table: `web` (Subs, GVS), `web_safari`
  (GVS), `mweb` (GVS), `android`/`ios` (GVS or Player), `web_music` (GVS), `web_creator` (GVS), `tv_simply` (GVS), `tv`/`web_embedded`/`android_vr`
  not required. "GVS PO Token is not required for YouTube Premium subscribers". "Manually extracting PO Tokens is no longer recommended.
  YouTube now binds PO Tokens to the video ID". "TL;DR recommended setup: Use a PO Token Provider plugin to provide the `mweb` client with a
  PO Token for GVS requests." https://github.com/yt-dlp/yt-dlp/wiki/PO-Token-Guide
- Provider plugins: **bgutil-ytdlp-pot-provider** ("Maintained by a yt-dlp maintainer"): v2.0.0 (2026-09-08; RCE fix, server now binds localhost);
  requires yt-dlp >= 2025.05.22 and Node >= 22 or Deno >= 2.0; HTTP-server mode on port 4416 (recommended) or per-call script mode ("NOT
  recommended for high concurrency"); `docker run --name bgutil-provider -d --init -p 127.0.0.1:4416:4416 brainicism/bgutil-ytdlp-pot-provider`;
  `python3 -m pip install -U bgutil-ytdlp-pot-provider`; custom URL via `--extractor-args "youtubepot-bgutilhttp:base_url=http://127.0.0.1:8080"`.
  Caution in README: "Providing a PO token does not guarantee bypassing 403 errors or bot checks, but it _may_ help your traffic seem more
  legitimate." It "was used to bypass the 'Sign in to confirm you're not a bot' message when invoking yt-dlp from an IP address flagged by YouTube."
  https://github.com/Brainicism/bgutil-ytdlp-pot-provider . Alternative: yt-dlp-getpot-wpc (browser-minted, experimental) https://github.com/coletdjnz/yt-dlp-getpot-wpc
- yt-dlp also has a public provider framework (`register_provider`, `PoTokenProvider` classes ending in `PTP`, cache providers `PCP`) so an app
  can supply tokens itself: https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/youtube/pot/README.md
- Extractor args (README `#### youtube`, verbatim excerpts): `player_client` — "currently available clients are `web`, `web_safari`, `web_embedded`,
  `web_music`, `web_creator`, `mweb`, `ios`, `visionos`, `android`, `android_vr`, `tv`, `tv_downgraded`, and `tv_simply`. By default, `visionos,web` is
  used. If no JavaScript runtime/engine is available, then `web` is omitted … You can prefix a client with `-` to exclude it, e.g.
  `youtube:player_client=default,-web`"; `po_token` — "Comma-separated list … `CLIENT.CONTEXT+PO_TOKEN`, e.g. `youtube:po_token=web.gvs+XXX,web.player=XXX`";
  `fetch_pot` — `always|never|auto` (default auto); `formats=missing_pot` — "include formats that require a PO Token but are missing one";
  `player_skip=configs,webpage,js,initial_data` ("could cause issues such as missing formats"); `visitor_data`, `data_sync_id`; `pot_trace=true`.
- Cookies (README): `--cookies FILE` (Netscape format) and `--cookies-from-browser BROWSER[+KEYRING][:PROFILE][::CONTAINER]` (brave, chrome, chromium,
  edge, firefox, opera, safari, vivaldi, whale). Wiki guidance (primary, https://github.com/yt-dlp/yt-dlp/wiki/Extractors): "By using your account
  with yt-dlp, you run the risk of it being banned (temporarily or permanently). Be mindful with the request rate … consider using a throwaway
  account." Export from a private window: log in, open `https://www.youtube.com/robots.txt` in the same tab, export cookies with an extension,
  close the window so the session is never rotated ("YouTube rotates account cookies frequently on open YouTube browser tabs"). "Do NOT use the
  `--cookies COOKIEFILE --cookies-from-browser BROWSER` method". "logging in with OAuth no longer works with yt-dlp."
- Rate limits (wiki Extractors, primary and quantitative): error "This content isn't available, try again later" = rate limit; "It is recommended to add
  a delay of around 5-10 seconds between downloads with `-t sleep`" (preset = `--sleep-subtitles 5 --sleep-requests 0.75 --sleep-interval 10
  --max-sleep-interval 20`); "the rate limit for guest sessions is ~300 videos/hour (~1000 webpage/player requests per hour). For accounts, it is
  ~2000 videos/hour (~4000 webpage/player requests per hour)." FAQ: 429/402 = "the service is blocking your IP address because of overuse"
  (solve CAPTCHA in a browser, pass cookies, `--source-address`). https://github.com/yt-dlp/yt-dlp/wiki/FAQ
- Datacenter / VPN IPs: the official docs never use the word "datacenter"; the primary statements are "IP address flagged by YouTube" (bgutil) and
  "account or IP address being blocked" (PO Token Guide). Secondary guides consistently report that cloud/VPS/VPN egress is challenged far more
  than residential (e.g. https://ytdlp.org/guides/fix-sign-in-to-confirm-not-a-bot , https://dalvo.io/blog/yt-dlp-sign-in-to-confirm-not-a-bot).
  Maintainer comments in the canonical thread https://github.com/yt-dlp/yt-dlp/issues/10128 were **UNVERIFIED** (not fetchable).
  Design implication: run the YouTube worker on a residential egress (home connection or `--proxy` to a residential proxy), concurrency 1,
  the `-t sleep` delays, a throwaway account's cookies, a bgutil sidecar, and treat every failure as "retry later", never as a hard error.
- Other resilience options: `--retries` (default 10), `--retry-sleep [TYPE:]EXPR`, `--limit-rate`, `--throttled-rate`, `--download-archive`.

---

## 2. YouTube Music specifically — ytmusicapi (sigma67)

- Version **1.12.3** (PyPI upload 2026-09-16; GitHub release 2026-09-16), Python >= 3.10, "Unofficial API for YouTube Music … emulates
  YouTube Music web client requests using the user's cookie data for authentication." Repo pushed 2026-09-25 (active).
  https://pypi.org/pypi/ytmusicapi/json , https://github.com/sigma67/ytmusicapi
- **Auth is NOT needed for search/browsing.** README groups features: browsing (search, suggestions, artist/album/song info, watch playlists,
  lyrics), exploring (moods/genres/charts) vs. library/playlist/upload management (auth). Setup docs: "further setup is only needed if you
  want to access account data using authenticated requests." https://raw.githubusercontent.com/sigma67/ytmusicapi/main/docs/source/setup/index.rst
  OAuth caveat: "As of November 2024, YouTube Music requires a Client Id and Secret for the YouTube Data API … select `OAuth client ID` and pick
  `TVs and Limited Input devices`" (https://raw.githubusercontent.com/sigma67/ytmusicapi/main/docs/source/setup/oauth.rst); uploads need browser-cookie auth.
- Rate limit (FAQ): "There most certainly is, although you shouldn't run into it during normal usage." spotDL wraps searches in 3 attempts.
- `search()` signature (primary, `ytmusicapi/mixins/search.py`):
  `search(self, query: str, filter: _SearchFilterType | None = None, scope: _SearchScopeType | None = None, limit: int = 20, ignore_spelling: bool = False) -> JsonList`
  filter in `songs, videos, albums, artists, playlists, community_playlists, featured_playlists, profiles, podcasts, episodes`; scope `library|uploads`;
  `ignore_spelling=True` -> "the exact search term will be searched for, and will not be corrected" (use this for ISRC/exact queries).
- Song result fields (docstring example, verbatim keys): `category:"Songs"`, `resultType:"song"`, `videoId`, `title`, `artists:[{name,id}]`,
  `album:{name,id}`, `duration:"4:19"`, `duration_seconds:259`, `isAvailable`, `isExplicit`, `inLibrary`, `feedbackTokens`, plus `thumbnails`
  (and `videoType` when the play endpoint carries it). Video result fields: `category:"Videos"`, `resultType:"video"`, `videoId`, `title`, `artists`,
  `views:"386M"`, `duration:"4:38"`, `duration_seconds:278`, `videoType` (e.g. `MUSIC_VIDEO_TYPE_OMV`). Note the docstring example itself shows
  the classic hazard: Oasis "Wonderwall" **song = 4:19, video = 4:38**.
- How `videoType`/`resultType` are derived (`ytmusicapi/parsers/search.py` + `navigation.py`):
  `NAVIGATION_VIDEO_TYPE = ["watchEndpoint","watchEndpointMusicSupportedConfigs","watchEndpointMusicConfig","musicVideoType"]`, read from
  `PLAY_BUTTON…playNavigationEndpoint` (or `onTap` for top results). When a result has no category the type is inferred from the browseId prefix
  (`MPRE`->album, `UC`->artist …) or `{"MUSIC_VIDEO_TYPE_ATV": "song", "MUSIC_VIDEO_TYPE_PODCAST_EPISODE": "episode"}.get(video_type, "video")`.
  `isExplicit = nav(data, BADGE_LABEL, True) is not None`.
- `videoType` semantics (ytmusicapi FAQ, primary): "`OMV`: Original Music Video - uploaded by original artist with actual video content;
  `UGC`: User Generated Content - uploaded by regular YouTube user; **`ATV`: High quality song uploaded by original artist with cover image**;
  `OFFICIAL_SOURCE_MUSIC`: Official video content, but not for a single track". `get_song()` examples also show `MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK`
  (user uploads) and `MUSIC_VIDEO_TYPE_PODCAST_EPISODE`. https://raw.githubusercontent.com/sigma67/ytmusicapi/main/docs/source/faq.rst
- ATV = **Art Track**: an auto-generated YouTube video built from the audio + artwork a label/distributor delivers to YouTube Music, published on
  auto-generated "<Artist> - Topic" channels, with a description "Provided to YouTube by <distributor> … ℗ <year> … Auto-generated by YouTube."
  Canonical explanation: https://support.google.com/youtube/answer/6007071 (**UNVERIFIED**: blocked here); distributor explanations via search
  snippets (RouteNote https://routenote.com/radar/what-is-a-topic-channel-on-youtube-youtube-topic-channels-explained/ , Symphonic
  https://support.symdistro.com/hc/en-us/articles/46477580343181 ). The description format is independently confirmed by yt-dlp's parser regex (section 1.4).
  Why ATV is the best single-song candidate: the audio is the delivered master (no intro/outro/crowd/video edits), the length matches the
  catalogue length, `album`/`artists`/`isExplicit` are structured, and the `songs` filter is the same catalogue Spotify/Apple ISRCs map to.
  spotDL formalises this as `verified = resultType == "song"` and returns immediately on a verified >= 80 score.
- Album art and metadata: `get_album(browseId)` returns `title, type, thumbnails, description, year, artists, trackCount, duration, audioPlaylistId,
  tracks[{videoId,title,artists,album,duration,duration_seconds,isExplicit,isAvailable,trackNumber,…}], other_versions`; `get_song(videoId)` returns
  `videoDetails{videoId,title,lengthSeconds,channelId,author,musicVideoType,thumbnail.thumbnails,viewCount}` and `streamingData.adaptiveFormats`
  (itags/bitrates only — URLs are ciphered, so download through yt-dlp). `get_artist(channelId)` returns `songs.browseId` (a playlist of the
  artist's catalogue) and `albums/singles` sections. Thumbnails are `lh3.googleusercontent.com` URLs whose `=w60-h60` suffix can be rewritten for a
  large square cover (practice used by gytmdl "high-resolution square covers"; not documented by ytmusicapi — **UNVERIFIED** as an API contract).
  ytmusicapi exposes the artist channel id (`artists[].id`, `UC…`), not the "- Topic" channel id.
- ISRC search: YouTube Music's search accepts an ISRC string as the query; spotDL relies on this (`get_results(song.isrc)` with no filter) and
  detects it via `ISRC_REGEX = ^[A-Z]{2}-?\w{3}-?\d{2}-?\d{5}$`. (Behaviour observed in spotDL code; YouTube does not document it.)

---

## 3. How spotDL matches a Spotify track to YouTube Music (exact rules from source)

Version 4.5.2 (PyPI 2026-07-20). Sources: https://raw.githubusercontent.com/spotDL/spotify-downloader/master/spotdl/providers/audio/base.py ,
`.../providers/audio/ytmusic.py` , `.../utils/matching.py` , `.../utils/config.py`.

Providers (`spotdl/providers/audio/__init__.py`): `YouTube`, `YouTubeMusic`, `SoundCloud`, `BandCamp`, `Piped` (slider.kz survives only as
vestigial rules inside matching.py). Defaults (config.py): `audio_providers: ["youtube-music"]`, `lyrics_providers: ["genius","azlyrics","musixmatch"]`
(+ `synced` provider exists), `format: "mp3"`, `bitrate: "128k"`, `output: "{artists} - {title}.{output-ext}"`, `threads: 4`, `filter_results: True`,
`only_verified_results: False`, `id3_separator: "/"`.

YouTubeMusic provider: `SUPPORTS_ISRC = True`, `SEARCH_ATTEMPTS = 3`, `GET_RESULTS_OPTS = [{"filter":"songs","ignore_spelling":True,"limit":50},
{"filter":"videos","ignore_spelling":True,"limit":50}]`, client `YTMusic(language="de")`. Each result -> `Result(url=music.youtube.com/watch?v=… if
resultType=="song" else www.youtube.com/…, verified = resultType=="song", name=title, author=artists[0].name, artists=tuple(names),
duration=parse_duration(duration), isrc_search=<query looked like an ISRC>, explicit=isExplicit, album=album.name)`; results without `videoId` or artists are dropped.

`AudioProvider.search(song)` order:
1. `search_query = create_song_title(song.name, song.artists).lower()` unless a custom template is set — i.e. `"artist1, artist2 - song name"`
   (formatter.py docstring: `Example: "Artist1, Artist2 - Song Name"`).
2. **ISRC first** (if `song.isrc` and `SUPPORTS_ISRC` and no custom query): `get_results(song.isrc)`; optionally keep only verified; if exactly one
   verified result -> return it; else `order_results(...)`, and if the best ISRC score `> 80.0` -> return it.
3. For each `GET_RESULTS_OPTS` (songs, then videos): `get_results(search_query, **opts)`; if any result URL is in the ISRC result set -> return it;
   `order_results(...)`; `best = get_best_result(...)`; **if `best_score >= 80 and best_result.verified` -> return immediately** ("song type results
   are always more accurate than video type"); otherwise accumulate and continue.
4. Return the overall best; `None` if nothing survived.

`order_results()` per result (0–100 scale):
- Skip if `check_common_word` fails (no word of the song name appears in the result name).
- `artists_match`: `calc_main_artist_match` (fuzzy `ratio` on slugified main artist; if primary < 50 fall back to secondary artists), `calc_artists_match`
  (paired comparison of remaining artists), then `artists_match_fixup1/2/3` (unverified: use channel name / search the title; verified: +5 per matching
  non-primary artist; single-artist results matched against the whole title; thresholds at 70/80).
- `name_match = calc_name_match` (`ratio` of slugified, re-sorted names; if `<= 75` retry with artist-enriched match strings and take the max);
  **−15 per forbidden word** found in the result but not in the song: `FORBIDDEN_WORDS = ["bassboosted","remix","remastered","remaster","reverb",
  "bassboost","live","acoustic","8daudio","concert","acapella","slowed","instrumental","cover"]`.
- `album_match = ratio(slug(song.album_name), slug(result.album))` (0 if either missing).
- `time_match = exp(-0.1 * |song.duration - result.duration|) * 100` -> 1 s: 90.5, 3 s: 74.1, 5 s: 60.7, 7 s: 49.7, 10 s: 36.8, 14 s: 24.7.
- Rejections: `name_match <= 60`; `artists_match < 70` (except slider.kz); `time_match < 25`; `time_match < 50 and average_match < 75`.
- `average_match = (artists_match + name_match) / 2`; if `verified and not isrc_search and album and album_match <= 80` ->
  `average = (average + album_match) / 2`; if `(not isrc_search and average <= 85) or slider.kz or time_match < 0` ->
  `average = (average + time_match) / 2` and **−5 if explicit flags disagree**; capped at 100.
`get_best_matches(results, 8)` keeps everything within 8 points of the top score; `get_best_result`: single -> it; top `> 80 and isrc_search` -> it;
otherwise add a view-count bonus `((views - min) / (max - min)) * 15` (cap 100) and take the highest.

Download/tagging: yt-dlp options `{"format": <per output>, "quiet", "no_warnings", "encoding": "UTF-8", "cookiefile", "outtmpl": "<temp>/%(id)s.%(ext)s",
"retries": 5, "extractor_args": {}}` + deno options + user `yt_dlp_args`. Conversion (`spotdl/utils/ffmpeg.py`): `FFMPEG_FORMATS = {"mp3": libmp3lame,
"flac": flac -sample_fmt s16, "ogg": libvorbis, "opus": libopus, "m4a": aac}`; **stream copy (`-vn -c:a copy`) when output is opus from a webm source or
m4a from an m4a source and no bitrate/ffmpeg args are given**; `-q:a` for numeric VBR values, `-b:a` otherwise. Tags via mutagen (`spotdl/utils/metadata.py`):
M4A preset (`covr` art, `©lyr` lyrics, `----:spotdl:ISRC`, `rtng` explicit 4/2), MP3 preset (`APIC`, `USLT`, `TSRC`), Vorbis comments for
flac/ogg/opus incl. `isrc`; cover art, lyrics, disc/track numbers, `comment` = source URL, optional `.lrc`.

---

## 4. Lidarr-ecosystem YouTube integrations (status as of 2026-09-28)

| Project | Repo | Alive? | Approach |
|---|---|---|---|
| **Tubifarry** (Lidarr plugin) | https://github.com/TypNull/Tubifarry | Yes — pushed 2026-09-17; releases 2.2.0.5 (2026-09-09), 2.2.0.4 (08-21), 2.2.0.3 (08-18), 2.2.0.2 (07-28), 2.2.0.1 (07-21); 1,045 stars; MIT | C# plugin for Lidarr's **plugins branch** (`System -> Plugins`, docker `ghcr.io/hotio/lidarr:pr-plugins`; 2.2.0.5 requires Lidarr >= 3.1.5.0 per release notes). Indexers: "Spotify's catalog as an indexer … then downloads the actual audio files from YouTube"; Slskd (Soulseek) indexer+client; web clients Lucida, DABmusic, T2Tunes, Subsonic; Invidious indexer/client (2.2.0.4). Download clients: YouTube, Slskd. YouTube: "extracts audio from YouTube and converts them to audio files using FFmpeg"; "Standard quality: 128kbps AAC (free users) / High quality: 256kbps AAC (YouTube Premium required)"; bot detection: cookies.txt + "Trusted Session Generator … requires Node.js". README does not mention yt-dlp (download client implementation unverified). No Tidal/Qobuz/Deezer (see TrevTV). Extras: Codec Tinker (ffmpeg rules), Lyrics Fetcher, MetaMix (Discogs/Deezer/Last.fm), Arr-Soundtracks import list. |
| TrevTV plugins | https://github.com/TrevTV/Lidarr.Plugin.Tidal , …/Lidarr.Plugin.Qobuz , …/Lidarr.Plugin.Deezer | Tidal 10.1.0.45 (2026-01-17, pushed 2026-01-17); Qobuz 10.1.0.42 (2025-11-22); Deezer (via Deemix) has 2026 issues | Lidarr plugins-branch indexer+downloader per service; "requires Lidarr's plugins branch … ghcr.io/hotio/lidarr:pr-plugins". |
| Invidious trusted-session-generator (used by Tubifarry) | https://github.com/iv-org/youtube-trusted-session-generator | **Deprecated**: "This tool is deprecated and may not work anymore because YouTube have changed how the identity tokens (po_token and visitor_data) are handled" | Playwright/Chromium mints `po_token` + `visitor_data`; superseded by Invidious Companion. |
| LidaTube | https://github.com/TheWicklowWolf/LidaTube | Yes — v0.2.56 (2026-08-21), pushed 2026-08-21, 364 stars | Standalone Docker (`thewicklowwolf/lidatube`) that reads missing *albums* from the Lidarr API and downloads via yt-dlp; env `minimum_match_ratio=90`, `secondary_search=YTS|YTDLP`, `preferred_codec=mp3`, `sleep_interval`, `cookies.txt` in config dir. Album-oriented, not a Lidarr indexer. |
| Lidarr-YouTube-Downloader (Angrido) | https://github.com/Angrido/Lidarr-YouTube-Downloader | Yes — v1.9.1 (2026-09-26), created 2025-11-12, 75 stars | Flask app that registers in Lidarr as a **Newznab indexer + SABnzbd download client** (`/api/newznab/api`, `/api/sabnzbd`) so Lidarr searches/grabs/imports natively. yt-dlp search, 15 candidates/track; ranking "title similarity (50%), duration (25%), official-channel bonus (15%), view-count weight, forbidden-word filtering"; optional **AcoustID/chromaprint verification against MusicBrainz recording IDs**; mutagen tags with MBIDs + 3000 px cover; MP3 (default, up to 320) / M4A / Opus; cookies; **bundled bgutil PO-token sidecar**. Closest existing analogue to our per-track pipeline. |
| dmzoneill/lidarr-youtube-downloader | https://github.com/dmzoneill/lidarr-youtube-downloader | pushed 2026-09-03, 77 stars | Iterates missing tracks, yt-dlp + ffmpeg. Small. |
| concubidated/lidarr-downloader | https://github.com/concubidated/lidarr-downloader | pushed 2025-12-20, 2 stars | Same idea, tiny. |
| MeTube | https://github.com/alexta69/metube | Very active — releases 2026.09.27/26/25/23/20; 14.9k stars | Web UI for yt-dlp; `AUDIO_DOWNLOAD_DIR`; yt-dlp options as JSON keyed by API option names; cookies for "confirm you're not a bot" videos; explicitly no tagging ("point beets … Picard … or Lidarr at your AUDIO_DOWNLOAD_DIR"). No library-style API documented. |
| yt-dlp-web-ui (marcopiovanello) | https://github.com/marcopiovanello/yt-dlp-web-ui | Yes — "Server v4" 2026-07-14; 2.6k stars | Go RPC server + UI; "JSON-RPC 1.0 interface through Websockets and HTTP-POST", OpenAPI at `/openapi`; "Extract audio" option. Usable as a download micro-service but not a matcher. |
| Pinchflat | https://github.com/kieraneglin/pinchflat | v2025.9.26; pushed 2025-12-16 (slowing) | Channel/playlist subscriptions, audio-only supported, cookies. Not per-song. |
| YoutubeDL-Material | https://github.com/Tzahi12345/YoutubeDL-Material | pushed 2026-03-08 | Web UI. |
| Firzen7/yt-dlp-web, sooros5132/yt-dlp-web | https://github.com/Firzen7/yt-dlp-web , https://github.com/sooros5132/yt-dlp-web | 0 stars (2026-09) / pushed 2025-05-29 | Minimal web frontends. |
| gytmdl | https://github.com/glomatico/gytmdl | 2.1.6 (2025-07-15), pushed 2025-07-15 | YouTube-Music CLI: "YouTube Music API is used to get accurate metadata that yt-dlp alone can't provide"; itags 140 default, 141/774 with premium cookies; square covers, LRC lyrics. Good reference for YTM tagging. |

There is no "official" Lidarr YouTube indexer; the two workable patterns are (a) a Lidarr *plugin* (plugins branch only) and (b) a fake
Newznab+SABnzbd façade (Angrido). For a new *arr-style app neither is needed — we own the search/download loop.

---

## 5. Audio quality realities and container choices

- What YouTube actually serves is a lossy transcode of the delivered master: Opus 251 (~128–160 kbps VBR, 48 kHz, 20 kHz low-pass per #9724)
  or AAC-LC 140 (128 kbps); 256 kbps (141/774) only with Premium cookies and only when yt-dlp's premium path works (section 1.3). FLAC/MP3-320 never
  come from YouTube; lossless only from Soulseek, Bandcamp purchases, or subscription rippers (section 7). Rank YouTube below Soulseek lossless
  and below Soulseek MP3-320 in the quality profile; treat it as "fill the gap now, upgrade later".
- Opus vs AAC/MP3 at equal bitrate: Opus is generally the more efficient codec (Xiph/opus-codec.org listening-test summary at
  https://opus-codec.org/comparison/ — **UNVERIFIED**, blocked here). Anecdotal spectral evidence in yt-dlp issues: SoundCloud 64 kbps Opus
  "peaking at 20khz" vs 128 kbps MP3 "peaking at 16khz" (https://github.com/yt-dlp/yt-dlp/issues/6668). Practical stance: 251 Opus ≈ 140 AAC
  or slightly better; don't re-encode either.
- **Never transcode lossy->lossy unless a player forces it.** yt-dlp's `-x --audio-format opus` (or the default `best`) does a lossless remux of
  webm/Opus into `.opus` (`-acodec copy`, section 1.4) and leaves `.m4a` alone. Equivalent ffmpeg: `ffmpeg -i in.webm -vn -c:a copy out.opus`
  (Ogg Opus; RFC 7845 defines the `.opus` extension and `audio/ogg; codecs=opus` — https://www.rfc-editor.org/rfc/rfc7845 , **UNVERIFIED** here).
  `ffmpeg -i in.webm -vn -c:a copy out.ogg` also works but `.opus` is the conventional extension taggers/players key on. spotDL does exactly
  `-vn -c:a copy` for webm->opus and m4a->m4a. If a target must be MP3/AAC, re-encode once at high quality (`--audio-format mp3 --audio-quality 0`,
  or AAC 256k) and record in the DB that the file is a second-generation lossy copy.
- Never store `.webm`: a real-world PR describes Opus downloads written as `.webm` that "Plex wouldn't scan and that carried no tags at all", fixed
  by remuxing to `.opus` with `-c:a copy` (https://github.com/ssubzwari/streamsnap/pull/36). yt-dlp's `COMMON_AUDIO_EXTS` likewise excludes webm.
- **Plex / Plexamp (UNVERIFIED — all Plex domains blocked here):** search snippets of Plex's support article
  https://support.plex.tv/articles/204377253-what-media-formats-are-supported/ say Plex "supports direct play of streamed FLAC, ALAC, MP3, AAC, APE and
  MPC" and that "Other server supported audio formats including MP3, ALAC, FLAC, OGG, etc. will be automatically transcoded", with Opus offered as the
  low-bandwidth transcode target; community threads (https://forums.plex.tv/t/opus-scanner/103429 , https://forums.plex.tv/t/opus-transcode-and-then-whats-best-at-plexamp-android-client/905151)
  discuss `.opus` scanning/direct-play per client. Safe interpretation: Plex Media Server decodes Opus, but whether a given Plexamp client direct-plays
  `.opus` vs. gets a server transcode depends on client/version. Recommendation: default output `.opus` (lossless remux) + `.m4a` untouched, and
  offer a per-library "compatibility" toggle that transcodes YouTube grabs to AAC `.m4a` 256k or MP3 for Plex-first users. Verify on the user's Plex.
- What others output by default: spotDL mp3 (config `format: mp3`, `bitrate: 128k`; README: "always download the highest possible bitrate; which is
  128 kbps for regular users and 256 kbps for YouTube Music premium users"); gytmdl itag 140 -> `.m4a`; Angrido MP3 (up to 320) / M4A / Opus;
  LidaTube mp3; Tubifarry AAC-in-MP4 via FFmpeg ("check that it's extracting audio to compatible formats like AAC embedded in MP4 containers"); MeTube raw yt-dlp output.

---

## 6. Legal / ToS (brief)

YouTube's Terms of Service (https://www.youtube.com/t/terms — **UNVERIFIED**, blocked; wording via search snippet) prohibit users from
"access[ing], reproduc[ing], download[ing], distribut[ing], transmit[ting], broadcast[ing], display[ing], sell[ing], licens[ing], alter[ing],
modify[ing] or otherwise us[ing] any part of the Service or any Content except: (a) as expressly authorized by the Service; or (b) with prior
written permission from YouTube and, if applicable, the respective rights holders", and from "access[ing] the Service using any automated means
(such as robots, botnets or scrapers) except (a) in the case of public search engines, in accordance with YouTube's robots.txt file; or (b) with
YouTube's prior written permission." Consequences seen in practice: account bans (yt-dlp wiki warning) and IP blocks. The RIAA's 2020 DMCA §1201 notice
against youtube-dl called YouTube's "rolling cipher" a technological protection measure (https://github.com/github/dmca/blob/master/2020/10/2020-10-23-RIAA.md);
GitHub reinstated the project on 2020-11-16 after the EFF's letter: "GitHub has determined that the notice does not meet the requirements of our
DMCA Takedown Policy. Accordingly, GitHub has decided to reject this notice with respect to the parent repository and we've reinstated that
repository" (https://github.com/github/dmca/blob/master/2020/11/2020-11-16-RIAA-reversal.md).
Copyright/anti-circumvention exposure varies by jurisdiction (out of scope). Product implications: YouTube source off by default, user supplies
their own cookies/PO-token sidecar, no bundled accounts, clear in-app disclaimer, and design the source as a plugin so it can be disabled.

---

## 7. Other secondary sources worth a plugin interface (brief)

- **SoundCloud via yt-dlp** (extractor primary: https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/soundcloud.py): default
  requested formats `http_aac, hls_aac, http_opus, hls_opus, http_mp3, hls_mp3` (extractor arg `soundcloud:formats`); free tier yields ~128 kbps MP3 and
  64 kbps Opus (issue #6668 shows `hls_opus_64`, `http_mp3_128`; yt-dlp's default picks the MP3); Go+ "hq" AAC 256 kbps needs a subscription and
  `--username oauth --password <oauth_token>` (extractor: `Use "--username oauth --password <oauth_token>" to login`; `is_premium = quality == 'hq'`, abr 256).
  Mostly useful for remixes/unreleased tracks not on YTM.
- **Bandcamp via yt-dlp** (https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/bandcamp.py): free streams are `mp3-128`
  (`'format': 'mp3-128'` in tests); purchased items expose `download_formats` (FLAC etc.) when logged-in cookies are passed (issues #11916/#11917).
  Legit lossless when the user has bought the release; a "Bandcamp collection" plugin is the cleanest legal source after Soulseek.
- **Deezer**: original deemix is dead (RemixDev stopped; Deezer blocked ARL logins — secondary: https://www.cinchsolution.com/deemix-alternatives/).
  Community monorepo **bambanah/deemix** is alive (deemix@3.14.0, deemix-webui@4.7.0, deemix-cli@0.2.0 on 2026-08-24; webui 4.6.0 (2026-06-18)
  "Removed email/password login; ARL now sole method") https://github.com/bambanah/deemix/releases.atom . **deezspot** (jakiepari) 1.6 (PyPI 2025-10-19,
  pushed 2026-02-11; needs Deezer ARL, MP3_320/FLAC need a paid account; Spotify path needs librespot credentials + Premium; "USE AS YOUR OWN RISK")
  https://github.com/jakiepari/deezspot ; forks such as Xoconoch/deezspot-spotizerr feed the Spotizerr web app. The old `deezer-py` PyPI package is
  frozen at 2022 (deemix 3.6.6 on PyPI, 2022-01-11). Expect breakage every time Deezer changes its API.
- **Tidal**: `exislow/tidal-dl-ng` now returns HTTP 404 on GitHub (only forks/mirrors remain: `Radexito/tidal-dl-ng-For-DJ` created 2026-01-14,
  a mirror `ManelCas/exislow__tidal-dl-ng.47ae9f0e` created 2025-12-28) — removal cause **UNVERIFIED** (search budget exhausted). streamrip covers Tidal.
- **streamrip** (nathom): "A scriptable stream downloader for Qobuz, Tidal, Deezer and SoundCloud"; "For Tidal and Qobuz, you NEED a premium subscription";
  quality tiers 0–4 (128k … 24-bit/192 kHz); v2.2.0 on GitHub 2026-03-12 (PyPI still 2.1.0, 2025-03-10); 333 open issues; disclaimer "By using
  streamrip, you agree to the terms and conditions of the Qobuz, Tidal, and Deezer APIs." https://github.com/nathom/streamrip
- **Qobuz**: qobuz-dl (vitiko98) last pushed 2025-07-19; issue #309 (2025-10-16): "Qobuz started banning accounts that use Qobuz-DL effectively on
  October 1st 2025 … no currently known workarounds" https://github.com/vitiko98/Qobuz-DL/issues/309 . OrpheusDL last pushed 2023-12-15 (stale).
  TrevTV Lidarr.Plugin.Qobuz 10.1.0.42 (2025-11-22).
- Legality caveat for all subscription rippers: they violate the services' ToS/DRM terms even with a paid account, and Qobuz has demonstrated
  account bans; keep them as optional, user-installed plugins with their own credentials, never bundled.

Plugin interface implied by all of the above: `search(isrc?, title, artists, album?, duration_s) -> [Candidate{provider, id, url, title, artists,
album, duration_s, explicit, verified/type, codec, bitrate_kbps, lossless, requires_auth}]`, `fetch(candidate, dest) -> File{path, codec, bitrate,
sha, provenance}`, `capabilities() -> {isrc_search, auth_kinds, lossless, rate_limit_hint}`.

---

## 8. Design recommendations for the YouTube/YTM source

1. **Search with ytmusicapi (no auth)**: ISRC query first (`ignore_spelling=True`), then `filter="songs"` on "artist - title", then `filter="videos"`
   only as a last resort. Prefer `resultType == "song"` / `videoType == ATV`; penalise OMV/UGC/live/remix; require duration within a few seconds
   (spotDL's `exp(-0.1*Δs)` decay with the 25/50 cut-offs is a good default; tighten to ±3 s for ATV vs catalogue length).
2. **Score** with a spotDL-derived rule set (section 3) plus our own catalogue metadata (MusicBrainz/Spotify ISRC, album, explicit flag), and, like
   Angrido, offer optional **AcoustID/chromaprint verification** after download.
3. **Download with yt-dlp**: `bestaudio` (Opus 251) remuxed losslessly to `.opus`, or `ba[ext=m4a]` -> `.m4a`; Premium 256k only if the user
   supplies Premium cookies and yt-dlp reports the formats — never required. Tag with mutagen from *our* metadata (don't trust YouTube tags),
   embed a large square cover from `get_album` thumbnails.
4. **Ops**: container with `pip install "yt-dlp[default]" deno` (or the `deno` PyPI package), bgutil sidecar on 4416, cookies file from the private-window
   method, `-t sleep`-style delays (`--sleep-requests 0.75 --sleep-interval 10 --max-sleep-interval 20`), concurrency 1, residential egress, stay under
   ~300 videos/hour (guest) / ~2000 (account), auto-update yt-dlp, and a health probe that runs `yt-dlp -F` on a known ATV video so the UI can show
   "YouTube source degraded" instead of failing grabs silently.
5. **Quality profile**: Soulseek FLAC > Soulseek MP3-320/AAC-256 > YouTube 256k (Premium) > YouTube Opus 251 / AAC 140 > SoundCloud/Bandcamp free streams;
   mark YouTube grabs upgradeable.
6. **Risk register**: YouTube changes (monthly), PO-token/JS-runtime churn, Premium-format regressions, IP/account blocks, ToS exposure; Plex
   `.opus` direct-play unverified -> ship the "transcode to AAC for compatibility" toggle.
