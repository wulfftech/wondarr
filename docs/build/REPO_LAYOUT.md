# Repository layout

```
wondarr/
├── README.md · LICENSE (GPL-3.0) · NOTICE.md (ported-code attributions) · CHANGELOG.md · CONTRIBUTING.md
├── global.json                    # pins the .NET 10 SDK
├── Directory.Build.props          # nullable, warnings as errors, analyzers, InvariantGlobalization
├── Directory.Packages.props       # central package versions
├── Wondarr.sln
├── src/
│   ├── Wondarr.Core/              # everything but HTTP and the sources: one folder per area
│   │                              #   Decisions (rejections, scoring), Searching, Importing, Verification,
│   │                              #   Identity, Metadata, Organizer, Tagging, References, ImportLists, Plex,
│   │                              #   Notifications, Jobs, Persistence (EF Core, migrations), …
│   ├── Wondarr.Sources.Slskd/     # slskd HTTP client, the bundled-process supervisor, settings rendering
│   ├── Wondarr.Sources.YouTube/   # YouTube Music (InnerTube) search, the yt-dlp runner, output policy
│   ├── Wondarr.Sources.Torznab/   # Torznab, Newznab, Prowlarr, Gazelle; qBittorrent and SABnzbd
│   └── Wondarr.Api/               # ASP.NET Core host: /api/v1 controllers, auth, SignalR, the SPA, Program.cs
├── frontend/src/{app,api,components,pages,theme,test}/   # React 19, TypeScript, Vite, Mantine, TanStack Query
├── tests/
│   ├── Wondarr.Core.Tests/        # xUnit + FluentAssertions
│   ├── Wondarr.Api.Tests/         # WebApplicationFactory integration tests
│   ├── Wondarr.Sources.Tests/     # contract tests against recorded fixtures
│   ├── fixtures/                  # recorded responses, tiny sample audio, NZB and torrent files
│   └── gate/                      # phase-gate scenarios and the recorded metadata the smoke test replays
├── tools/FakeSlskd/               # the fake slskd, AcoustID and container stubs the gates run against
├── docker/                        # Dockerfile (frontend → publish → runtime with ffmpeg, fpcalc, deno, slskd),
│                                  #   root/ (s6-overlay, PUID/PGID), docker-compose.yml, DOCKERHUB.md
├── scripts/                       # smoke-test.sh, phase<n>-gate.py, metadata-replay.py, safe-merge.sh,
│                                  #   remote-run.sh, check-notice.py, fetch-slskd.sh; tests/ for the shell scripts
├── website/                       # the docs site (Starlight), published to GitHub Pages by docs.yml
├── packaging/                     # catalogue entries for TrueNAS, Cosmos and DockSTARTer (see its README)
├── unraid/ · ca_profile.xml       # the Unraid template and Community Applications profile
├── .github/workflows/             # ci.yml, release.yml, mirror-dockerhub.yml, docs.yml, record-gate-metadata.yml
└── docs/                          # adr/, architecture/, build/, api/openapi.json, screenshots/
```

Solution-wide conventions are in `docs/build/CODING_STANDARDS.md`.
