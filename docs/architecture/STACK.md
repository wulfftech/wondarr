# Tech stack and code porting

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the stack decision and the list of code to port. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

### 8.1 What the *arrs are, and why not fork Lidarr

Sonarr, Radarr, Lidarr and Prowlarr are C#/ASP.NET Core (Radarr/Lidarr/Prowlarr on `net8.0`, Sonarr v4 still on `net6.0`), Dapper + FluentMigrator on SQLite/Postgres, DryIoc, NLog, and a React 18 + Redux + webpack front end; all GPL-3.0. There is no shared "Servarr" package: the forks stay in sync by a cherry-picking bot, and extracting the core was closed as not planned ([Sonarr#7528](https://github.com/Sonarr/Sonarr/issues/7528)). Lidarr is "Sonarr but made for music" and measures about 95k lines of C# in Core (264 files touching Album/Artist/Track), 82 migrations and 103k lines of front end; changing its unit from album to recording would rewrite the domain, parser, decision engine, metadata source, import pipeline, API and most of the UI. **Decision: do not fork Lidarr; build new and port its reusable subsystems.**

### 8.2 The Soulseek library question

The only production-grade embeddable Soulseek client is Soulseek.NET (C#, the engine inside slskd). Since April 2026 it is GPL-3.0-only with policed "Additional Terms" requiring each embedding app to transmit its own registered client version, and every NuGet version is currently unlisted and flagged deprecated with no public explanation ([release 10.0.0](https://github.com/jpdillingham/Soulseek.NET/releases/tag/10.0.0), [nuget.org](https://www.nuget.org/packages/Soulseek/10.0.2)). The network's rules also expect a full client that shares. **Decision: drive slskd over its HTTP API** (packaged with the app, §9); the app never embeds a Soulseek client, so this question no longer constrains the language.

### 8.3 Decision: C#/.NET 10 + React 19 (aligned with the *arrs)

The owner's preference is alignment with the other *arrs; with slskd external, C# carries no Soulseek-library risk, and it turns the *arrs' GPL-3.0 code base into a parts bin:

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS to Nov 2028) | slskd already targets it; Lidarr is on .NET 8 so ported code needs no downgrade |
| Web/API | ASP.NET Core controllers + OpenAPI, SignalR for live queue/search events | SignalR is what the *arrs and slskd use for push updates |
| Data | EF Core 10 + SQLite (WAL), migrations in-repo | Simpler than the *arrs' Dapper + FluentMigrator + Marr mini-ORM; ported classes bring their logic, not their persistence |
| Jobs | Hosted services + a SQLite-backed command queue exposing `/api/v1/command` semantics; Quartz.NET for cron-style schedules | Mirrors the *arrs' command/task model that dashboards understand |
| DI / logging | Microsoft.Extensions.DependencyInjection; Serilog (JSON + rolling files) with key redaction | |
| Tagging | ATL (`z440.atl.core`, MIT, managed, writes MP3/FLAC/M4A/Opus with pictures and lyrics); TagLib# as fallback | |
| Fingerprinting | `fpcalc` subprocess + AcoustID HTTP (Lidarr's `FingerprintingService` is directly portable); AcoustID.NET optional | |
| yt-dlp | YoutubeDLSharp (BSD-3) subprocess wrapper with `-J`/`--print` parsing; Deno bundled in the image | |
| YouTube Music search | Small InnerTube client in C# (port of ytmusicapi's `search` parser for the `songs`/`videos` filters); the `YTMusicAPI` NuGet package is evaluated first | ytmusicapi is a thin wrapper over `music.youtube.com/youtubei/v1/search` |
| MusicBrainz / CAA / Deezer / iTunes / LRCLIB / ListenBrainz / Last.fm | Thin typed `HttpClient`s with a shared rate limiter and the metadata cache | |
| Plex | Port Lidarr's `PlexServerProxy` (sections, partial refresh) + `emptyTrash`; playlists via `/playlists/upload` | |
| Torrents / usenet | Port Lidarr's qBittorrent and SABnzbd proxies and its Torznab/Newznab request generators, caps and RSS parsers | |
| Frontend | React 19 + TypeScript + Vite + TanStack Query/Table + Mantine, Lucide icons; *arr-style shell (Library / Wanted / Activity / Settings / System) | Lidarr's own React 18 + Redux + webpack UI is not worth porting; conventions are |
| Packaging | Multi-stage build; self-contained trimmed publish on `mcr.microsoft.com/dotnet/aspnet:10.0` (or Alpine) + static ffmpeg/ffprobe + chromaprint `fpcalc` + Deno; multi-arch (amd64, arm64) | |

**Runner-up: Python (FastAPI + React)** as in revision 1: every music library is native there (yt-dlp, mutagen, pyacoustid, ytmusicapi, plexapi), SoulSync and spotDL code could be reused directly rather than ported, and Bazarr proves the shape. It loses on the owner's alignment preference and on the size of the portable *arr code base.

### 8.4 What gets ported, from where

| Source (licence) | Take | How |
|---|---|---|
| Lidarr, Prowlarr, Sonarr (GPL-3.0) | qBittorrent + SABnzbd clients; Newznab/Torznab request generators, caps and RSS parsers, category tables; `QualityParser` and release-name regexes; `FileNameBuilder` token engine and illegal-character rules; notification providers (Webhook, Discord, Apprise, Email, Telegram, Plex…); health-check framework; API-key/Forms auth handlers, URL base, `/ping`; disk transfer (hardlink/move/copy), permissions, recycle bin; remote path mappings; decision-specification pattern with permanent/temporary rejections; backup service; `FingerprintingService` | Copy with attribution, adapt to EF Core and our domain; app licensed GPL-3.0 so this is clean |
| SoulSync (MIT, Python) | Fake-lossless detector approach, version-aware title matching heuristics, quality-ladder presets | Port logic to C#; keep attribution |
| spotDL (MIT, Python) | YouTube Music matching rules (ISRC-first, verified-result early return, forbidden-word penalties, duration decay) | Port rules (§6) |
| Sockseek (AGPL-3.0, C#) | Ranking order and search-query strategy | **Re-implement from the documented behaviour; do not copy code**, to keep the app plainly GPL-3.0 |
| Exportify (MIT) | CSV column contract | Read only |

### 8.5 Licence

GPL-3.0, public repository. Required by the ported Lidarr/Prowlarr code and consistent with the *arr convention; slskd (AGPL-3.0 + additional terms) runs as a separate program and is redistributed unmodified in the bundled image with its notices and source offer.
