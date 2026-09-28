# Tech-stack research for a single-song *arr (Docker-first, self-hosted)

Research date: 2026-09-28. Everything below was checked on that date. Registry data (npm, PyPI, NuGet, Go proxy) and raw GitHub files were read directly; GitHub star/licence/last-push figures come from the GitHub search API via the session's GitHub connector; sources marked "(search snippet)" were only seen through a web-search summary because the origin site was blocked by this session's egress proxy (wiki.servarr.com, trash-guides.info, docs.linuxserver.io, hotio.dev, docs.unraid.net, ca.unraid.net, slsknet.org, learn.microsoft.com, docs.astral.sh, several *.readthedocs.io). Where the Servarr wiki was needed I read the same markdown from the Servarr/Wiki GitHub repo instead.

---

## 1. What the real *arrs use, and what forking Lidarr would cost

### 1.1 Language, runtime, libraries (verified from csproj/package.json on `develop`, 2026-09-28)

| App | TargetFramework (NzbDrone.Core csproj) | DB access | Migrations | Logging | Source |
|---|---|---|---|---|---|
| Sonarr v4 | `net6.0` | Dapper 2.0.123, `System.Data.SQLite.Core.Servarr` 1.0.115.5-18, Npgsql 7.0.9 | `Servarr.FluentMigrator.Runner(.SQLite/.Postgres)` 3.3.2.9 | NLog 5.3.4 | https://raw.githubusercontent.com/Sonarr/Sonarr/develop/src/NzbDrone.Core/Sonarr.Core.csproj |
| Radarr | `net8.0` | Dapper 2.1.79 | FluentMigrator.Runner.Core/SQLite/Postgres 6.2.0 | NLog 5.5.1 | https://raw.githubusercontent.com/Radarr/Radarr/develop/src/NzbDrone.Core/Radarr.Core.csproj |
| Lidarr | `net8.0` | Dapper 2.1.79 | FluentMigrator.Runner.* 6.2.0 | NLog 5.5.1 + NLog.Targets.Syslog 7.0.0 | https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Lidarr.Core.csproj |
| Prowlarr | `net8.0` | Dapper 2.1.79 | FluentMigrator.Runner.* 6.2.0 | NLog 5.5.1 | https://raw.githubusercontent.com/Prowlarr/Prowlarr/develop/src/NzbDrone.Core/Prowlarr.Core.csproj |
| Readarr | `net6.0` | Dapper, Servarr.FluentMigrator.* | | NLog | https://raw.githubusercontent.com/Readarr/Readarr/develop/src/NzbDrone.Core/Readarr.Core.csproj |
| slskd (for comparison) | `net10.0` | EF Core 10.0.9 + Microsoft.Data.Sqlite 10.0.9 + Dapper 2.1.79 | EF | Serilog 4.3.1 | https://raw.githubusercontent.com/slskd/slskd/master/src/slskd/slskd.csproj |

