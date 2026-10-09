# Phase 8 tasks — polish and release

Each task is sized for one subagent run (≤ ~10 files, ≤ ~400 lines of diff; see `AGENT_WORKFLOW.md` §2.0). The full spec for each lives in `docs/build/tasks/<id>.md` and is committed on `main` before its subagent starts. Delegation follows `DECISIONS.md` build session 10 #1 (OpenRouter paused): **sonnet** = a `general-purpose` subagent on Sonnet in its own worktree; **haiku** = a Haiku subagent for mechanical work; **orchestrator** = written in this session. "+ review" = a Sonnet review subagent on the branch's diff before merging.

Gate (`PHASES.md` Phase 8): a first public release is cut with multi-arch images, and the docs cover setup with slskd, Plex and qBittorrent end to end.

## Research (2026-10-09)

- **SoulSync's fake-lossless check** (`Nezreka/SoulSync`, `core/repair_jobs/fake_lossless_detector.py`, MIT per `license.txt`): a disabled-by-default repair job that runs `ffmpeg -t 30 -af highpass=f=<0.35 × sample rate>,volumedetect` over the first 30 s of each FLAC/WAV and reports "possible fake lossless" when the mean volume above ≈15.4 kHz is below −70 dB. It never changes a file. A single threshold cannot tell a 16 kHz cutoff from a 19 kHz one, and the band-energy variant measured on real files on `ch01` (`volumedetect` after chained high-passes) leaks too much through the filter skirts to separate an MP3-128 transcode from the genuine FLAC by more than a few dB. So Wondarr keeps SoulSync's *approach* — ffmpeg decodes, a spectral test decides, lossless containers only — and re-implements the measurement (no SoulSync code is copied, so no `NOTICE.md` row): ffmpeg decodes a window to mono 44.1 kHz float PCM, Wondarr computes a Welch power spectrum in C# and looks for the lossy encoder's brick-wall low-pass (a cliff of ≥ 25 dB within 1 kHz with only the noise floor above it). A prototype over five real FLACs on `ch01` and their re-encodes put the edges at 16.6–16.9 kHz (MP3-128), 18.6–18.9 kHz (MP3-192), 19.9–20.4 kHz (MP3-320) and none for the genuine files and ffmpeg's AAC-256 (the final rule, after a first one read clean walls too high), so the threshold is 19.5 kHz; a 320 or AAC transcode is not reliably separable from a genuine file and is accepted (DECISIONS build session 10 #2).
- **Multi-arch:** `release.yml` already builds `linux/amd64,linux/arm64` with QEMU on the current action majors (setup-qemu v4, setup-buildx v4, login v4, metadata v6, build-push v7). The Dockerfile compiles .NET and the frontend on `$BUILDPLATFORM` and fetches per-arch binaries by `TARGETARCH` (slskd 0.26.0 `linux-arm64`, fpcalc 1.6.1 `linux-arm64`, Deno `aarch64`, yt-dlp `linux_aarch64`, s6-overlay `aarch64`; ffmpeg from the multi-arch `mwader/static-ffmpeg:9.0.2`). Missing: the `{{major}}` tag (not for `0.x`), a GitHub Release with notes, and any run of the arm64 image — the free `ubuntu-24.04-arm` runner (public repositories) can run the Phase 0 smoke test natively on the pushed arm64 image.
- **Unraid:** Community Applications takes submissions at `ca.unraid.net/submit` from a public repository with an OSI licence, a `ca_profile.xml` and a valid template (`<Container version="2">` with `Config` elements of `Type` Port/Path/Variable). Unraid's convention is PUID 99 / PGID 100 and `/mnt/user/appdata/<app>`. Submitting is an outward action the owner does.
- **Docs site:** Material for MkDocs is in maintenance mode since 2025-11 (Zensical is its successor and not at parity); Starlight (Astro) needs only Node, which the repository already pins, and ships search, sidebar and dark mode. Pages deploys with `actions/upload-pages-artifact@v5` + `actions/deploy-pages@v5` (`pages: write`, `id-token: write`, environment `github-pages`). The site is served at `https://wulfftech.github.io/wondarr/` unless the owner picks a domain; turning Pages on is the owner's call.
- **The Phase 0 image** is still public: `ghcr.io/wulfftech/compilarr:sha-6103320` (the commit that closed Phase 0), so the release's upgrade check can start from a real Phase 0 database (`/config/compilarr.db`, renamed on first start since Phase 1a).

## Tasks

