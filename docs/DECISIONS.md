# Decision log

> **Name:** the project was renamed from *Compilarr* to **Wondarr** on 2026-09-28 (Phase 1a, `docs/DECISIONS.md` build session 2 #10). Entries before build session 2 #10 use the old name.

> All product/design decisions taken with the owner during the planning session (2026-09-28). New decisions go at the bottom as dated entries; architectural ones also get an ADR in `docs/adr/`.

Answers from the owner to the round-1 questions, and what each changed in this plan.

| # | Question | Decision | Effect on the plan |
|---|---|---|---|
| 1 | Build vs adopt | **Build new**, porting useful code from existing projects | §3.6 lists what to port and from where; licences in §8.5 |
| 2 | Name | "Syncarr" proposed | **Taken** — `syncarr/syncarr` (syncs Radarr/Sonarr/Lidarr instances) plus forks; and the word implies sync, not acquisition. Candidates that still look free: Cratearr, Jukearr, Recordarr, Tracksarr, Songdarr (§3.6). Still open (§12) |
| 3 | Stack | **Aligned with the other *arrs** where possible | Recommendation switched to **C#/.NET 10 + React** (§8). This also unlocks near-verbatim porting of Lidarr/Prowlarr GPL-3.0 code (download clients, Torznab/Newznab, quality parser, naming engine, notifications, health checks, fingerprinting) |
| 4 | slskd | Not running yet; **deploy/package with the app**; **sharing configurable in-app** | Compose ships slskd headless alongside the app; the app owns slskd's configuration (credentials, shared folders, slots, limits) via its API; a bundled single-image variant is planned (§9.1, §9.5) |
| 5 | Plexamp album layer | Assess; prefer flat `Artist - Title.ext` or artist folders; else aggregate into the fewest albums | **Assessment (§7.2): Plex does not hard-require album folders but strongly advises them, and flat folders have documented mis-grouping failures under default settings.** The Plexamp preset therefore keeps an album layer and implements the "fewest albums per artist" compaction; flat and artist-folder layouts remain available for other players or for Plex with "Prefer local metadata" and the caveats stated |
| 6 | Quality | FLAC supported; **default cutoff 320**; transcode YouTube to AAC or MP3 | Default profile "Standard 320": cutoff MP3-320 (AAC-256 and FLAC allowed, upgrades on); YouTube grabs transcoded once to AAC-256 `.m4a` by default (MP3-320 selectable) but *ranked as their Opus-160 source* so they stay upgradeable (§6.5, Appendix B) |
| 7 | Torrents | **qBittorrent only**; no private-tracker rules for now | Phase 7 scoped to qBittorrent (+ SABnzbd for usenet); partial single-file downloads on by default; Transmission/Deluge/rTorrent/NZBGet and tracker-policy switches moved to backlog |
| 8 | Inputs | Individual adds, Deezer/YouTube Music playlists, **Spotify via exported CSV**, Last.fm, ListenBrainz, and **the user's existing music folder (flat or layered), with manual matching where AcoustID cannot resolve** | CSV importer speaks the Exportify column set (includes ISRC); **library adoption with a manual-match queue is promoted to Phase 3** because it is both an input and the dedupe source (§4.4, §7.6, §10) |
| 9 | Deployment | Docker on Linux, aligned with the primary *arrs | hotio/LinuxServer-style image: `/config`, `/data`, `PUID/PGID/UMASK/TZ`, `/ping`, URL base, multi-arch; Unraid template (§9) |
| 10 | Dedupe | Not via Plex/Lidarr; the app scans the user's main Music folder | "Reference library" roots: scanned, matched, marked as owned, optionally adopted (§7.6) |

### Round 2 (same day)

| # | Question | Decision | Effect on the plan |
|---|---|---|---|
| 1 | Name | "Comparr" or "Compilarr" (compilations) | **Compilarr** adopted as the working name: no GitHub hits by web search; "Comparr" only matches a French price-comparison site and reads as "compare". Confirm with a GitHub name search before creating the repo (§3.6) |
| 2 | Usenet client | Keep SABnzbd, "as is tradition" | Unchanged: SABnzbd in Phase 7; NZBGet in backlog |
| 3 | YouTube output | A setting; make it quite customisable | Output policy is per library with per-source override: codec (AAC, MP3, keep Opus), bitrate or VBR quality, container, sample rate, plus "never fake lossless"; ranked as the Opus-160 source regardless (§7.4, Phase 4) |
| 4 | Album policy | Both configurable; `fewest_albums` default | Unchanged; all four policies selectable per library and per song (§7.3) |
| 5 | slskd packaging | **Bundle from the start** | The app image ships slskd and supervises it as a child process from Phase 0; an "external slskd" mode remains for users who already run one (§9.1, §9.5, Appendix A) |
| 6 | Reference library | Reference by default; adopt/retag available | Unchanged (§7.6, Phase 3) |
| 7 | Match queue | Auto-accept above a high threshold, ask below | Unchanged (§7.6) |
| 8 | Port | 1077 if free | **1077 adopted**: no *arr-family application uses it (Sonarr 8989, Radarr 7878, Lidarr 8686, Readarr 8787, Prowlarr 9696, Bazarr 6767, Whisparr 6969, Seerr 5055, Tautulli 8181, slskd 5030); it is above the privileged range; IANA lists 1077 for an unrelated game service, which is irrelevant on a LAN (§9.2) |

### 2026-09-28 — Build session 1 (Phase 0 start)

| # | Topic | Decision | Why |
|---|---|---|---|
| 1 | Worker model | **`deepseek/deepseek-v4.1-flash`** pinned in `.env` (`COMPILARR_WORKER_MODEL`) | Bake-off on P0-01 against `z-ai/glm-5.3-flash` and `qwen/qwen3-coder-next` (table in `docs/build/PROGRESS.md`): the only one to finish, commit and report within one continuation round, and the cheapest. Re-run the bake-off if a phase starts failing on worker quality |
| 2 | Phase 0 budget | USD 10 of worker spend (owner), bake-off excluded (it cost USD 0.35) | Owner answer at session start |
| 3 | Assertion library | **FluentAssertions 7.x** (7.2.2, Apache-2.0), not 8.x | FluentAssertions 8 moved to a commercial licence; 7.x keeps `CODING_STANDARDS.md` unchanged. AwesomeAssertions (MIT fork) is the drop-in if 7.x ever becomes a problem |
| 4 | Worker cost accounting | Real cost = token usage × OpenRouter price list; the `--max-budget-usd` flag is scaled so `COMPILARR_WORKER_BUDGET_USD` means real USD | Claude Code bills unknown model ids at Opus rates (~30× over for flash models) |
| 5 | Container test host | No Docker on the dev PC; container checks run on `ch01` over SSH with `sudo docker` (`compilarr-test`) | Owner answer; the other hosts do not accept the session's SSH key |
| 6 | Line endings | `.gitattributes` forces LF in the working tree | s6 init scripts and `fetch-slskd.sh` break with CRLF; the dev PC has `core.autocrlf=true` |



---
| 7 | .NET SDK | `global.json` pins 10.0.401 with `rollForward: latestPatch` | CI's runner resolved 10.0.401 while the dev box had 10.0.101; newer analyzers (CA1873) broke the CI build only. One feature band keeps local and CI equal |
| 8 | Unauthenticated UI JSON | `/initialize.json` answers **401** (not the *arr 302-to-login) when the UI policy fails; HTML routes still redirect | .NET 10 cookie authentication no longer redirects for API endpoints (`[ApiController]`); a 401 is also what the SPA's `fetch` can act on |
| 9 | Workflows are orchestrator-only | P0-11 (`.github/workflows/`) is written by the orchestrator, not a worker | Workers may not touch `.github/workflows/` (AGENT_WORKFLOW §7) |
| 10 | NOTICE enforcement | `scripts/check-notice.py` runs in CI and fails when a file with a `Ported from` header is missing from `NOTICE.md` | Makes the attribution rule mechanical |
| 11 | Worker caps | `COMPILARR_WORKER_MAX_TURNS` 25 → **50**, `COMPILARR_WORKER_TIMEOUT_MIN` 20 → **30** (owner, mid-Phase 0) | Nearly every Phase 0 task needed 45–60 turns; at 25 each needed 2–3 continuation runs, each re-reading context, and several were left half-finished for the orchestrator |

### 2026-09-28 — Build session 2 (Phase 1)

| # | Topic | Decision | Why |
|---|---|---|---|
| 1 | Identity chain for free text | Deezer search (plain `q=`, filtered client-side) gives an ISRC and a reference duration → MusicBrainz `/isrc/{isrc}` names the recording; otherwise MB recording search ranked on title, artist, version flags and duration against the Deezer reference; Deezer-only songs are kept; nothing found → "unresolved" | Live checks (research_metadata_plex §1.2.1): MB search returns many score-100 near-duplicates (snippets, DJ edits, remixes), and Deezer's field queries no longer work |
| 2 | Release list for album choice | MB release **browse** by recording (official, ≤ 3 pages of 100); track/disc number from one release lookup of the chosen release | Recording lookups cap `inc=releases` at 25; browse media have no tracks |
| 3 | Quality profile items | Ordered worst → best list of groups (`name?`, `qualityIds`, `allowed`); cutoff met when the file's group index ≥ the cutoff quality's group index. "Standard 320" puts AAC-256 in the MP3-320 group | Expresses "AAC-256 counts as met" (QUALITY_DEFINITIONS) without special cases; same model as Lidarr's grouped profiles. Quality ids 1–43 are a stable public contract |
| 4 | No `song.file_id` | "Has a file" = a `song_file` row exists (unique `song_id`) | Avoids a circular FK between `song` and `song_file`; ARCHITECTURE §5.4 updated |
| 5 | Pseudo-album | Title `Singles`, album artist = the artist, one synthetic UUID per artist, one date fixed at creation; the `compilation` policy uses one `Various Artists` album per library titled with the library name | One folder and one album id per pseudo-album (LIBRARY_OUTPUT §7.3); "Singles" reads cleanly under the artist in Plexamp |
| 6 | Pasted lists | Stored as an import list of type `paste`; unresolved lines are `import_list_item` rows in state `unresolved` | The review state is the one Phase 6 import lists need anyway |
| 7 | Deezer previews | Never stored; `GET /api/v1/preview` returns a fresh signed URL on demand | Preview URLs carry an `hdnea` token that expires after ~30 min |
| 8 | Default library | "Music" at `/data/music`, layout `plexamp`, policy `fewest_albums`, min 2 tracks per real album | The product's primary target is Plex/Plexamp (LIBRARY_OUTPUT §7.2) |
| 9 | Metadata base URLs are configuration | `metadata.*_base_url` in `config.yml` / `APP__METADATA__…` | Lets the CI gate replay recorded MusicBrainz/Deezer/CAA/iTunes responses instead of calling the live services |
| 10 | **Name: Compilarr → Wondarr** (owner, 2026-09-28; supersedes round 2 #1) | Renamed in **Phase 1a**, after the Phase 1 gate and before Phase 2 (plan: `docs/build/PHASE_1A_TASKS.md`). The orchestrator renames the repository contents by script; it also renames the GitHub repository (`wulfftech/wondarr`) and publishes `ghcr.io/wulfftech/wondarr`, and the owner sets the new package public. Historical documents keep the old name with a note | The name should say what the product hunts: an artist's one stand-alone hit, not their obscure 12-track album of B-sides ("one-hit wonders"). Collision check: the only prior use is `github.com/wondarr/wondarr`, a single-commit 2018 Rust experiment, 0 stars, no releases or images, dormant 7+ years; nothing on Docker Hub, the Servarr wiki, Reddit or trademark records |
| 11 | Phase 1 gate in CI replays recorded metadata | The gate was run live once (2026-09-28, PASS 48/50) through `scripts/metadata-replay.py`; CI replays that recording inside the built image and fails on any unrecorded request. `metadata.musicbrainz_mirror_interval_ms` lets a self-hosted MusicBrainz mirror (or the replay) go faster than 1 req/s; `musicbrainz.org` itself always keeps 1 req/s | Deterministic CI without calling MusicBrainz from shared runners; re-record with `SMOKE_METADATA=record` when the app's requests change |
| 12 | Worker caps | 100 turns / 60 min per run (owner, 2026-09-28), passed on the launch command until `.env` is updated | Phase 1 tasks regularly hit 50 turns with nearly finished, green work |

### 2026-09-29 — Build session 3 (Phase 2)

| # | Topic | Decision | Why |
|---|---|---|---|
| 1 | Soulseek account | The account in `.env` logs in from the bundled slskd on `ch01`; no duplicate-login kick in a 10-minute watch | A first attempt failed with `INVALIDUSERNAME` because the orchestrator's own shell one-liner passed the value **with its inline `# comment`**; `.env` values are now always read with the `\s#` comment rule (`scripts/worker.py`'s loader). The same mistake made a valid AcoustID key look invalid |
| 2 | Search results by polling (v1) | The search runner polls `GET /searches/{id}` every 500 ms until `isComplete`, then reads `/responses` once; `/hub/search` streaming and the "grab at ≥ 850 while streaming" early stop move to Phase 5 | Verified live: `/responses` is `[]` until completion; with `responseLimit` 100 searches complete in ~3 s, so streaming would save little and adds a SignalR client dependency |
| 3 | `searchTimeout` unit | Milliseconds (8000) | slskd hands the value to Soulseek.NET unchanged (0.26.0 source) |
| 4 | Per-grab download folder | Every grab uses the batch endpoint with `options.destination = wondarr/{queueItemId}` | The importer finds the file without guessing slskd's `${SOURCE_DIRECTORY}` layout, and parallel grabs cannot collide |
| 5 | Completion detection | Poll transfers (10 s while active); the `DownloadFileComplete` webhook only wakes the tracker early; the webhook carries its own generated token header, not the app API key | A lost webhook costs latency, never correctness; the app key never lands in `slskd.yml` |
| 6 | Soulseek settings storage | The settings API writes the `soulseek` section of `/config/config.yml` and reloads configuration; environment variables still win and are reported read-only | `config.yml` is already the app's own settings file; no second source of truth |
| 7 | Shared source contracts | `Candidate`, `ParsedName`, `ISourceProvider` and friends (`src/Wondarr.Core/Sources/`) are written by the orchestrator before the workers start | Lets the parser, the decision engine and the source be built in parallel against one shape |