Other Sonarr.Core packages seen in the same csproj: FluentValidation 9.5.4, Newtonsoft.Json 13.0.3, MonoTorrent 2.0.7, Servarr.FFMpegCore 4.7.0-26, SixLabors.ImageSharp 3.1.12, Polly 8.5.0, MailKit 4.17.0. The arrs use Dapper + FluentMigrator, not EF Core. DryIoc is the container (the search snippet for Lidarr's csproj history lists "DryIoc for dependency injection": https://github.com/Sonarr/Sonarr/issues/7528 and https://github.com/lidarr/Lidarr/blob/bd62a20ddb039c6a8442f0c858b9d19c76f33338/src/NzbDrone.Common/NzbDrone.Common.csproj); see the verification note appended at the end of this file for the direct csproj grep.

Frontend (identical numbers in Sonarr and Lidarr `package.json`): React 18.3.1, react-dom 18.3.1, Redux 4.2.1, react-redux 7.2.4, reselect 4.1.8, TypeScript 5.7.2, Webpack 5.95.0 (no Vite), react-router 5.2.0, react-window/react-virtualized, FontAwesome.
- https://raw.githubusercontent.com/Sonarr/Sonarr/develop/package.json
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/package.json

.NET version reality check: Sonarr v4 is still on .NET 6 in September 2026. The .NET 8 bump was attempted three times and closed each time: PR #6580 (Mar 2024, converted to draft: "won't be landed until we finalize plans for v5 due to backwards compatibility issues with older OSes" - Markus101), PR #6776 (May-Sep 2024, closed), PR #6983 (Jul 2024, closed as duplicate). Issue #6597 "Update Sonarr to .NET 8" is closed.
- https://github.com/Sonarr/Sonarr/pull/6580
- https://github.com/Sonarr/Sonarr/pull/6776
- https://github.com/Sonarr/Sonarr/pull/6983
- https://github.com/Sonarr/Sonarr/issues/6597

Microsoft support windows (search snippets): .NET 10 is LTS, supported 11 Nov 2025 - 14 Nov 2028; .NET 8 and .NET 9 both end support on 10 Nov 2026.
- https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
- https://abp.io/community/articles/.net-10-what-you-need-to-know-lts-release-coming-november-2025-xennnnky
- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
So a new C# app started now should target .NET 10 (slskd already does), not 8/9.

### 1.2 Packaging
- Official arr builds are per-platform self-contained-ish archives (build.sh in repo: https://github.com/Sonarr/Sonarr/blob/develop/build.sh) and are consumed by LinuxServer.io and hotio images. LSIO `docker-lidarr` README: image `lscr.io/linuxserver/lidarr`, Alpine base (3.24 in the latest changelog entry), env `PUID/PGID/TZ`, volumes `/config`, `/music`, `/downloads`, port 8686. https://raw.githubusercontent.com/linuxserver/docker-lidarr/master/README.md
- hotio/lidarr repo: GPL-3.0, has `s6-overlay` directory and per-arch Dockerfiles; docs at hotio.dev/containers/lidarr (site blocked here). https://github.com/hotio/lidarr
- The Servarr Docker guide (read from the wiki's GitHub source) recommends hotio ("documentation and Dockerfile don't make any poor path suggestions", "images are automatically updated 2x in 1 hour") and says LSIO images are fine but to "avoid their 'suggested (optional)' paths". https://raw.githubusercontent.com/Servarr/Wiki/master/docker-guide.md

### 1.3 Licence and reusability of the "Servarr" shared code
- Sonarr LICENSE.md = GNU GPL v3 (verified: https://raw.githubusercontent.com/Sonarr/Sonarr/develop/LICENSE.md). GitHub API licence for Sonarr, Radarr, Lidarr, Prowlarr = GPL-3.0; Lidarr README says "GNU GPL v3" (https://raw.githubusercontent.com/Lidarr/Lidarr/develop/README.md).
- There is no shared NuGet/framework package. The NzbDrone.* projects live inside each fork and the "Servarr bot" cherry-picks Sonarr commits into Radarr/Lidarr/Readarr/Prowlarr. Issue #7528 "Extracting core NzbDrone framework as a standalone package" was closed as not planned; it quotes: "The cherry-picking approach that currently is implemented by the Servarr bot requires every single commit to Sonarr to have conflicts be manually resolved and merged into every clone project, which is painful and time consuming." https://github.com/Sonarr/Sonarr/issues/7528
- Forkable? Yes, under GPL-3.0: any fork must itself be GPL-3.0, keep notices, and publish source.

### 1.4 What Lidarr forked from, and the cost of moving its unit from album to track
- Lidarr's GitHub description is literally "Looks and smells like Sonarr but made for music." (repo created 2017-05-06; 5,688 stars; last push 2026-09-14). It retains Sonarr's `NzbDrone.*` project layout (`src/NzbDrone.Core/Lidarr.Core.csproj`). https://github.com/Lidarr/Lidarr
- Lidarr's domain is Artist -> Album -> AlbumRelease -> Track, and tracks exist only as nested resources; per-track monitoring is an open feature request: "Download/monitor single songs?" https://github.com/Lidarr/Lidarr/issues/826 (also https://github.com/Lidarr/Lidarr/issues/516, https://github.com/lidarr/Lidarr/issues/1537 about singles vs albums).
- Metadata comes from a proprietary-ish proxy: `api.lidarr.audio`, served by LidarrAPI.Metadata, which "requires access to a musicbrainz postgresql database and solr search server" and which the project itself flags as a single point of failure. https://github.com/Lidarr/LidarrAPI.Metadata (search snippet) ; self-hosted replacement: https://github.com/NC1107/lidarr-metadata-provider

Measured on a sparse clone of `Lidarr/Lidarr@da7b4dfb` (2026-09-13), counted in this session:

| Area | Files | LOC | Notes |
|---|---|---|---|
| `src/NzbDrone.Core` (all) | 1,406 .cs | 95,428 | 264 files mention Album/Artist/Track by name |
| `NzbDrone.Core/Music` | 73 .cs | 5,716 | models: Artist, ArtistMetadata, Album, Release, Medium, Track, Ratings, Links, Member, MonitoringOptions... |
| `NzbDrone.Core/Parser` | 21 .cs | 3,319 | Parser.cs, ParsingService.cs, QualityParser.cs, FingerprintingService.cs (release-name parsing is album-shaped) |
| `NzbDrone.Core/DecisionEngine` | 45 .cs | | specs such as DiscographySpecification, SameTracksSpecification, CutoffSpecification, UpgradeDiskSpecification |
| `NzbDrone.Core/MetadataSource` | 19 .cs | | IProvideAlbumInfo, IProvideArtistInfo, ISearchForNewAlbum, SkyHook (api.lidarr.audio client) |
| `NzbDrone.Core/Datastore/Migration` | 82 migrations | | schema history you would inherit |
| `src/Lidarr.Api.V1` | 163 .cs | | 38 files reference Album/Artist/Track |
| `frontend/src` | 1,846 files | 103,158 | 103 files reference Album/Artist/Track; top-level dirs Album, Artist, Track, TrackFile, AddArtist, Wanted, InteractiveImport, InteractiveSearch, Organize, Retag... |

Assessment: changing the unit from album to track is not a refactor, it is a rewrite of the domain (Music), the parser (release names are album/discography oriented), the decision engine (cutoff/upgrade/discography specs), the metadata source (album/artist proxy -> recording-level MusicBrainz or other), the import/organiser/retag pipeline (TrackFile is album-scoped on disk), 82 migrations, most of the API resources and roughly the whole UI, while carrying ~200k LOC of C#/TS you did not write, a GPL-3.0 obligation, a Sonarr-cherry-pick maintenance model, and an indexer/download-client abstraction that has no notion of Soulseek or YouTube at all (Lidarr's download clients are torrent/usenet only; Soulseek is bolted on externally by soularr). Verdict: do not fork Lidarr. Borrow its patterns (quality/metadata profiles, decision specifications, queue/history/blocklist, `/ping` + `/api/vN/health`, API-key auth, URL base) and its API shape, which third-party tools (pyarr, Homepage, etc.) already understand.

---

## 2. Candidate A - Python

Versions from PyPI on 2026-09-28 (`https://pypi.org/pypi/<pkg>/json`):

| Component | Package | Version (date) | Licence / status | Notes |
|---|---|---|---|---|
| Web | fastapi | 0.141.1 (2026-07-29) | MIT, 102.7k stars, classifier still "4 - Beta" | https://github.com/fastapi/fastapi |
| Web (alt) | litestar | 2.24.0 (2026-06-11) | MIT, 8.5k stars, "5 - Production/Stable" | msgspec serialisation, first-class SQLAlchemy plugin/DTOs; smaller ecosystem (https://betterstack.com/community/guides/scaling-python/litestar-vs-fastapi/, https://dev.to/locionic/fastapi-vs-litestar-2026-performance-benchmarks-when-to-switch-epf) |
| ORM | sqlalchemy | 2.1.1 (2026-09-25) | Production/Stable | + aiosqlite 0.22.1 for async SQLite; Alembic for migrations (standard, not re-verified) |
| Scheduler | apscheduler | 3.11.3 (2026-06-28) stable; 4.0.0a6 (2025-04-27) pre-release | MIT, 7.6k stars | 3.x has BackgroundScheduler/AsyncIOScheduler + SQLAlchemy job store; the 4.x docs say the v4.0 series "should NOT be used in production" (https://apscheduler.readthedocs.io/en/master/migration.html, search snippet; https://pypi.org/project/APScheduler/) |
| Queue (durable, no Redis) | huey | 3.4.0 (2026-09-04) | MIT, 6.0k stars | "builtin support for Redis, Postgres, Sqlite, File-system, and in-memory storage", `periodic_task(crontab(...))`, `retries=`, thread/process/greenlet workers (https://github.com/coleifer/huey). dramatiq (Redis/RabbitMQ) and arq (Redis) need a broker - not re-verified here, general knowledge. |
| YouTube | yt-dlp | 2026.8.19 (2026-08-19) | Unlicense (https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/LICENSE), 194k stars | Native Python: `yt_dlp.YoutubeDL(params).extract_info()/download()`, progress hooks, `devscripts/cli_to_api.py` maps CLI flags to params (https://github.com/yt-dlp/yt-dlp/, https://instagit.com/yt-dlp/yt-dlp/how-to-use-yt-dlp-as-python-library/) |
| Tagging | mutagen | 1.48.1 (2026-06-25) | GPL-2.0-or-later, 2.0k stars | Reads and writes MP3/ID3, FLAC, MP4/M4A, Ogg Opus (and ASF, APE, WavPack, AIFF...) (https://mutagen.readthedocs.io/en/latest/, https://github.com/quodlibet/mutagen) |
| Fingerprint | pyacoustid | 1.3.1 (2026-04-09) | MIT, 409 stars | uses libchromaprint via ctypes or the `fpcalc` binary; AcoustID lookup/submit built in (https://github.com/beetbox/pyacoustid) |
| Soulseek (embedded) | aioslsk | 1.6.4 (2026-09-22) | GPL-3.0-or-later, classifier "4 - Beta", 66 stars, 633 commits | asyncio client: search, download, upload/shares, rooms, PMs, distributed network, mock server for tests; Python 3.10-3.14 (https://github.com/JurgenR/aioslsk, https://pypi.org/project/aioslsk/) |
| Soulseek (via slskd) | slskd-api | 0.2.4 (2026-04-10) | AGPL-3.0 (!) | wrapper used by soularr; the slskd REST API is small enough to call directly with httpx and avoid the AGPL wrapper (https://github.com/bigoulours/slskd-python-api, https://github.com/mrusse/soularr) |
| YT Music | ytmusicapi | 1.12.3 (2026-09-16) | MIT, 3.0k stars | the canonical unofficial YT Music client; other languages are ports of it (https://github.com/sigma67/ytmusicapi) |
| MusicBrainz | musicbrainzngs | 0.7.1 (2020-01-11) | BSD-2 | stale; the WS/2 JSON API is simple enough to call with httpx (https://github.com/alastair/python-musicbrainzngs) |
| Plex | plexapi | 4.18.2 (2026-07-10) | BSD-3, 1.3k stars | library scan/analyze/refresh, playlists, sessions (https://github.com/pkkid/python-plexapi) |
| Validation / packaging | pydantic 2.13.5, uv 0.12.19, uvicorn 0.54.0 | | | uv multi-stage Docker pattern: build with `ghcr.io/astral-sh/uv`, `UV_COMPILE_BYTECODE=1`, `uv sync --locked --no-dev`, copy venv into `python:3.12-slim` (https://hynek.me/articles/docker-uv/, https://docs.astral.sh/uv/guides/integration/docker/ - site blocked, cited via search) |

Docker footprint: `python:3.12-slim` is roughly 45 MB compressed (blog figure, not verified against Docker Hub which hides sizes: https://oneuptime.com/blog/post/2026-02-08-how-to-reduce-docker-image-size-for-python-applications/view). Adding Debian's `ffmpeg` package pulls a large dependency tree (~360 MB Debian vs ~56 MB Alpine per https://github.com/sitkevij/ffmpeg); a static ffmpeg (`mwader/static-ffmpeg`, ~117 MB compressed for amd64: https://github.com/wader/static-ffmpeg) plus `libchromaprint-tools`/`chromaprint` is the usual fix. Expect ~250-450 MB uncompressed.

Precedents (verified):
- Bazarr, the Python member of the arr family: Python 3, Flask 3.1.3 + waitress 3.0.2 (`create_server(app, host, port, threads=100)`), Flask-SocketIO, SQLAlchemy 2.x, APScheduler `BackgroundScheduler` with Interval/Cron triggers; frontend React ^19.3.0 + @mantine/core ^9.6.1 + @tanstack/react-query ^5.101.2 + @tanstack/react-table ^9.2.4 + react-router ^8.4.0 + Vite ^8.3.0 + TypeScript ^7.0.2; GPL-3.0; 4.3k stars.
  - https://raw.githubusercontent.com/morpheus65535/bazarr/master/bazarr/app/server.py
  - https://raw.githubusercontent.com/morpheus65535/bazarr/master/bazarr/app/scheduler.py
  - https://raw.githubusercontent.com/morpheus65535/bazarr/master/frontend/package.json
- Headphones (rembo10): Python, CherryPy, APScheduler; GPL-3.0; 3.8k stars; last push 2025-08-08; v0.6.1 (Nov 2022) was the last release. https://github.com/rembo10/headphones
- Tautulli: Python, CherryPy, APScheduler-compatible cron; GPL-3.0; 6.6k stars. https://github.com/Tautulli/Tautulli
- soularr: Python bridge Lidarr <-> slskd via `pyarr` + `slskd-api`; GPL-3.0; 981 stars; Docker-distributed. https://github.com/mrusse/soularr
- LidaTube (TheWicklowWolf): Python + yt-dlp for Lidarr; GPL-3.0; 364 stars; the same author ships Syncify/SpotTube/ChannelTube on the same stack. https://github.com/TheWicklowWolf/LidaTube

Assessment:
- Performance: adequate. The work is IO-bound (HTTP to slskd/qBittorrent/SAB/Prowlarr, yt-dlp network IO) or done in subprocesses/C libraries (ffmpeg, fpcalc/libchromaprint, mutagen is pure Python but fast enough for per-file tagging). The GIL is not a practical limit for this daemon; Bazarr runs the same shape at scale.
- Single process: uvicorn + FastAPI + in-process APScheduler/asyncio tasks (Bazarr's model) is simple; huey with SqliteHuey if you want durable, retryable jobs without Redis.
- Typing: good but runtime-checked (pydantic, mypy/pyright), weaker than Go/C#/TS.
- Packaging: no single binary; the image must ship the interpreter and deps; uv lockfiles make it reproducible.
- Community: Python is the most-used general language in the 2025 Stack Overflow survey (57.9%, https://survey.stackoverflow.co/2025/technology) and the music-tooling ecosystem (yt-dlp, mutagen, beets, Picard, pyacoustid, ytmusicapi, spotdl, soularr) is overwhelmingly Python, which matters for contributors.
- yt-dlp operational note: releases are frequent and irregular (2026: 01.31, 02.21, 03.03, 03.13, 03.17, 06.09, 07.04, 08.19, i.e. 4-84 days apart: https://github.com/yt-dlp/yt-dlp/releases). Embedding pins the version to the image; shelling out lets users `yt-dlp -U` at runtime at the cost of reproducibility (discussion: https://github.com/uniskela/ts6-manager/pull/66, https://github.com/kieraneglin/pinchflat/issues/867). A Python app can do either (embed by default, optional runtime `pip install -U yt-dlp` into a writable venv).

---

## 3. Candidate B - Go

Versions from proxy.golang.org on 2026-09-28:

| Component | Module | Version (date) | Licence | Notes |
|---|---|---|---|---|
| Router | github.com/go-chi/chi/v5 | v5.3.2 (2026-08-20) | MIT, 22.9k stars | used by Navidrome and autobrr (go.mod below) |
| SQLite (pure Go) | modernc.org/sqlite | v1.59.0 (2026-09-15) | BSD | autobrr uses it; CPU-bound work slower than C (2x on inserts in the 2022 DataStation benchmark; one 2024-26 benchmark shows much worse concurrent-query scaling): https://datastation.multiprocess.io/blog/2022-05-12-sqlite-in-go-with-and-without-cgo.html, https://www.alekseialeinikov.com/en/blog/topics/programming/sqlite-in-go-drivers-wal-locking-production-patterns, https://github.com/cvilsmeier/go-sqlite-bench |
| SQLite (cgo) | github.com/mattn/go-sqlite3 | v1.14.52 (2026-09-05) | MIT | Navidrome uses it with FTS5; needs a C toolchain per arch |
| SQLite (wasm) | github.com/ncruces/go-sqlite3 | v0.35.6 (2026-09-23) | MIT | not evaluated further |
| Scheduler | github.com/robfig/cron/v3 | v3.0.1 (2020-01-04) | MIT | unchanged for 6 years but used by both Navidrome and autobrr; alternative github.com/go-co-op/gocron/v2 v2.22.0 (2026-07-09) |
| Tagging (write, all formats) | go.senan.xyz/taglib (sentriz/go-taglib) | v0.14.0 (2026-07-24) | LGPL-2.1, 94 stars | TagLib 2.x compiled to Wasm, run in-process with wazero: no cgo, static builds; read+write incl. multi-valued tags and cover art; MP3/FLAC/M4A/WAV/OGG/WMA; ~0.3 ms read, ~1.85 ms write per track; used by Navidrome (go.mod has `go.senan.xyz/taglib v0.11.1` with a `deluan/go-taglib` replace) and gonic. https://github.com/sentriz/go-taglib |
| Tagging (pure Go) | github.com/alexballas/tunetag | pseudo-version 2026-09-22 | MIT, 0 stars, 52 commits | claims read+write for MP3/FLAC/M4A/Ogg Opus (with re-paging)/WAV/AIFF/APEv2/WMA with no cgo or Wasm; "approaching feature-complete for v1"; far too new to bet on. https://github.com/alexballas/tunetag |
| Tagging (partial) | dhowden/tag (read-only, last 2024-04-17); bogem/id3v2 v2.1.4 (MP3 write, 2023-02-09); go-flac/go-flac/v2 v2.0.4 (FLAC only) | | | none writes all four formats |
| yt-dlp | github.com/lrstanley/go-ytdlp | v1.5.4 (2026-09-19) | MIT, 327 stars | subprocess wrapper with generated typed flags for every yt-dlp option and helpers that download/cache yt-dlp, ffmpeg, ffprobe binaries with checksums. https://github.com/lrstanley/go-ytdlp |
| Soulseek | github.com/bh90210/soul | v1.1.0 (2025-03-12) | Unlicense, 14 stars | full message (de)serialisation; client does search/download/distributed; "Finish upload" is still a TODO; no room/wishlist search. https://github.com/bh90210/soul |
| Soulseek (others) | llehouerou/gosoulseek (MIT, 2 stars, 21 commits, PoC); a-cordier/goose (14 stars, 2 commits) | | | https://github.com/llehouerou/gosoulseek, https://github.com/a-cordier/goose |
| Fingerprint | none native | | | `fpcalc` subprocess + direct AcoustID HTTP; `acoustid/go-acoustid` is server-side backend code, `zakkor/go-acoustid` a small mp3 tagger (https://github.com/acoustid/go-acoustid, https://github.com/zakkor/go-acoustid) |
| Plex | github.com/LukeHagar/plexgo | v0.29.2 (2026-08-21) | generated from plex-api-spec (MIT) | https://github.com/LukeHagar/plex-api-spec ; also jrudio/go-plex-client |
| YT Music | github.com/prettyirrelevant/ytmusicapi | | | exists; maturity not established |

Deployment: single static binary with the SPA in `embed.FS` (pattern: https://ofeng.org/posts/go-embed-vite/, https://github.com/Darep/golang-react-app-single-binary); scratch/alpine image of ~6-10 MB before ffmpeg/fpcalc (https://github.com/dev-details/go-lang-docker-image-size-comparison, https://medium.com/@minhaz1217/smallest-docker-image-for-go-api-project-b204b1f41d4e); Alpine `ffmpeg` lands around 100 MB on disk (https://www.ffmpeg-micro.com/blog/ffmpeg-in-docker-the-dockerfile-isn-t-the-hard-part).

Precedents (verified from go.mod / package.json / GitHub API):
- Navidrome: Go 1.27, chi v5.3.2, mattn/go-sqlite3 v1.14.52 (cgo, FTS5), go-taglib via wazero v1.12.0, Masterminds/squirrel, robfig/cron v3; UI react ^17.0.2 + react-admin ^3.19.12 + @material-ui/core ^4.12.4 + Vite ^7.3.2; GPL-3.0; 23.9k stars. https://raw.githubusercontent.com/navidrome/navidrome/master/go.mod, https://raw.githubusercontent.com/navidrome/navidrome/master/ui/package.json
- autobrr: Go 1.27, chi v5.3.2, modernc.org/sqlite v1.57.0, lib/pq, robfig/cron v3, squirrel, zerolog; web react ^19.2.8, @tanstack/react-query ^5.102.8, @tanstack/react-router ^1.170.32, @headlessui/react ^2.2.10, tailwindcss ^4.3.3, vite ^8.2.2, typescript ^6.0.3; GPL-2.0; 3.1k stars. https://raw.githubusercontent.com/autobrr/autobrr/develop/go.mod, https://raw.githubusercontent.com/autobrr/autobrr/develop/web/package.json
- gotify/server: Go, 16k stars. https://github.com/gotify/server ; gonic uses go-taglib.

Assessment: best deployment story (static binary, tiny image, trivial multi-arch), excellent concurrency and memory profile, strong typing. Costs: more code to write (routing/DI/validation/migrations are all assembled by hand), no usable embedded Soulseek client (so slskd is mandatory), tagging depends on a Wasm-hosted TagLib (LGPL) rather than a native library, fingerprinting is subprocess-only, and the music-domain ecosystem (YT Music, MusicBrainz helpers) is thin. The "Go can't write tags" objection is no longer true thanks to go-taglib, but a pure-Go all-format writer is not proven.

---

## 4. Candidate C - TypeScript / Node

Versions from registry.npmjs.org on 2026-09-28:

| Component | Package | Version (date) | Licence | Notes |
|---|---|---|---|---|
| Web | hono 4.13.9 (2026-09-24, 32k stars); fastify 5.12.5; @nestjs/core 12.1.0 (76.7k stars) | | MIT | |
| DB | better-sqlite3 13.0.3 (2026-08-05) + drizzle-orm 0.45.3 (2026-09-21) | | MIT / Apache-2.0 | or built-in `node:sqlite`: added v22.5.0, flag-free since v22.13/v23.4, "Stability: 1.2 - Release candidate" as of v25.7.0 (https://nodejs.org/api/sqlite.html) |
| Jobs | BullMQ needs Redis (well known); pg-boss needs Postgres; sidequest 1.16.5 (LGPL-3.0-or-later, supports SQLite per its site https://bunqueue.dev/blog/why-bunqueue and the Better Stack comparison); bree 9.2.9 (worker threads, no DB, no persistence); croner 10.0.1 / node-cron (no persistence); Seerr uses node-schedule 2.1.1 | | | https://betterstack.com/community/guides/scaling-nodejs/best-nodejs-schedulers/ |
| Tagging (read) | music-metadata 11.16.1 (2026-09-24) | | MIT, 1.3k stars | parser only (https://github.com/Borewit/music-metadata) |
| Tagging (MP3 only) | node-id3 0.2.9 (2025-04-03) | | MIT | ID3v2 write only |
| Tagging (all formats) | node-taglib-sharp 6.0.3 (2026-04-12) | | LGPL-2.1-or-later, 52 stars | TS port of TagLib#; R/W MP3 (ID3v2/APE), FLAC (Xiph), M4A (iTunes atoms), OGG (Xiph comments incl. Opus) (https://github.com/benrr101/node-taglib-sharp) |
| Tagging (all formats, Wasm) | taglib-wasm 2.3.0 (2026-09-18) | | MIT for TS, LGPL-2.1-or-later for the Wasm binary; 38 stars | TagLib 2.3.2; R/W MP3/FLAC/M4A/Ogg incl. Opus; cover art; Node WASI backend does seek-based I/O and `applyTagsToFile()` writes in place; README says Node >= 24 for WASI; not thread-safe (https://github.com/CharlesWiltgen/taglib-wasm) |
| yt-dlp | youtube-dl-exec 3.1.15 (2026-09-05) | | MIT, 616 stars | promise wrapper, downloads the yt-dlp binary; yt-dlp-wrap is unmaintained (last publish 2023-09-13) (https://github.com/microlinkhq/youtube-dl-exec, https://www.npmjs.com/package/yt-dlp-wrap) |
| Soulseek | slsk-client 2.0.2 (2026-09-14) | | MIT, 212 stars, repo since 2017 | rewritten in TypeScript as a 2.x line: promise API, search with attribute filters, downloads with resume/retry, sharing/upload slots, private messages, protocol-compliance report (https://github.com/f-hj/slsk-client, package.json shows `typescript ^7.0.2`, `dist/index.d.ts`). Very fresh rewrite; soulseek-ts (12 stars) does search+download only (https://github.com/jgchk/soulseek-ts) |
| Plex | @lukehagar/plexjs 0.43.0 (2025-11-13) | | generated SDK | https://github.com/LukeHagar/plex-api-spec |
| YT Music | ytmusic-api 5.3.1 (2026-02-13) | | GPL-3.0 (!) | https://www.npmjs.com/package/ytmusic-api ; @codyduong/ytmusicapi is archived |

Precedents (verified):
- Seerr (Jellyseerr/Overseerr lineage): next 16.2.6, react 19.2.6, express 5.2.1, typeorm 0.3.31, sqlite3 ^5.1.7 / pg 8.23.0, node-schedule 2.1.1, tailwindcss 3.4.19, @headlessui/react 2.2.10, @heroicons/react 2.2.0, swr 2.4.1; MIT; 12.7k stars. Not NestJS. https://raw.githubusercontent.com/seerr-team/seerr/develop/package.json
- Maintainerr: TypeScript, MIT, 2.3k stars, data at `/opt/data`, runs as `user: 1000:1000` (no PUID/PGID), health endpoint `/api/health/ready`. https://github.com/jorenn92/Maintainerr (the NestJS+Next.js split is general knowledge, not re-verified here)
- Tunarr: TypeScript pnpm/Turbo monorepo with ffmpeg engine (search snippet: https://dev.co/devops/open-source/tunarr)

Assessment: one language across API and UI and a huge contributor pool (TypeScript is GitHub's most-used language since Aug 2025 per the survey snippet). Durable job scheduling without Redis is the weak spot (roll your own on SQLite, or adopt Sidequest which is LGPL); tag writing works via a TagLib port or Wasm build with small communities; a native Soulseek client exists but is a weeks-old rewrite; the Node runtime makes the image comparable to Python's. Performance is fine for this workload.

---

## 5. Candidate D - C# / .NET

Versions from api.nuget.org on 2026-09-28:

| Component | Package | Version | Licence | Notes |
|---|---|---|---|---|
| Runtime | .NET 10 LTS (to Nov 2028) | | | slskd targets net10.0 today |
| DB | Microsoft.EntityFrameworkCore.Sqlite 10.0.12 / Microsoft.Data.Sqlite 10.0.12 (11.0 RC1 also published) | | MIT | or the arr way: Dapper 2.1.89 + FluentMigrator.Runner.SQLite 8.0.1 |
| Scheduler | Quartz 4.2.1 | | Apache-2.0, 7.1k stars | AdoJobStore supports the `SQLite-Microsoft` provider with `Quartz.Impl.AdoJobStore.SQLiteDelegate`; caveat that Microsoft.Data.Sqlite does not enlist in TransactionScope (https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html, https://github.com/quartznet/quartznet/issues/517) |
| Scheduler (alt) | Hangfire.Core 1.8.25 + Hangfire.Storage.SQLite 0.4.3 (2026-03-01) | | Hangfire: LGPL-3.0 or commercial (https://raw.githubusercontent.com/HangfireIO/Hangfire/main/LICENSE.md); storage: MIT, 178 stars, community-maintained, 15 s queue polling, .NET 8 (https://github.com/raisedapp/Hangfire.Storage.SQLite) | Hangfire's site disclaims community storages |
| Scheduler (simplest) | `IHostedService` + `PeriodicTimer` + own command queue | | | what the arrs do (NzbDrone.Core/Jobs, Messaging/Commands) |
| Soulseek (embedded) | Soulseek (Soulseek.NET) 10.0.2 (2026-06-10) | | GPL-3.0-only + Additional Terms; 230 stars; 6,491 commits; slskd's engine | see 5.1 |
| Tagging | z440.atl.core (ATL) 7.17.0 | | MIT, 576 stars, fully managed, .NET Standard 2.1 / .NET 6+ | R/W tags and pictures for MP3, FLAC, M4A/MP4/AAC, OGG Opus, WAV and many more (https://github.com/Zeugma440/atldotnet) |
| Tagging (alt) | TagLibSharp 2.3.0 | | LGPL-2.1, 1.5k stars | only 3 NuGet versions ever; last push 2025-05-31 (https://github.com/mono/taglib-sharp) |
| Fingerprint | AcoustID.NET 1.3.3 | | LGPL-2.1, 45 stars | managed port of chromaprint plus lookup/submit web client; needs an `IDecoder` (NAudio example) so ffmpeg-decoded PCM is still required; `fpcalc` subprocess remains the simpler route (https://github.com/wo80/AcoustID.NET) |
| yt-dlp | YoutubeDLSharp 1.2.0 | | BSD-3, 251 stars, last push 2026-01-05 | `YoutubeDL`/`YoutubeDLProcess`, `OptionSet`, progress + cancellation, can download yt-dlp/ffmpeg binaries (https://github.com/Bluegrams/YoutubeDLSharp) |
| YouTube (native, no yt-dlp) | YoutubeExplode 6.6.2 | | LGPL-3.0 (general knowledge) | pure .NET extractor; breaks like any scraper (https://www.nuget.org/packages/YoutubeExplode) |
| Plex | LukeHagar.PlexAPI.SDK 0.21.0; Plex.Api 4.1.2 | | | https://www.nuget.org/packages/LukeHagar.PlexAPI.SDK, https://www.nuget.org/packages/Plex.Api/ |
| YT Music | YTMusicAPI 1.0.10 | | | https://www.nuget.org/packages/YTMusicAPI |

### 5.1 Soulseek.NET: the big plus and its strings
- Feature-complete, production-proven client (it is what slskd runs): search, browse, download, upload/shares, rooms, private messages, distributed network; the README also expects downstream apps to honour the server's "excluded search phrases" list. https://github.com/jpdillingham/Soulseek.NET
- Release 10.0.0 (Apr 2026) relicensed from GPLv3-or-later to GPLv3-only with GPL section-7 Additional Terms. The terms (per the release notes) require: "Any Covered Software that connects to the Soulseek network must transmit a client version identifier that is unique to that software and not already in use by any known Soulseek client implementation" (major version hard-coded to 170, minor must be > 100 and registered in the README's reserved ranges; slskd holds 760-7699999). The maintainer: "I fully intend to police this; anyone that fails to use a unique version will have their license revoked." https://github.com/jpdillingham/Soulseek.NET/releases/tag/10.0.0 ; NOTICE: https://raw.githubusercontent.com/jpdillingham/Soulseek.NET/master/NOTICE
- NuGet status (checked directly against the NuGet v3 registration/catalog API on 2026-09-28): every version from 8.5.0 through 10.0.2 is `listed: false` and carries a deprecation record with reason `CriticalBugs` (catalog `lastEdited` 2026-04-18 for 8.5.0-9.1.0 and 2026-08-09 for 10.0.0-10.0.2). The NuGet page therefore shows "This package has been deprecated as it has critical bugs" and "The owner has unlisted this package". No issue, discussion or release note explains it (searched https://github.com/jpdillingham/Soulseek.NET/issues?q=is%3Aissue+nuget and https://github.com/jpdillingham/Soulseek.NET/discussions). slskd master still references `Soulseek 10.0.2` (unlisted packages still restore by exact version). Treat this as a real supply-chain/governance risk: single maintainer, aggressive licence enforcement, and a package that is currently withdrawn from discovery. https://www.nuget.org/packages/Soulseek/10.0.2 ; https://api.nuget.org/v3/registration5-gz-semver2/soulseek/index.json
- The Soulseek network's own rules (search snippet of https://www.slsknet.org/news/node/681, site blocked here) forbid "automated clients (robot/bot) ... or scripts otherwise failing to implement the full range of Soulseek features" and only tolerate alternative clients that implement "chat, search, wishlist, download, upload, and respect / recognition of privileges". An embedded client therefore has to share/upload too; delegating to slskd (a full client) side-steps that obligation for the new app.

Docker: `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` ~107 MB, Debian ~224 MB; self-contained + trimmed + chiseled samples 28-47 MB (search snippets: https://dev.to/mfund0/choosing-the-right-net-image-for-your-workloads-2ino, https://github.com/dpbevin/dotnet-staticfiles, https://github.com/dotnet/dotnet-docker/blob/main/documentation/sample-image-size-report.md). Plus ffmpeg/fpcalc as for the others.

Precedents: slskd (net10.0, EF Core 10, Serilog, SignalR, Soulseek 10.0.2; web: react ^16.8.6, semantic-ui-react ^2.1.0, react-scripts ^5 (CRA), @microsoft/signalr ^7; AGPL-3.0 with "Additional Terms" appended in 0.25.0 alongside "Upgrade to .NET 10" and "Bump Soulseek.NET to 10.0.0"; 4.0k stars) - https://raw.githubusercontent.com/slskd/slskd/master/src/web/package.json, https://github.com/slskd/slskd/releases ; all Servarr apps; Listenarr (C#, audiobook arr, 900 stars) - https://github.com/Listenarrs/Listenarr.

Assessment: the most complete library coverage of the four (ATL is the cleanest MIT-licensed all-format tagger in any language here; Quartz/Hangfire/hosted services for jobs; EF Core or the exact arr data stack), the closest reuse of arr patterns, LTS runtime, small trimmed images. The one unique advantage, embedding Soulseek.NET, is also its biggest risk (GPL-3.0-only + policed version terms + unlisted/deprecated package). It also means two languages (C# + TS) and a smaller self-hosted contributor pool than Python/TS, though the arrs prove it works.

---

## 6. Frontend

Current versions (npm, 2026-09-28): react 19.3.0 (2026-09-09), vite 8.3.1, @tanstack/react-query 5.104.0, @tanstack/react-table 9.2.4, lucide-react 1.48.0 (ISC).

What comparable self-hosted apps actually use (verified package.json):
- Bazarr: React 19 + Mantine 9 (core, form, modals, notifications, spotlight, dropzone) + TanStack Query 5 + TanStack Table 9 + react-router 8 + Vite 8.
- autobrr: React 19 + TanStack Query 5 + TanStack Router + Headless UI 2 + Tailwind 4 + Heroicons + Vite 8.
- Seerr: Next.js 16 + React 19 + Tailwind 3.4 + Headless UI + Heroicons + SWR.
- Navidrome: react-admin 3 + Material-UI v4 (React 17) on Vite 7.
- Sonarr/Lidarr: React 18 + Redux + webpack 5, hand-rolled components.
- slskd: React 16 + Semantic UI React on CRA.

Library choice: shadcn/ui is copy-in components on Tailwind with Lucide as its default icon set and a TanStack-Table-based data-table recipe (https://ui.shadcn.com/docs/components/base/data-table, https://tanstack.com/table/latest/docs/framework/react/examples/lib-shadcn-base); Mantine 9 ships far more ready-made data-heavy components (tables, forms, modals, notifications, spotlight) with CSS modules and no Tailwind, and 2026 comparisons frame it as "Mantine = speed, shadcn = control" (https://www.shadcndeck.com/blog/mantine-vs-shadcn, https://designrevision.com/compare/mantine-vs-shadcn). For an arr UI (dense tables, wanted lists, search modals, queue/activity, big settings forms) Mantine (the Bazarr precedent) or shadcn + TanStack Table are both sound; SvelteKit/Vue have no evidenced precedent among arr-adjacent apps and were not researched further.

---

## 7. Deployment conventions

- Path layout: the Servarr Docker guide says "Pick one path layout and use it for all of them. It's suggested to use `/data`" with `/data/torrents/{tv|movies|music|books}`, `/data/usenet/...`, `/data/media/{Movies|TV|Music|Books}` so that "Hard links will work and moves will be atomic, instead of copy + delete"; it recommends "an eponymous user per daemon and a shared group with a umask of `002`" (dirs 775 / files 664) and `/config` owned by the daemon user with umask 022 or 077. https://raw.githubusercontent.com/Servarr/Wiki/master/docker-guide.md
- LinuxServer.io convention: s6-overlay v3, user `abc` (911:911) remapped at start via `PUID`/`PGID`, optional `UMASK`, `/config` volume; the init chowns `/app`, `/config`, `/defaults` (search snippets: https://docs.linuxserver.io/general/understanding-puid-and-pgid/, https://docs.linuxserver.io/general/running-our-containers/). LSIO lidarr example above uses `/config`, `/music`, `/downloads`. hotio images also use s6-overlay but do not support LSIO `DOCKER_MODS` (search snippets: https://hotio.dev/faq/, https://docs.theme-park.dev/setup/).
- Simple non-root alternative: Maintainerr runs as `user: 1000:1000` with a single data dir and no PUID/PGID logic. https://github.com/jorenn92/Maintainerr
- slskd compose: ports 5030 (HTTP), 5031 (HTTPS), 50300 (Soulseek listen), volume `/app`, `SLSKD_REMOTE_CONFIGURATION=true`, optional `PUID`/`PGID`; API keys configured under `web.authentication.api_keys` with a `X-API-Key` header and optional CIDR restriction; Swagger at `/swagger` with `--swagger`/`SLSKD_SWAGGER`; slskd warns API keys without HTTPS are "NOT RECOMMENDED". https://raw.githubusercontent.com/slskd/slskd/master/README.md, https://raw.githubusercontent.com/slskd/slskd/master/docs/config.md
- Health: Sonarr v4 exposes unauthenticated `/ping` (empty 200, readiness) and `/api/v3/health` (non-200 when a critical health check fails); a typical HEALTHCHECK is `wget --spider http://localhost:8989/ping` (https://github.com/Sonarr/Sonarr/issues/5396, https://somedaysoon.xyz/posts/tech/healthchecks/). Maintainerr uses `/api/health/ready`.
- Reverse proxy / auth: Sonarr's "URL Base" ("For reverse proxy support, default is empty"; enter `/sonarr` for `mydomain.com/sonarr`), Authentication = Forms or Basic with "Enabled" or "Disabled for Local Addresses", and an API key that is redacted in logs. https://raw.githubusercontent.com/Servarr/Wiki/master/sonarr/settings.md
- Unraid Community Applications: an XML template per app, e.g. LSIO's `lidarr.xml`: `<Container version="2">` with Name, Repository, Registry, Network, Shell, Privileged, Support, Project, Overview, Category, WebUI (`http://[IP]:[PORT:8686]/system/status`), TemplateURL, Icon, ExtraParams and `<Config Name=... Target=... Default=... Mode=... Description=... Type=Port|Path|Variable Display=always|advanced Required=... Mask=...>` entries for the port, `/config`, `/music`, `/downloads`, `PUID`, `PGID`, `UMASK`. https://raw.githubusercontent.com/linuxserver/templates/master/unraid/lidarr.xml (CA's own docs at ca.unraid.net/docs.unraid.net were blocked; format guide via search: https://selfhosters.net/docker/templating/templating/)

---

## 8. Licensing landscape (GitHub API / registry metadata, 2026-09-28)

| Project | Licence |
|---|---|
| Sonarr, Radarr, Lidarr, Prowlarr | GPL-3.0 |
| Bazarr, Tautulli, Headphones, soularr, LidaTube, Navidrome | GPL-3.0 |
| autobrr | GPL-2.0 |
| slskd | AGPL-3.0 + Additional Terms (since 0.25.0) |
| Soulseek.NET | GPL-3.0-only + Additional Terms (unique version identifier, policed) |
| aioslsk | GPL-3.0-or-later |
| slsk-client (npm) | MIT |
| bh90210/soul | Unlicense |
| slskd-api (PyPI wrapper) | AGPL-3.0 |
| yt-dlp | Unlicense (public domain) |
| mutagen | GPL-2.0-or-later |
| ATL (z440.atl.core) | MIT |
| TagLib# / node-taglib-sharp / go-taglib | LGPL-2.1 |
| taglib-wasm | MIT (TS) + LGPL-2.1-or-later (Wasm) |
| music-metadata, node-id3, pyacoustid, ytmusicapi (Python), plexapi (BSD-3), APScheduler, huey, FastAPI, Litestar, chi, Hono, NestJS, YoutubeDLSharp (BSD-3), go-ytdlp, youtube-dl-exec | MIT/BSD (permissive) |
| ytmusic-api (npm) | GPL-3.0 |
| Hangfire.Core | LGPL-3.0 or commercial |
| Quartz.NET | Apache-2.0 |
| Seerr, Maintainerr | MIT |

Implications:
- Linking an embedded Soulseek client (Soulseek.NET or aioslsk) or mutagen makes the app a GPL derivative in the FSF's reading, so the app must be GPL-3.0 (GPL-3.0 is compatible with GPL-2.0-or-later and GPL-3.0-or-later). That matches the arr convention (all GPL-3.0) and Bazarr/Navidrome.
- Talking to slskd over HTTP is arm's-length use of a separate program and carries no copyleft into the app; but the convenient Python `slskd-api` wrapper is AGPL-3.0, so write a thin client instead if you want to keep licence options open.
- Permissive stacks are possible (ATL MIT + slsk-client MIT + music-metadata MIT + youtube-dl-exec MIT, or Go with go-taglib LGPL dynamic-ish via Wasm), and MIT is the norm for the Seerr family. Recommendation: GPL-3.0, because it is the arr norm, it keeps every option open (mutagen, aioslsk, Soulseek.NET, GPL ports), and self-hosters are used to it.

---

## 9. Comparison table

Ratings: 5 = best. Evidence is in the sections above.

| Criterion | A Python | B Go | C TypeScript | D C#/.NET |
|---|---|---|---|---|
| Soulseek library maturity (embedded) | 3 - aioslsk 1.6.4, Beta, full features incl. upload/rooms, 66 stars | 1 - bh90210/soul: upload unfinished, 14 stars | 2 - slsk-client 2.0.2, full features but a fresh TS rewrite, 212 stars | 5* - Soulseek.NET, production-proven in slskd; *GPL-3.0-only + policed terms, NuGet package unlisted/deprecated |
| Soulseek via slskd HTTP API | 5 (soularr precedent) | 5 | 5 | 5 |
| Tagging write coverage MP3/FLAC/M4A/Opus | 5 - mutagen (GPL) | 4 - go-taglib (TagLib-in-Wasm, LGPL) | 4 - taglib-wasm / node-taglib-sharp (small communities) | 5 - ATL (MIT, managed) |
| yt-dlp integration | 5 - native in-process API | 4 - go-ytdlp typed subprocess | 4 - youtube-dl-exec | 4 - YoutubeDLSharp |
| Fingerprinting | 5 - pyacoustid (lib or fpcalc) | 3 - fpcalc subprocess + raw HTTP | 3 - fpcalc + raw HTTP | 4 - AcoustID.NET managed chromaprint or fpcalc |
| Scheduler / durable jobs without Redis | 5 - APScheduler 3.x, huey SqliteHuey | 4 - cron libs + own SQLite table | 3 - DIY or Sidequest (LGPL) | 5 - Quartz AdoJobStore/Hangfire SQLite/hosted services |
| Plex / YT Music / MusicBrainz clients | 5 - plexapi, ytmusicapi (canonical) | 3 - plexgo, thin YT Music port | 3 - plexjs, ytmusic-api (GPL) | 4 - two Plex SDKs, YTMusicAPI |
| Single binary / image size | 2 - interpreter + deps, ~300-450 MB with ffmpeg | 5 - static binary, ~100 MB with ffmpeg | 2 - Node runtime | 4 - trimmed self-contained ~30-50 MB base |
| Dev velocity for this app | 5 | 3 | 4 | 4 |
| Community familiarity (self-hosted music) | 5 - Bazarr, Headphones, Tautulli, soularr, LidaTube, beets/Picard ecosystem | 4 - Navidrome, autobrr, gotify | 4 - Seerr, Maintainerr, Tunarr | 4 - all arrs, slskd, Jellyfin |
| Raw performance / memory | 2 | 5 | 3 | 4 |
| Type safety | 3 | 5 | 4 | 5 |
| Licence flexibility | 3 (mutagen/aioslsk GPL) | 4 | 4 | 3 (Soulseek.NET GPL + terms) |

---

## 10. Recommendation

Recommended: Stack A (Python), specifically FastAPI (or Litestar) + SQLAlchemy 2 + Alembic + SQLite, APScheduler 3.x `AsyncIOScheduler` for periodic work with a small SQLite-backed task table (or huey/SqliteHuey) for durable per-song jobs, yt-dlp embedded via `YoutubeDL` (pinned per image, optional runtime self-update), mutagen for tags, pyacoustid + fpcalc, python-plexapi, ytmusicapi, a thin httpx client for slskd (avoid the AGPL wrapper), qBittorrent/Transmission/Deluge/SABnzbd/NZBGet/Torznab over their HTTP APIs, and a React 19 + Vite + TanStack Query/Table + Mantine (or shadcn) SPA served from the same container. Image: `python:3.12-slim` via uv multi-stage + static ffmpeg + chromaprint, PUID/PGID or plain `user:`, `/config`, `/data` layout, `/ping` and `/api/v1/health`, URL-base support. Licence GPL-3.0.

Why:
1. Every domain-specific dependency the app needs is first-class and canonical in Python: yt-dlp is Python, mutagen is the reference tagger for all four formats, pyacoustid/chromaprint, python-plexapi and ytmusicapi are the libraries the other ecosystems port from, and the Soulseek-for-arrs precedent (soularr, LidaTube, Bazarr's whole architecture) is Python.
2. The primary Soulseek path is slskd's HTTP API, which neutralises Stack D's only unique advantage while avoiding the Soulseek.NET licence-policing and NuGet-unlisting risk and the network's "full-feature client" rule (slskd carries those obligations). If embedding later becomes necessary, aioslsk (Beta, full-featured, asyncio) exists behind the same provider interface.
3. The workload is IO- and subprocess-bound, so Python's runtime cost is not a bottleneck; Bazarr proves the single-process Flask/waitress/APScheduler shape at arr scale, and FastAPI/asyncio is a step up from that.
4. Contributor pool and iteration speed: Python is the most-used language among developers and the language most self-hosted music tooling is written in, and the app is mostly glue code around external services, exactly where Python is fastest to write and review.
5. Costs are real but acceptable for a Docker-first target: no single binary, a 300-450 MB image, weaker static typing (mitigated with pydantic + pyright), and copyleft libraries (mutagen) which push toward GPL-3.0, the arr norm anyway.

Honest case for the runner-up, Stack D (C#/.NET 10):
- It is the only stack with a production-grade embeddable Soulseek client; if the product must own its Soulseek connection (no slskd sidecar), C# is the rational choice and slskd itself is the proof.
- ATL is the cleanest tagging option of all four (MIT, fully managed, every format, pictures), AcoustID.NET gives managed chromaprint, Quartz/Hangfire give mature persistent scheduling, EF Core + SQLite is trivial, and .NET 10 is LTS to 2028 with tiny trimmed images.
- It reuses the arrs' architecture most directly (quality profiles, decision engine, command queue, API-key auth, `/ping`), so a contributor who knows Sonarr's code will feel at home, and Prowlarr/Torznab/download-client integrations can be ported almost line-for-line.
- Against it: Soulseek.NET is GPL-3.0-only with policed unique-version terms and has been unlisted/deprecated on NuGet since mid-2026 with no public explanation; two languages (C# + TS); and the self-hosted music ecosystem (yt-dlp, ytmusicapi, plexapi) is one wrapper further away than in Python.

Go is the choice if the top priority is a single static binary with a ~100 MB image and the lowest memory footprint, accepting slskd as mandatory, Wasm-hosted TagLib for tags, and more hand-written plumbing. TypeScript is viable (Seerr-style) but has the weakest story for durable local job queues and depends on very small tagging/Soulseek libraries.

Things this research could not verify in-session: exact `python:3.12-slim` image size; the LinuxServer healthcheck documentation page; the hotio and Unraid CA documentation pages; the reason behind the Soulseek NuGet deprecation; TechEmpower-style throughput numbers (not consulted).

---

## Appendix: late verifications (2026-09-28)

### DryIoc in the arrs (direct csproj grep)
- Sonarr: `DryIoc.dll 5.4.3` in `src/NzbDrone.Common/Sonarr.Common.csproj`; `DryIoc.dll 5.4.3` + `DryIoc.Microsoft.DependencyInjection 6.2.0` + `Microsoft.AspNetCore.Owin 6.0.21` in `src/NzbDrone.Host/Sonarr.Host.csproj`; also Sentry 4.0.2, NLog.Layouts.ClefJsonLayout, NLog.Targets.Syslog, Microsoft.Extensions.Hosting.WindowsServices 6.0.2.
- Lidarr: `DryIoc.dll 5.4.3` + `DryIoc.Microsoft.DependencyInjection 6.2.0` in Common/Host; `System.Data.SQLite 2.0.4`; Sentry 5.16.3; Hosting.WindowsServices 8.0.1.
- URLs: https://raw.githubusercontent.com/Sonarr/Sonarr/develop/src/NzbDrone.Common/Sonarr.Common.csproj , https://raw.githubusercontent.com/Sonarr/Sonarr/develop/src/NzbDrone.Host/Sonarr.Host.csproj , https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Common/Lidarr.Common.csproj , https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Host/Lidarr.Host.csproj
- Conclusion: the arr stack is ASP.NET Core (Owin shim retained) + DryIoc container + Dapper + FluentMigrator + System.Data.SQLite (Servarr fork on Sonarr) + NLog + Sentry, React/Redux/webpack frontend.

### Soulseek.NET "ADDITIONAL TERMS" (verbatim excerpts from the tail of LICENSE, https://raw.githubusercontent.com/jpdillingham/Soulseek.NET/master/LICENSE)
The LICENSE file appends an "ADDITIONAL TERMS" section after the GPLv3 text. It states that compliance "is a material condition of the License grant" and that "Any use, modification, or conveyance of the Program, or any instance of Covered Software, without adherence to these Additional Terms is a violation of the License and is subject to the termination and reinstatement provisions of Section 8." "Covered Software" is defined to include "any software that incorporates the Program or any portion thereof, whether or not modified" (so an app that embeds the library is covered). Section 1 disclaims endorsement of unauthorised sharing and adds an indemnification clause; Section 2 requires preservation of interface notices "including terminal startup output and web-based dashboards".

Unique client version clause (grep "version identifier", line numbers from the file):
```
878-  from the original author(s).
879-
880:  5. Client Version Identifier (Section 7(c))
881-
882-  Any Covered Software that connects to the Soulseek network must transmit
883:a client version identifier that is unique to that software and not already
884-in use by any known Soulseek client implementation, including but not limited
885-to the Program itself.  The Soulseek server and its administrators rely on
886:the client version identifier to distinguish between client implementations
887-and to enforce network policies; transmitting an identifier already associated
888-with another client constitutes a false representation of the software's
889-identity and implies endorsement or affiliation with that client's authors,
890-which is expressly disclaimed.
891-
892-  Any person or entity that conveys or deploys Covered Software bears
893-responsibility for making a reasonable good-faith effort to verify that their
894-chosen identifier is unique prior to deployment.  If notified that their
895-chosen identifier conflicts with that of an existing client implementation,
896-they must change it to a unique identifier within a reasonable time and must
897-not continue to distribute or operate the Covered Software with the conflicting
898-identifier after becoming aware of the conflict.  This condition applies
899-regardless of the technical means by which the identifier is set or
900-transmitted.
```

Termination/revocation language near the tail (grep "revok|terminat" after line 674):
```
703-conditions under Section 7 of the GPLv3, is a material condition of the
704-License grant.  Any use, modification, or conveyance of the Program, or any
705-instance of Covered Software, without adherence to these Additional Terms is a
706:violation of the License and is subject to the termination and reinstatement
707-provisions of Section 8.
708-
709-  1. Disclaimer of Endorsement and Limitation of Liability (Sections 7(a) and 7(f))
710-
711-  Pursuant to Sections 7(a) and 7(f), the original author(s) of the Program
712-do not condone, endorse, or support the unauthorized sharing of copyrighted
713-works or the transmission of any unlawful or illicit materials, whether through
714-the Program or any Covered Software.
```