| ID | Title | Depends on | Size | Delegate |
|---|---|---|---|---|
| P8-00 | Pre-flight, research, this plan, DECISIONS build session 10 | — | S | orchestrator |
| P8-01 | Spectral fake-lossless check: `ISpectralAnalyzer` (ffmpeg → PCM → Welch spectrum → cutoff and verdict), an import step before conversion for lossless files, `import.fake_lossless_check` (`reject` / `off`), a rejected file blocklisted for the song with the cutoff in the reason | — | M | sonnet + review |
| P8-02a | Migration test from the Phase 0 schema: a database migrated to `AddCommands` (the last Phase 0 migration) with Phase 0's rows (settings, jobs, commands), named `compilarr.db`, upgraded by the real startup path to the current schema; the data survives and every later migration's defaults hold | — | S | sonnet + review |
| P8-02b | Release engineering: `release.yml` — the major tag for ≥ 1.0, an arm64 smoke job on `ubuntu-24.04-arm` against the pushed image, a GitHub Release from the tag with the CHANGELOG section as notes; `CHANGELOG.md` for the release; version from the tag | P8-02a | S | orchestrator |
| P8-03 | Library mass editor, backend: song list filters (text, monitored, has file, cutoff met, quality, profile, library, tag, artist), `PUT /api/v1/song/editor` (monitored, profile, library → `MoveSongs`, tags add/remove/replace), `DELETE /api/v1/song/editor`, `GET /api/v1/tag` (labels with counts), saved views (`custom_filter` table, `/api/v1/customfilter` CRUD) | — | M | sonnet + review |
| P8-04 | Library page: selection and the mass editor, tags on a song, the filter bar, saved views | P8-03 | M | sonnet |
| P8-05 | ReplayGain writer (optional, off by default per library): track gain/peak measured with ffmpeg's `ebur128` (−18 LUFS reference), written as `REPLAYGAIN_TRACK_GAIN/PEAK` by the tag writer for every format at import, conversion and move | — | M | sonnet + review |
| P8-06 | Housekeeping: a daily `Housekeeping` job that trims finished commands and old search runs/candidates (backlog 2026-10-07-09), and the command executor no longer spins when a handler cannot be built (backlog 2026-10-07-12) | — | S | sonnet + review |
| P8-07 | Queue and history show a container grab: the release title, the file, "with N other songs" (Phase 7 follow-up) | — | S | sonnet |
| P8-08 | Docs site: Starlight under `website/` — install (Docker, compose, Unraid), first run, Soulseek (bundled and external slskd), Plex, qBittorrent, SABnzbd and indexers, YouTube, import lists, reference libraries, quality and upgrades, conversion, backups, API and *arr integrations, troubleshooting; the Unraid template `unraid/wondarr.xml` and `ca_profile.xml`; the Pages workflow (orchestrator) | — | M | sonnet (pages) + haiku (template) + orchestrator (workflow, review against the code) |
| P8-10 | Settings → General: the API key (masked, show, copy) and the Forms login account (`/api/v1/auth/user`), an instance summary — found missing by P8-08a: a new user could not set a login or find the key in the UI | — | S | sonnet |
| P8-09 | Release gate: from the docs alone on `ch01` — the published image with slskd, a throwaway Plex and qBittorrent, a song imported from Soulseek, YouTube and a torrent; a Phase 0 database upgraded to the release; then the tag (after the owner's go-ahead) | all | M | orchestrator |

Waves: **A** P8-01 ∥ P8-02a ∥ P8-03 → **B** P8-04 ∥ P8-05 ∥ P8-06 → **C** P8-07 ∥ P8-08 → P8-02b → P8-09. At most three subagents at once; never a merge while one runs its tests. Subagents cannot regenerate `docs/api/openapi.json` or the frontend types; the orchestrator does at merge.

Migrations: P8-03 (`custom_filter`), P8-05 (the library's ReplayGain switch, if it is not part of the output-policy JSON — decided in its spec).

Out of scope for Phase 8 (backlog): the OpenAPI enum typing (2026-10-07-08), writing slskd's settings in external mode (P5-07 remainder), the live SABnzbd check (needs a usenet provider from the owner).

## What subagents must not do

- Never write `.webm`; never produce lossless from lossy; never accept a file the spectral check calls lossy as a lossless quality.
- Never touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`; never merge, push or open a PR.
- Never send a request to a real service in a test; fixtures, fakes and generated signals only.
- Never log a secret; never delete or move torrent data.
