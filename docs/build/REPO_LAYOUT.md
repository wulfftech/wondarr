# Intended repository layout

Created incrementally from Phase 0 onward (docs/build/PHASE_0_TASKS.md). Names are binding unless an ADR changes them.

```
wondarr/
├── AGENTS.md                      # AI session instructions (orchestrator + workers)
├── CLAUDE.md                      # one-line pointer at AGENTS.md (Claude Code auto-loads this name)
├── README.md · LICENSE (GPL-3.0) · NOTICE.md (ported-code attributions) · CHANGELOG.md
├── .env.example · .editorconfig · .gitignore
├── global.json                    # pins the .NET 10 SDK
├── Directory.Build.props          # nullable, warnings-as-errors, analyzers, InvariantGlobalization
├── Directory.Packages.props       # central package versions
├── Wondarr.sln
├── src/
│   ├── Wondarr.Core/            # domain, decision engine, import pipeline, metadata, organizer, tagging (no ASP.NET)
│   │   ├── Domain/                # Song, Artist, AlbumContext, Quality, Profiles, Library, ReferenceLibrary…
│   │   ├── Metadata/              # MusicBrainz, CAA, Deezer, iTunes, AcoustID, LRCLIB clients + cache + rate limiter
│   │   ├── Decision/              # Candidate, specifications (rejections), scoring
│   │   ├── Import/                # probe, verify, tag, organize, place, notify
│   │   ├── Organizer/             # naming template engine (Lidarr-style tokens), album policy ("fewest albums")
│   │   ├── Tagging/               # ATL-based writer, Picard mapping
│   │   ├── Sources/               # ISourceProvider, IDownloadClient abstractions
│   │   ├── ImportLists/           # CSV (Exportify), Deezer, YouTube Music, Last.fm, ListenBrainz, reference library
│   │   ├── Notifications/         # ported providers
│   │   ├── Jobs/                  # command queue, scheduled jobs
│   │   └── Persistence/           # EF Core DbContext, migrations
│   ├── Wondarr.Sources.Slskd/   # slskd HTTP client, SlskdHost (bundled process supervisor), settings rendering
│   ├── Wondarr.Sources.YouTube/ # YouTube Music (InnerTube) search, yt-dlp runner, output policy
│   ├── Wondarr.Sources.Torznab/ # Torznab/Newznab + Prowlarr + Gazelle-direct, qBittorrent, SABnzbd (ported)
│   ├── Wondarr.Api/             # ASP.NET Core host: controllers (/api/v1), auth, SignalR, static SPA hosting
│   └── Wondarr.Host/            # Program.cs, DI composition, hosted services (optional split from Api)
├── frontend/                      # React 19 + TypeScript + Vite + Mantine + TanStack
│   ├── src/{app,pages,components,api,hooks,theme}/
│   └── package.json
├── tests/
│   ├── Wondarr.Core.Tests/      # xUnit + FluentAssertions; golden tests for parsing/scoring/naming
│   ├── Wondarr.Api.Tests/       # WebApplicationFactory integration tests
│   ├── Wondarr.Sources.Tests/   # contract tests against recorded fixtures (slskd, qBittorrent, SABnzbd, MB, AcoustID)
│   └── fixtures/                  # recorded JSON/XML responses, sample audio (tiny), sample NZB/torrent files
├── docker/
│   ├── Dockerfile                 # multi-stage: frontend build → dotnet publish → runtime (+ffmpeg, fpcalc, deno, slskd)
│   ├── root/                      # s6-overlay init scripts, PUID/PGID handling
│   └── docker-compose.yml
├── scripts/
│   ├── worker.py                  # cheap-worker runner (OpenRouter / headless claude) — see AGENT_WORKFLOW.md
│   ├── fetch-slskd.sh             # downloads + verifies the pinned slskd release at image build
│   └── dev.sh / dev.ps1           # run API + frontend locally
├── .github/workflows/             # ci.yml (build/test/lint), release.yml (multi-arch image → GHCR), codeql.yml
├── .claude/
│   ├── agents/                    # subagent definitions (worker, reviewer, researcher)
│   └── settings.json              # project permissions + hooks (see AGENT_WORKFLOW.md)
└── docs/                          # this documentation set (see README.md map)
```

Solution-wide conventions live in `docs/build/CODING_STANDARDS.md`.
