# Wondarr code and documentation review, 2026-11-04

Reviewed: `wulfftech/wondarr` at commit `80f4a38` (release 0.1.16; branch `claude/upbeat-clarke-7nplni`, identical to `main`).
Produced by one Claude Code cloud session that split the repository into eleven areas, ran a reviewer on each, and then re-checked every high finding and a sample of the mediums against the code before merging. Nothing in the repository was changed. This file lives in `docs/reviews/`, which `.gitignore` keeps out of the published repository.

## How to use this file

- **One finding is one block** under "Detailed findings", with a stable id (`area-nn`), a severity, a confidence, the file and line, a summary, a concrete failure scenario, the evidence quoted from the tree, and a suggested fix. The index table above the details lists them all, most severe first.
- **Resolving a finding:** take it as a task by id, read the quoted evidence against the current file (line numbers are from commit `80f4a38` and will drift), fix it on its own branch with a test where the finding names a test gap, and record the outcome next to the id in this file (`fixed in <commit>`, `rejected: <reason>`, or `deferred`). Findings in one file or one subsystem are best taken together; the "Suggested order" below groups them.
- **Severity:** `critical` data loss or security hole; `high` wrong behaviour in ordinary use or a race that loses work; `medium` an edge case, a leak, or a test that cannot catch what it claims; `low` a minor defect, misleading dead code, a stale comment, or a style misstep.
- **Confidence:** `high` the reviewer read the whole path and the scenario follows from the quoted code; `medium` the mechanism is clear but one external fact was not re-verified (a library's behaviour, a third-party tool's exact output); `low` plausible, with what would confirm it stated in the block.
- **Lead re-verification:** the eight high findings and the mediums `api-01`, `api-02`, `api-03`, `core-search-01`, `sources-01`, `ops-01`, `ops-02`, `docs-user-02` and `docs-user-03` were checked a second time by the merging session by reading the named lines; each held.
- **Outcome lines:** a finding that already carries `- outcome:` was re-judged by the merging session once the real `/writing-style` skill arrived. "rejected" means no work; "confirmed" and "partly confirmed" mean the finding stands as the line describes. The index table's last column repeats the tag.

## What was run

| Check | Result |
|---|---|
| `dotnet build -warnaserror` (SDK 10.0.401) | Green, 0 warnings |
| `dotnet test` | Api 1 671 passed, 9 skipped (live tests); Sources 578 passed; Core 2 973 passed, 6 skipped, **9 failed** |
| `npm run lint`, `typecheck`, `format` (frontend) | Green |
| `npm test` (frontend) | 46 files, 435 tests, green |
| `bash -n` on every shell script, `ast.parse` on every Python script | Clean |

The 9 Core failures are not product defects. This container has Ubuntu's ffmpeg 6.1.1 instead of the image's pinned 9.0.2 and runs the suite as root; findings `tests-01` and `tests-02` describe the two causes and the fix (skip conditions). Everything else is green.

## About the writing-style review

The request was to review the documentation against the current `/writing-style` skill. That skill was not reachable from this cloud session at first (it is not among the account's synced skills, the repository's `.claude/` folder was git-ignored, and the owner's Remote Control folders were not available), so the documentation reviewers worked from a rule set reconstructed from the repository's own prose. The owner then supplied the original skill, which is now in the repository at `.claude/skills/writing-style/SKILL.md` and reaches every checkout and cloud session.

The real skill differs from the reconstruction in ways that matter. For repository prose it applies Style 1 (Default): dry, punchy, confident, short sentences and fragments, no hedging, no forward-caveating, no "not this, but this" framing, tables for technical lists. It does not ban em dashes, arrows, "e.g.", abbreviations, the passive voice or any tense, and the README's product voice is the target, not a misstep. Every style finding was therefore re-judged against the real skill and carries an `outcome:` line: 28 are rejected (no work), the spelling and grammar findings stand, `Note:` stands as forward-caveating, and the paragraph-length findings stand under "Short sentences". A further pass with the rules the reconstruction lacked found the documentation clean of hedging and forward-caveating and produced two new findings (`style-01`, `style-02`). The mapping from the reconstructed rules to the real skill is in the appendix. Fact findings (S1 to S4) never depended on the skill.

## The short version

- **No critical finding.** Authentication, API-key handling, secret masking, process handling, injection surfaces, checksum verification of every bundled download and the CI/release permissions all checked out and are listed under "Verified and not reported" in each area's coverage notes, so the next reviewer need not repeat them.
- **Eight high findings.** Four in code: a reference library whose root path is edited strands every owned song's file path (`core-data-01`); a malformed Deezer ISRC aborts a whole import-list sync and repeats every interval (`core-external-01`); the interactive Soulseek search re-runs on every queue push because of a shared query key (`frontend-01`); the YouTube settings page cannot save a sample rate and shows a stored one as "Keep" (`frontend-02`). Four in docs: a UI path that does not exist (`docs-user-01`), a "binding" repository layout with 14 missing paths (`docs-internal-01`), an `.env.example` whose variable names nothing reads (`docs-internal-02`), and a broken tool path in a gate schema (`docs-internal-03`).
- **The medium findings cluster** around three themes: the reference-library state machine (rows left `Identified` with no song, releases that ignore a second copy, an unreadable file that keeps its song), restart and failure recovery in the import pipeline (in-place upgrades, the Opus rename, cross-device moves, the recycle-bin clock), and the gap between the architecture documents and the code (tables, endpoints, commands, config keys and jobs that were designed but not built, or built differently).
- **The low findings** are small script and workflow defects, test gaps the reviewers named next to the defect they would have caught, spelling and grammar slips, the paragraph-length problems in the older architecture documents, and 28 style findings that the real skill does not support and that are marked rejected so nobody spends time on them.
- **One decision the skill raises** (`style-02`): its code-comment rules (lowercase, one line, no docstrings) and the repository's practice (XML doc summaries on every public member, sentence-case WHY comments) disagree across the whole codebase. That is the owner's call, not a worker's.

## Suggested order

1. `frontend-02`, `frontend-01`, `frontend-03` (one page and one API module; a user-visible bug and a wasted search budget).
2. `core-data-01` with `core-data-02`, `core-data-03`, `core-data-04` (one subsystem: reference ownership; add the missing tests together).
3. `core-external-01` and `core-external-02` (one catch clause and two validators).
4. `core-import-01`, `core-import-02`, `core-import-03`, `core-import-04` with their test gaps `core-import-09`, `core-import-10`.
5. `api-01`, `api-02`, `api-03`, `api-05`; `sources-01`, `sources-02`; `core-search-01`, `core-search-02`.
6. `ops-01`, `ops-02`, `ops-03`, `ops-11` (release gate coverage, the hard-coded healthcheck port, the smoke-test leak, the TrueNAS PUID question).
7. The documentation fact findings (`docs-user-01` to `docs-user-12`, `docs-internal-01` to `docs-internal-28`): most are one-line edits; `REPO_LAYOUT.md`, `CONFIG_EXAMPLES.md`, ARCHITECTURE §5.4 to §5.6 and `.env.example` want a rewrite from the code rather than a patch.
8. The style findings that survived re-judgement: `docs-user-06`, `-26`, `-27`, `-28`, `docs-internal-30` (the `Note:` only), `-42`, `-43`, `-44`, `style-01`. Skip every finding whose outcome line starts with "rejected". `style-02` goes to the owner as a question.

## Summary tables and index

Total findings: 143 (critical 0, high 8, medium 45, low 90); 28 of the low style findings were rejected on re-judgement against the real skill and need no work, leaving 115 open.

| Area | critical | high | medium | low | total |
|---|---|---|---|---|---|
| api | 0 | 0 | 3 | 3 | 6 |
| tests | 0 | 0 | 0 | 2 | 2 |
| style | 0 | 0 | 0 | 2 | 2 |
| core-import | 0 | 0 | 4 | 6 | 10 |
| core-data | 0 | 1 | 2 | 2 | 5 |
| core-search | 0 | 0 | 2 | 5 | 7 |
| core-external | 0 | 1 | 1 | 4 | 6 |
| sources | 0 | 0 | 2 | 4 | 6 |
| frontend | 0 | 2 | 4 | 6 | 12 |
| ops | 0 | 0 | 4 | 8 | 12 |
| docs-user | 0 | 1 | 5 | 24 | 30 |
| docs-internal | 0 | 3 | 18 | 24 | 45 |

| ID | Sev | Conf | Category | Claim | Where | Re-judged |
|---|---|---|---|---|---|---|
| core-data-01 | high | high | data-loss | Editing a reference library's rootPath strands every owned song's file path at the old root | `src/Wondarr.Core/References/ReferenceLibraryService.cs:325` |  |
| core-external-01 | high | medium | correctness | Unvalidated Deezer ISRC reaches MusicBrainzClient and aborts a whole list run | `src/Wondarr.Core/Identity/IdentityResolver.cs:473` |  |
| frontend-01 | high | high | correctness | Every `['queue']` invalidation re-runs the interactive Soulseek search | `frontend/src/api/queue.ts:146` |  |
| frontend-02 | high | high | correctness | YouTube settings cannot save a 44.1/48 kHz sample rate, and show a stored one as "Keep" | `frontend/src/pages/settings/YouTubeSettingsPage.tsx:105` |  |
| docs-user-01 | high | high | stale-doc | adding-songs.md sends the reader to "Activity → Tasks", which does not exist | `website/src/content/docs/library/adding-songs.md:53` |  |
| docs-internal-01 | high | high | stale-doc | REPO_LAYOUT.md calls its names binding, but 14 of its paths do not exist | `docs/build/REPO_LAYOUT.md:3` |  |
| docs-internal-02 | high | high | stale-doc | .env.example names secrets that nothing in the repository reads | `.env.example:3` |  |
| docs-internal-03 | high | high | stale-doc | Phase 4 schema names a tool path `tools/the fake yt-dlp` that does not exist | `tests/gate/phase4-scenario.schema.md:3` |  |
| api-01 | medium | high | security | Two slow POST /login connections lock every user out of login indefinitely | `src/Wondarr.Api/Extensions/LoginRateLimiting.cs:56` |  |
| api-02 | medium | high | security | `auth: none` serves the API key (/initialize.json) to any Host, so DNS rebinding reads it | `src/Wondarr.Api/Authentication/UiAuthorizationPolicyProvider.cs:47` |  |
| api-03 | medium | high | correctness | `?page=2147483647` is a 500 on /api/v1/log and returns page 1 on the other lists | `src/Wondarr.Api/Paging/PagingResource.cs:57` |  |
| core-import-01 | medium | high | data-loss | Restart recovery never finds an in-place upgrade, so the upgraded file is re-imported over | `src/Wondarr.Core/Importing/ImportRecovery.cs:151` |  |
| core-import-02 | medium | high | correctness | The Opus-to-.ogg rename is not saved before verification, so a shutdown strands the download | `src/Wondarr.Core/Importing/ImportService.cs:387` |  |
| core-import-03 | medium | medium | data-loss | A move whose source cannot be unlinked leaves an unrecorded copy in the library | `src/Wondarr.Core/Organizer/FilePlacer.cs:339` |  |
| core-import-04 | medium | medium | data-loss | A recycled file whose time stamp could not be set is deleted on the next Housekeeping, not after the retention | `src/Wondarr.Core/Organizer/RecycleBin.cs:154` |  |
| core-data-02 | medium | high | correctness | Deleting a song leaves its reference row `Identified` with no song; the file is never offered or identified again | `src/Wondarr.Core/Persistence/DomainModelConfiguration.cs:413` |  |
| core-data-03 | medium | high | correctness | Skip, re-resolve and re-identification release a song without following its second Identified copy | `src/Wondarr.Core/References/ReferenceMatchService.cs:678` |  |
| core-search-01 | medium | high | correctness | SoulseekFilenameParser throws OverflowException on a 10+ digit dash segment | `src/Wondarr.Core/Sources/SoulseekFilenameParser.cs:398` |  |
| core-search-02 | medium | high | correctness | Tier early-stop ignores peer reputation, so YouTube is skipped when every Soulseek hit is on cooldown | `src/Wondarr.Core/Searching/SongSearchService.cs:537` |  |
| core-external-02 | medium | high | correctness | The all-zero GUID passes LookupInput but crashes the resolver with ArgumentException | `src/Wondarr.Core/Identity/LookupInput.cs:110` |  |
| sources-01 | medium | high | correctness | NzbFileList's yEnc regex timeout is uncaught and aborts the whole song search | `src/Wondarr.Sources.Torznab/Parsing/NzbFileList.cs:40` |  |
| sources-02 | medium | medium | data-loss | A listed NZB file SABnzbd renamed by sanitising is deleted and re-grabbed, never blocklisted | `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:845` |  |
| frontend-03 | medium | high | correctness | Removing a queue item with blocklist does not refresh the blocklist shown beside it | `frontend/src/api/queue.ts:130` |  |
| frontend-04 | medium | high | correctness | Deleting the open library leaves Settings → Library with no panel shown | `frontend/src/pages/settings/LibrarySettingsPage.tsx:645` |  |
| frontend-05 | medium | medium | correctness | `['songs']` prefix invalidation refetches the song page's external lookups on every library change | `frontend/src/api/songs.ts:109` |  |
| frontend-11 | medium | high | test-gap | "saves the monitored flag" test does not assert the flag that was sent | `frontend/src/pages/SongPage.test.tsx:750` |  |
| ops-01 | medium | high | test-gap | Release tags move on Phase 0/1 only; the Phase 2–7 gates are not in the promote chain | `.github/workflows/release.yml:33` |  |
| ops-02 | medium | high | correctness | HEALTHCHECK hardcodes port 1077, so a changed `server.port` makes the container permanently unhealthy | `docker/Dockerfile:192` |  |
| ops-03 | medium | high | resource-leak | smoke-test.sh cleanup leaves the Phase 7 container running and the p6/p7 work trees behind on failure | `scripts/smoke-test.sh:69` |  |
| ops-11 | medium | low | correctness | TrueNAS template never sets PUID/PGID; it relies on the apps library injecting them from `run_as` | `packaging/truenas/wondarr/templates/docker-compose.yaml:7` |  |
| docs-user-02 | medium | high | stale-doc | install.md says Wondarr takes no backup before an upgrade; it has since 0.1.1 | `website/src/content/docs/getting-started/install.md:154` |  |
| docs-user-03 | medium | high | stale-doc | install.md says `:latest` follows every release; the workflow moves it only for the highest stable release | `website/src/content/docs/getting-started/install.md:152` |  |
| docs-user-04 | medium | high | stale-doc | first-run.md and the README say Prowlarr needs Wondarr's API key; the API page says Wondarr is not a Prowlarr app | `website/src/content/docs/getting-started/first-run.md:28` |  |
| docs-user-05 | medium | high | stale-doc | first-run.md's navigation list is missing the Updates item | `website/src/content/docs/getting-started/first-run.md:6` |  |
| docs-user-06 | medium | high | style | README "AI disclaimer" paragraph has four typos | `README.md:13` | confirmed |
| docs-internal-04 | medium | high | stale-doc | ADR-0011 says scripts/worker.py "stays in the repo"; it is git-ignored | `docs/adr/0011-agentic-build-workflow.md:18` |  |
| docs-internal-05 | medium | high | stale-doc | ADR index does not reflect the amendments of ADR-0011 | `docs/adr/README.md:17` |  |
| docs-internal-06 | medium | high | stale-doc | ADR-0003 still decides YoutubeDLSharp and a TagLib# fallback; neither is used | `docs/adr/0003-stack-dotnet-react.md:9` |  |
| docs-internal-07 | medium | high | stale-doc | STACK.md says self-contained trimmed publish; the image is framework-dependent | `docs/architecture/STACK.md:31` |  |
| docs-internal-08 | medium | high | stale-doc | DEPLOYMENT.md's image-contents table omits yt-dlp | `docs/architecture/DEPLOYMENT.md:29` |  |
| docs-internal-09 | medium | high | stale-doc | Plex playlists are not written through `/playlists/upload` | `docs/architecture/STACK.md:28` |  |
| docs-internal-10 | medium | high | stale-doc | ARCHITECTURE §5.4 lists three tables that do not exist and omits four that do | `docs/architecture/ARCHITECTURE.md:109` |  |
| docs-internal-11 | medium | high | stale-doc | ARCHITECTURE §5.6 sketch names endpoints and commands that are not in the API | `docs/architecture/ARCHITECTURE.md:179` |  |
| docs-internal-12 | medium | medium | stale-doc | ARCHITECTURE §5.5 lists a weekly "Metadata refresh" job that does not exist | `docs/architecture/ARCHITECTURE.md:143` |  |
| docs-internal-13 | medium | high | stale-doc | QUALITY_DEFINITIONS output-policy table is the version-1 shape and names a quality that does not exist | `docs/architecture/QUALITY_DEFINITIONS.md:29` |  |
| docs-internal-14 | medium | high | stale-doc | CONFIG_EXAMPLES.md documents config.yml keys and sections that do not exist | `docs/architecture/CONFIG_EXAMPLES.md:35` |  |
| docs-internal-15 | medium | medium | stale-doc | LIBRARY_OUTPUT describes artist.jpg, per-grab output overrides and an ID3v2.3 option that are not built | `docs/architecture/LIBRARY_OUTPUT.md:63` |  |
| docs-internal-16 | medium | high | stale-doc | DISTRIBUTION.md contradicts itself on Docker Hub and GitHub metadata | `docs/build/DISTRIBUTION.md:5` |  |
| docs-internal-17 | medium | high | stale-doc | tests/fixtures/README.md draws a golden-suite layout that does not exist | `tests/fixtures/README.md:9` |  |
| docs-internal-18 | medium | high | stale-doc | CODING_STANDARDS names tooling the build does not use and a release step no workflow runs | `docs/build/CODING_STANDARDS.md:16` |  |
| docs-internal-19 | medium | high | stale-doc | Architecture docs and ADRs reference numbered documents that are not in the repository | `docs/architecture/ARCHITECTURE.md:3` |  |
| docs-internal-20 | medium | high | stale-doc | smoke-test.sh header omits the Phase 5 gate it runs and repeats a parenthetical | `scripts/smoke-test.sh:2` |  |
| docs-internal-21 | medium | high | stale-doc | FakeSlskd README does not describe the InnerTube, Plex and yt-dlp stubs the gates depend on | `tools/FakeSlskd/README.md:1` |  |
| api-04 | low | high | stale-doc | `UseForwardedHeaders()` is a no-op, but the comment and the docs say proxy headers can be processed | `src/Wondarr.Api/Program.cs:93` |  |
| api-05 | low | high | security | slskd webhook's "came through a proxy" check looks at one header, not the shared proxy-header list | `src/Wondarr.Api/Slskd/SlskdWebhookController.cs:198` |  |
| api-06 | low | high | stale-doc | OpenAPI snapshot test comment says /api/v1/system/status does not exist yet | `tests/Wondarr.Api.Tests/OpenApiSnapshotTests.cs:34` |  |
| tests-01 | low | high | test-gap | Nine ffmpeg-gated tests fail on a host whose ffmpeg is not the image's 9.0.2 | `tests/Wondarr.Core.Tests/Media/DamagedMp3.cs:18` |  |
| tests-02 | low | high | test-gap | TagWriterSafetyTests.Leaves_a_read_only_file_untouched fails when the suite runs as root | `tests/Wondarr.Core.Tests/Tagging/TagWriterSafetyTests.cs:17` |  |
| style-01 | low | high | style | Two "not X, but Y" framings in the internal docs (Style 1: no "not this, but this") | `docs/architecture/LIBRARY_OUTPUT.md:16` |  |
| style-02 | low | high | style | The skill's code-comment rules and the repository's comment practice disagree everywhere | `.claude/skills/writing-style/SKILL.md:94` |  |
| core-import-05 | low | medium | concurrency | Removing a Completed queue item can race the import that already started on it | `src/Wondarr.Core/Importing/QueueActions.cs:110` |  |
| core-import-06 | low | high | correctness | A download whose decode check always times out is deferred every 15 minutes for ever | `src/Wondarr.Core/Importing/ImportService.cs:484` |  |
| core-import-07 | low | high | correctness | Rejecting a converted download with clean_leftover_folders off leaves an empty grab folder behind | `src/Wondarr.Core/Importing/ImportService.cs:360` |  |
| core-import-08 | low | high | style | CompactExecutor.CleanUpFolderAsync walks the folder recursively for a value it never uses | `src/Wondarr.Core/Compaction/CompactExecutor.cs:1061` |  |
| core-import-09 | low | high | test-gap | No test covers restart recovery of an upgrade that landed at the old file's path | `tests/Wondarr.Core.Tests/Importing/QueueTrackerTests.cs:445` |  |
| core-import-10 | low | high | test-gap | No test covers a cancellation between the Opus-to-.ogg rename and the first save | `tests/Wondarr.Core.Tests/Importing/ImportServiceTranscodeTests.cs:104` |  |
| core-data-04 | low | high | correctness | A file that changes and becomes unreadable keeps owning its song for good | `src/Wondarr.Core/References/ReferenceScanner.cs:256` |  |
| core-data-05 | low | high | stale-doc | Docs and XML docs say adopt "moves/renames" the user's files; the adopter copies them | `docs/architecture/LIBRARY_OUTPUT.md:95` |  |
| core-search-03 | low | high | correctness | Bundler's "busy" filter disagrees with HasActiveForSongAsync, leaving a stray accepted record on the lead song's run | `src/Wondarr.Core/Searching/ContainerBundler.cs:69` |  |
| core-search-04 | low | high | correctness | The attempt budget is per GrabBestAsync call, so one run can make up to 10 grab attempts with max_auto_attempts_per_search = 4 | `src/Wondarr.Core/Searching/SongSearchService.cs:1047` |  |
| core-search-05 | low | medium | correctness | Push grabs (JudgeAsync) bypass max_active_downloads and the slot gate | `src/Wondarr.Core/Searching/SongSearchService.cs:413` |  |
| core-search-06 | low | medium | correctness | Missing Search selects songs that are already Completed/Importing, spending batch places and slot waits on them | `src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:164` |  |
| core-search-07 | low | medium | correctness | A missed Quartz fire is skipped, not run late | `src/Wondarr.Core/Jobs/ScheduledTaskService.cs:94` |  |
| core-external-03 | low | medium | correctness | A cancelled waiter at the spacing gate leaves a dead slot that delays every later caller | `src/Wondarr.Core/Metadata/Http/RequestSpacingGate.cs:38` |  |
| core-external-04 | low | high | correctness | Five spacing handlers are registered without a provider key, so fail-fast errors say "unknown" | `src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:215` |  |
| core-external-05 | low | low | security | Per-list Last.fm API keys are never registered with the redactor, and `api_key=` escapes the cleanser | `src/Wondarr.Core/ImportLists/ImportListService.cs:699` |  |
| core-external-06 | low | high | stale-doc | `ImportListItemJson.ReadLine` doc promises a fallback the code does not have | `src/Wondarr.Core/ImportLists/PasteListService.cs:797` |  |
| sources-03 | low | medium | correctness | A Soulseek username or password that looks like YAML null is rendered unquoted | `src/Wondarr.Sources.Slskd/SlskdConfigRenderer.cs:45` |  |
| sources-04 | low | high | correctness | YouTube ReadOnlyFields names nested ytdlp keys in a spelling the UI never matches | `src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs:518` |  |
| sources-05 | low | medium | correctness | A `%` in youtube.downloads_dir is read by yt-dlp as an output-template directive | `src/Wondarr.Sources.YouTube/YtDlpRunner.cs:367` |  |
| sources-06 | low | low | concurrency | A settings restart that outlives the shutdown budget can leave a freshly launched slskd behind | `src/Wondarr.Sources.Slskd/SlskdHost.cs:301` |  |
| frontend-06 | low | high | correctness | The Library page's Artist filter is never invalidated | `frontend/src/api/songs.ts:40` |  |
| frontend-07 | low | high | correctness | Library filters desync from the URL when the route changes without a remount | `frontend/src/pages/LibraryPage.tsx:307` |  |
| frontend-08 | low | high | stale-doc | Two "keyed so a refetch resets the form" comments describe behaviour the key does not give | `frontend/src/pages/settings/LibrarySettingsPage.tsx:327` |  |
| frontend-09 | low | high | correctness | `apiRequest` in the API client is dead code with a misleading contract | `frontend/src/api/client.ts:43` |  |
| frontend-10 | low | high | correctness | Schema queries are nested under list keys, so every list change refetches the "never stale" schema | `frontend/src/api/importLists.ts:29` |  |
| frontend-12 | low | low | correctness | Add songs rows keyed by `mbRecordingId` collide when the id is an empty string | `frontend/src/pages/AddSongsPage.tsx:227` |  |
| ops-04 | low | high | stale-doc | ci.yml labels the smoke step "Phase 0 to Phase 3" but the script's defaults run Phases 0–7 | `.github/workflows/ci.yml:108` |  |
| ops-05 | low | high | stale-doc | ci.yml still says the GHCR package is private; the release smoke job and every template pull it anonymously | `.github/workflows/ci.yml:97` |  |
| ops-06 | low | high | correctness | `.gitignore` ignores every `config/` and `data/` directory anywhere in the tree, not just the runtime ones | `.gitignore:8` |  |
| ops-07 | low | high | test-gap | check-notice.py accepts any backticked mention anywhere in NOTICE.md as "listed", and never scans `tests/` | `scripts/check-notice.py:20` |  |
| ops-08 | low | medium | correctness | remote-run.sh aborts after uploading the tree when the last listed file is deleted locally | `scripts/remote-run.sh:26` |  |
| ops-09 | low | high | correctness | safe-merge.sh runs the full (possibly remote) check for an already-merged branch and then dies at `git commit` | `scripts/safe-merge.sh:256` |  |
| ops-10 | low | high | correctness | The secondary-container wait loops in smoke-test.sh never fail on `unhealthy` or timeout | `scripts/smoke-test.sh:243` |  |
| ops-12 | low | low | correctness | The app and its child processes run as uid 1000 with `HOME=/root`; only slskd has a workaround | `docker/root/etc/s6-overlay/s6-rc.d/svc-wondarr/run:9` |  |
| docs-user-07 | low | high | stale-doc | first-run.md lists four album policies; the UI and layouts page have five | `website/src/content/docs/getting-started/first-run.md:46` |  |
| docs-user-08 | low | high | stale-doc | Two bold UI labels differ from what the UI shows | `website/src/content/docs/library/import-lists.md:43` |  |
| docs-user-09 | low | high | stale-doc | Internal "plan" and "Phase" references survive in the README, CONTRIBUTING and the changelog | `README.md:84` |  |
| docs-user-10 | low | high | stale-doc | CONTRIBUTING's repository map lists three of the sixteen scripts as if they were all | `CONTRIBUTING.md:44` |  |
| docs-user-11 | low | high | stale-doc | CHANGELOG preamble still says "Semantic Versioning once released" | `CHANGELOG.md:3` |  |
| docs-user-12 | low | low | stale-doc | soulseek.md requires slskd 0.26.0 for external mode, but nothing in the code checks a version | `website/src/content/docs/sources/soulseek.md:86` |  |
| docs-user-13 | low | high | style | Navigation paths unbolded in the changelog (R4), 10 instances | `CHANGELOG.md:57` | rejected |
| docs-user-14 | low | high | style | Navigation paths unbolded on three website pages (R4), 5 instances | `website/src/content/docs/library/import-lists.md:22` | rejected |
| docs-user-15 | low | medium | style | README navigation paths unbolded (R4), 8 instances | `README.md:63` | rejected |
| docs-user-16 | low | medium | style | Em-dashes in README and CONTRIBUTING prose (R9), 8 instances | `README.md:7` | rejected |
| docs-user-17 | low | medium | style | Em-dashes in website and changelog prose (R9), 4 instances | `website/src/content/docs/guides/end-to-end.md:26` | rejected |
| docs-user-18 | low | high | style | Arrow chain and "e.g." in CONTRIBUTING (R10, R7) | `CONTRIBUTING.md:54` | rejected |
| docs-user-19 | low | high | style | "and so on" on two library pages (R7) | `website/src/content/docs/library/adding-songs.md:10` | rejected |
| docs-user-20 | low | high | style | Filler words "simply" and "just" (R1), 3 instances | `website/src/content/docs/library/managing-the-library.md:57` | rejected |
| docs-user-21 | low | medium | style | Future tense where the house style uses the present (R1), 5 instances | `website/src/content/docs/reference/configuration.md:74` | rejected |
| docs-user-22 | low | medium | style | Agentless passives (R5), 3 instances | `website/src/content/docs/getting-started/first-run.md:55` | rejected |
| docs-user-23 | low | medium | style | Parentheticals that carry a whole clause (R11), about 20 on the site | `website/src/content/docs/getting-started/first-run.md:28` | rejected |
| docs-user-24 | low | medium | style | Headings that are sentences or imperatives rather than noun phrases (R13), 12 instances | `website/src/content/docs/sources/youtube.md:8` | rejected |
| docs-user-25 | low | medium | style | Unexplained abbreviations MBID, CBR and VBR (R12) | `website/src/content/docs/library/import-lists.md:25` | rejected |
| docs-user-26 | low | high | style | "Slskd" capitalised (R8) | `website/src/content/docs/reference/troubleshooting.md:83` | confirmed |
| docs-user-27 | low | high | style | "a 60 second window" needs a hyphen (R8) | `website/src/content/docs/library/quality-and-upgrades.md:69` | confirmed |
| docs-user-28 | low | medium | style | "dismissable" (R8) | `website/src/content/docs/sources/plex.md:34` | confirmed |
| docs-user-29 | low | medium | style | "Configuration → feedback" link text uses an arrow for a section reference (R10) | `CHANGELOG.md:89` | rejected |
| docs-user-30 | low | low | style | DockSTARTer README closes with "please visit our support page" (R1, R14) | `packaging/dockstarter/wondarr/README.md:26` | rejected |
| docs-internal-22 | low | high | stale-doc | MATCHING_ENGINE §6.2 names the container limit `max_container_size`; the key is `search.max_container_size_mb` | `docs/architecture/MATCHING_ENGINE.md:40` |  |
| docs-internal-23 | low | high | stale-doc | The Singles pseudo-album is titled "Singles", not "{Artist} – Singles" | `docs/adr/0007-album-layer-and-fewest-albums-policy.md:9` |  |
| docs-internal-24 | low | high | stale-doc | ARCHITECTURE §5.3 and §5.6 name the import-list policies differently | `docs/architecture/ARCHITECTURE.md:87` |  |
| docs-internal-25 | low | medium | stale-doc | ADR-0010 lists a `calendar` endpoint that was never built | `docs/adr/0010-arr-conventions-and-port.md:9` |  |
| docs-internal-26 | low | high | stale-doc | ARCHITECTURE §5.1 diagram shows clients and sources ADR-0009 puts on the backlog | `docs/architecture/ARCHITECTURE.md:28` |  |
| docs-internal-27 | low | high | stale-doc | phase2-scenario.py's usage omits the --reserve switch phase4-scenario.py relies on | `scripts/phase2-scenario.py:20` |  |
| docs-internal-28 | low | high | stale-doc | STACK.md carries evaluation notes that were settled long ago | `docs/architecture/STACK.md:24` |  |
| docs-internal-29 | low | high | style | R7 "e.g." and "etc." in finished prose (ARCHITECTURE, LIBRARY_OUTPUT, DEPLOYMENT, CONFIG_EXAMPLES, fixtures README) | `docs/architecture/ARCHITECTURE.md:56` | rejected |
| docs-internal-30 | low | high | style | R1 filler words ("simply", "just", "Note:") | `docs/architecture/LIBRARY_OUTPUT.md:12` | partly confirmed |
| docs-internal-31 | low | medium | style | R2 American spellings in prose ("catalog", "organize", "organizer") | `docs/architecture/ARCHITECTURE.md:149` | rejected |
| docs-internal-32 | low | high | style | R3 "~" for "about" | `docs/architecture/STACK.md:25` | rejected |
| docs-internal-33 | low | medium | style | R6/R13 rhetorical-question heading and R14 "we" in MATCHING_ENGINE | `docs/architecture/LIBRARY_OUTPUT.md:14` | rejected |
| docs-internal-34 | low | medium | style | R9 em dashes as sentence breaks in ARCHITECTURE.md (24) | `docs/architecture/ARCHITECTURE.md:34` | rejected |
| docs-internal-35 | low | medium | style | R9 em dashes as sentence breaks in DEPLOYMENT.md (27) | `docs/architecture/DEPLOYMENT.md:50` | rejected |
| docs-internal-36 | low | medium | style | R9 em dashes as sentence breaks in LIBRARY_OUTPUT.md (31) | `docs/architecture/LIBRARY_OUTPUT.md:34` | rejected |
| docs-internal-37 | low | medium | style | R9 em dashes as sentence breaks in MATCHING_ENGINE.md (21) | `docs/architecture/MATCHING_ENGINE.md:37` | rejected |
| docs-internal-38 | low | medium | style | R9 em dashes in DISTRIBUTION.md (12) and the smaller files | `docs/build/DISTRIBUTION.md:160` | rejected |
| docs-internal-39 | low | medium | style | R10 arrows and "X → Y → Z" chains in prose | `docs/architecture/ARCHITECTURE.md:53` | rejected |
| docs-internal-40 | low | medium | style | R12 abbreviations the reader is never given (MB, LB, CAA, YT, sr, ToS, RG2) | `docs/architecture/ARCHITECTURE.md:30` | rejected |
| docs-internal-41 | low | medium | style | R4 UI labels not bold and a ">" navigation path | `docs/architecture/MATCHING_ENGINE.md:74` | rejected |
| docs-internal-42 | low | medium | style | R11/R13 paragraph-length bullets and nested parentheses in ARCHITECTURE.md | `docs/architecture/ARCHITECTURE.md:51` | partly confirmed |
| docs-internal-43 | low | medium | style | R11/R13 paragraph-length bullets and nested parentheses in MATCHING_ENGINE.md | `docs/architecture/MATCHING_ENGINE.md:36` | partly confirmed |
| docs-internal-44 | low | medium | style | R11/R13 paragraph-length bullets and nested parentheses in LIBRARY_OUTPUT.md and DEPLOYMENT.md | `docs/architecture/LIBRARY_OUTPUT.md:34` | partly confirmed |
| docs-internal-45 | low | medium | style | R5 agentless passives in ARCHITECTURE.md and LIBRARY_OUTPUT.md | `docs/architecture/ARCHITECTURE.md:34` | rejected |

## Detailed findings


## High findings

### core-data-01: Editing a reference library's rootPath strands every owned song's file path at the old root
- severity: high
- confidence: high
- category: data-loss
- file: src/Wondarr.Core/References/ReferenceLibraryService.cs:325
- file: src/Wondarr.Core/References/ReferenceLibraryService.cs:672
- file: src/Wondarr.Core/References/ReferenceOwnership.cs:374
- file: src/Wondarr.Core/References/ReferenceOwnership.cs:265
- file: src/Wondarr.Core/References/ReferenceAdopter.cs:352
- file: src/Wondarr.Core/Profiles/LibraryService.cs:128
- summary: `UpdateAsync` accepts a new `RootPath` and stores it without touching the `song_file` rows that were linked under the old root (their `path` is the absolute path at link time). Nothing later repoints them: the scan sees the same relative paths with the same size/mtime, so it never re-links, and every 0.1.15 "only while it is still the file read" guard compares the stored absolute path against `AbsolutePath(newRoot, relative)`, which never matches again. The managed-library update refuses exactly this move ("the rows would say the files are where they no longer are"); the reference-library update has no such rule and no repoint.
- scenario: Reference library "NAS" at `/mnt/music` scans and identifies 5 000 songs; `song_file.path = /mnt/music/A/B.flac` for each. The user remounts the share and edits the library to `/media/music` (PUT accepted; the test `Renaming_a_library_to_its_own_name_is_allowed` asserts the new root is stored). Next scan: every row is `sameFile` (unchanged) so only `LastSeenAt` is written; the 5 000 `song_file.path` values still say `/mnt/music/...`, a path that no longer exists. From then on: `ReleaseAsync` (`held.Path != AbsolutePath(root, relative)`) and `RepointAsync` return false, so a file the user later deletes never frees its song (the row's `SongId` is cleared at ReferenceScanner.cs:453-455 but the dead `song_file` stays, and the song is never wanted again); the adopter's `Owns` is false for every row, so an adopt-mode library silently "skips" every file; the UI, Plex path mapping and lyrics sidecar lookups read a path that does not exist.
- evidence: `library.RootPath = input.RootPath.Trim();` (ReferenceLibraryService.cs:672, no check, no repoint) versus `if (!PathRules.AreEqual(stored.RootPath, library.RootPath) && await _context.Songs...AnyAsync(... song.File != null ...)) errors.Add(("rootPath", "the library holds songs with files; move or delete them first..."))` (LibraryService.cs:128-143). Guards: `|| !string.Equals(held.Path, absolutePath, StringComparison.OrdinalIgnoreCase)) return false;` (ReferenceOwnership.cs:376-379).
- fix: Either refuse a root change while any `song_file` with `source_type = 'reference'` names one of the library's rows (mirror LibraryService), or rewrite those rows' `path` from old root to new root in the same transaction as the library update (they are identifiable through `source_ref.referenceLibraryId`). Add a test for an update that changes the root of a library that owns songs.

### core-external-01: Unvalidated Deezer ISRC reaches MusicBrainzClient and aborts a whole list run
- severity: high
- confidence: medium
- category: correctness
- file: src/Wondarr.Core/Identity/IdentityResolver.cs:473
- file: src/Wondarr.Core/Identity/IdentityResolver.cs:478
- file: src/Wondarr.Core/Identity/IdentityResolver.cs:988
- file: src/Wondarr.Core/Identity/IdentityResolver.cs:994
- file: src/Wondarr.Core/Metadata/MusicBrainz/MusicBrainzClient.cs:318
- file: src/Wondarr.Core/Metadata/PartialSearch.cs:57
- file: src/Wondarr.Core/ImportLists/PasteListService.cs:560
- summary: `IdentityResolver` hands a Deezer track's `Isrc` straight to `MusicBrainzClient.GetRecordingsByIsrcAsync`, which throws `ArgumentException` for anything that is not exactly `^[A-Z]{2}[A-Z0-9]{3}\d{7}$` after trim/upper-case. `ArgumentException` is not a "provider failure" (`ProviderKeys.IsProviderFailure`) and is not caught by `PasteListService.ResolveLineAsync`, so it escapes the resolver, the line loop and the sync.
- scenario: A Deezer playlist (or an `Artist - Title` paste) whose reference hit carries an ISRC Deezer stores in a non-canonical shape, for example `FR-Z03-14-00030` (hyphens) or an 11-character value. `RunTextPipelineAsync` reaches line 478 (or `ResolveDeezerTrackAsync` reaches line 994, which has no guard at all), `NormalizeIsrc` throws, `GuardedAsync`'s filter at line 807 rejects it, `ResolveLineAsync` (catches only `MetadataProviderException`, `HttpRequestException`, `JsonException`, non-caller `OperationCanceledException`) lets it out, `ProcessAsync` dies, and `ImportListService.SyncAsync` never reaches line 468 (`LastSyncedAt`). The list stays due, `GetDueAsync` returns it again next hour, the same item is still `Pending`, and the scheduled run logs an Error and crashes at the same line every interval; a pasted list's `BulkAddSongs` command fails the same way with the remaining lines never processed. On the Add-songs page the same input makes `SearchPartialAsync` throw instead of the 503/partial answer the controller handles.
- evidence: `var recordings = await _musicBrainz.GetRecordingsByIsrcAsync(track.Isrc!, cancellationToken)` (IdentityResolver.cs:993-995, no try) and `if (!IsrcPattern.IsMatch(normalized)) { throw new ArgumentException(...) }` (MusicBrainzClient.cs:322-327); `IsProviderFailure` lists only `MetadataProviderException, HttpRequestException, JsonException, TimeoutRejectedException, TaskCanceledException` (PartialSearch.cs:61-73). Only `LookupInput.Parse` (LookupInput.cs:115-119) validates ISRCs, and Deezer-sourced ISRCs never go through it.
- fix: Validate/normalise a Deezer ISRC with the same rule `LookupInput` uses before bridging (strip hyphens, check the pattern, skip the bridge when it fails), or make `GetRecordingsByIsrcAsync` answer `[]` for a malformed ISRC; and add `ArgumentException` to the per-line catch in `ResolveLineAsync` so one bad line can never abort a run. What would confirm the trigger: a Deezer track fixture whose `isrc` is hyphenated or short (none in `tests/fixtures/deezer`).

### frontend-01: Every `['queue']` invalidation re-runs the interactive Soulseek search
- severity: high
- confidence: high
- category: correctness
- file: frontend/src/api/queue.ts:146
- file: frontend/src/api/queue.ts:183
- file: frontend/src/api/queue.ts:130
- file: frontend/src/app/signalr.ts:135
- file: frontend/src/app/signalr.ts:180
- file: frontend/src/components/InteractiveSearchModal.tsx:297
- summary: The interactive search is cached under `['queue', 'search', songId]`, a child of `QUEUE_QUERY_KEY = ['queue']`. Every prefix invalidation of `['queue']` (each `queue` SignalR push, `useGrabRelease`, `useRemoveQueueItem`, and the whole-cache `catchUp()` on reconnect) refetches it while the dialog or the song page's panel is open, which submits a fresh Soulseek search each time; the module's own comment says the search must never be re-run because the budget is shared.
- scenario: A download is in progress (so the server pushes `queue` messages as it progresses). The user opens Interactive search for a song and reads the candidates. Each push runs `invalidateQueries({ queryKey: ['queue'] })`, which matches `['queue','search',id]` by prefix and re-runs the search (up to a minute each, 30-per-4-min budget), replacing the table under the user. Worse: `useGrabRelease.onSuccess` returns the `invalidateQueries` promise, which TanStack awaits before firing the mutate-level callbacks, so after "Grab" the button stays in `loading` and the "Grabbed" toast / modal close wait until the re-run search has finished.
- evidence: `queryKey: [...QUEUE_QUERY_KEY, 'search', songId]` (queue.ts:146) vs `onSuccess: () => queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY })` (queue.ts:183, 130) and `void queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY })` (signalr.ts:135); comment at queue.ts:134-137: "nothing is cached … and a failure is never retried: the search budget is shared".
- fix: Give the interactive search its own root key (e.g. `['interactive-search', songId]`) that no list invalidation touches, or invalidate the list and status with `exact: true` / a predicate; the same applies to `catchUp()` (exclude it with `refetchType`/predicate).

### frontend-02: YouTube settings cannot save a 44.1/48 kHz sample rate, and show a stored one as "Keep"
- severity: high
- confidence: high
- category: correctness
- file: frontend/src/pages/settings/YouTubeSettingsPage.tsx:105
- file: frontend/src/pages/settings/YouTubeSettingsPage.tsx:118
- file: frontend/src/pages/settings/YouTubeSettingsPage.tsx:243
- file: frontend/src/pages/settings/YouTubeSettingsPage.tsx:383
- file: src/Wondarr.Core/Profiles/OutputPolicy.cs:417
- file: src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs:416
- summary: The page keeps `sampleRate` as a string (`'keep' | '44100' | '48000'`) and sends it as such, but the server's `OutputPolicy.ParseSampleRate` accepts only the string `"keep"` or a JSON number, and `ToJson` writes a number back. The two shapes disagree in both directions.
- scenario: On Settings → YouTube pick AAC, then "44.1 kHz", Save. The body carries `"sampleRate": "44100"`; `ParseSampleRate` sees `JsonValueKind.String` that is not "keep" and adds "sampleRate must be 'keep' or a sample rate in Hz."; the update answers 400 and the page shows that message under the form. Conversely, a policy stored with `sampleRate: 44100` (a number) is read by `policyState` as `typeof source.sampleRate === 'string' ? … : ''` → `'keep'`, so the form shows "Keep" and a Save of any other field silently writes `"keep"`, discarding the stored rate. Tests cover only `sampleRate: 'keep'` (YouTubeSettingsPage.test.tsx:14,161,170).
- evidence: `const sampleRate = typeof source.sampleRate === 'string' ? source.sampleRate : '';` (page:105); `sampleRate: policy.sampleRate` sent as the SegmentedControl's string value (page:243, 383-392); server: `if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var rate))` else `errors.Add(("sampleRate", "sampleRate must be 'keep' or a sample rate in Hz."))` (OutputPolicy.cs:425-438); `writer.WriteNumber("sampleRate", rate)` (OutputPolicy.cs:241).
- fix: Read a numeric `sampleRate` in `policyState` (`typeof === 'number' ? String(rate) : 'keep'`) and send `policy.sampleRate === 'keep' ? 'keep' : Number(policy.sampleRate)`, as `profiles.ts` `readRule`/`writeRule` already do for the library policy; add a test that saves 44100.

### docs-user-01: adding-songs.md sends the reader to "Activity → Tasks", which does not exist
- severity: high
- confidence: high
- category: stale-doc
- file: website/src/content/docs/library/adding-songs.md:53
- file: CHANGELOG.md:390
- summary: The page says the "Waiting for a free download slot" message shows under **Activity → Tasks**. Activity has only Queue, History and Blocklist tabs; Tasks is its own navigation item under the System group (`/system/tasks`).
- scenario: A user whose add is waiting for a slot opens Activity, finds no Tasks tab and does not find the message or the **Cancel** button the same paragraph goes on to describe under **Tasks → Recent commands** (which is right).
- evidence: adding-songs.md:53 `shows "Waiting for a free download slot" under **Activity → Tasks**`; frontend/src/components/AppLayout.tsx:45-49 `{ to: '/activity', ... label: 'Activity' } ... { to: '/system/tasks', ... label: 'Tasks' }`; frontend/src/pages/ActivityPage.tsx:288-290 has only the `queue`, `history` and `blocklist` tabs. CHANGELOG.md:390 repeats the same path ("Activity → Tasks shows").
- fix: Write **Tasks** (or **Tasks → Recent commands**) in adding-songs.md; the changelog line is historical but could be corrected the same way.

### docs-internal-01: REPO_LAYOUT.md calls its names binding, but 14 of its paths do not exist
- severity: high
- confidence: high
- category: stale-doc
- file: docs/build/REPO_LAYOUT.md:3
- file: docs/build/REPO_LAYOUT.md:17
- file: docs/build/REPO_LAYOUT.md:18
- file: docs/build/REPO_LAYOUT.md:30
- file: docs/build/REPO_LAYOUT.md:32
- file: docs/build/REPO_LAYOUT.md:44
- file: docs/build/REPO_LAYOUT.md:50
- file: docs/build/REPO_LAYOUT.md:51
- summary: (S1) The file says "Names are binding unless an ADR changes them", and CONTRIBUTING.md sends readers to it for "the full tree", but a large part of the tree it draws is not in the repository, is named differently, or is git-ignored. No ADR changed the names.
- scenario: A contributor adds a decision specification under `src/Wondarr.Core/Decision/` or a hook under `frontend/src/hooks/` because the binding layout says so; the real folders are `Decisions/` and there is no `hooks/`. A contributor looks for `scripts/dev.sh`, `.github/workflows/codeql.yml` or `src/Wondarr.Host/` and finds none. Compared with `git ls-files` (tracked files only):
  - `src/Wondarr.Core/Decision/` → real name `src/Wondarr.Core/Decisions/`
  - `src/Wondarr.Core/Import/` → real name `src/Wondarr.Core/Importing/`
  - `src/Wondarr.Host/` → does not exist (Program.cs and DI composition live in `src/Wondarr.Api/`)
  - `frontend/src/hooks/` → does not exist (the real set is `api, app, components, pages, test, theme`)
  - `scripts/worker.py`, `scripts/worker_agent.py`, `scripts/test_worker_agent.py`, `scripts/openrouter_value.py` → not tracked; all four are listed in `.gitignore`
  - `scripts/dev.sh / dev.ps1` → do not exist
  - `.github/workflows/codeql.yml` → does not exist (the real set is `ci.yml, docs.yml, mirror-dockerhub.yml, record-gate-metadata.yml, release.yml`)
  - `.claude/agents/`, `.claude/settings.json` → not tracked; `.claude/` is in `.gitignore`
  - `docs/ (see README.md map)` → there is no `docs/README.md`; the map is in the root `README.md`
  The layout also omits tracked top-level content a contributor needs: `website/`, `packaging/`, `unraid/`, `tools/FakeSlskd/`, `tests/gate/`, `ca_profile.xml`, `CONTRIBUTING.md`, `.dockerignore`, `.gitattributes`, `docker/DOCKERHUB.md`, and 27 of the 38 folders under `src/Wondarr.Core/` (Albums, Authentication, Backup, Blocklisting, Compaction, Configuration, CustomFilters, DownloadClients, Feedback, HealthCheck, History, Housekeeping, Identity, Indexers, Logging, Lyrics, Media, Messaging, Paging, Plex, Profiles, References, Searching, Songs, Updates, Verification, Wanted).
- evidence: "Created incrementally from Phase 0 onward. Names are binding unless an ADR changes them." / "│   │   ├── Decision/              # Candidate, specifications (rejections), scoring" / "│   └── Wondarr.Host/            # Program.cs, DI composition, hosted services (optional split from Api)" / "│   ├── src/{app,pages,components,api,hooks,theme}/" / "├── .github/workflows/             # ci.yml (build/test/lint), release.yml (multi-arch image → GHCR), codeql.yml"
- fix: Regenerate the tree from `git ls-files` (folders only, two levels deep) and either drop "Names are binding" or keep it and record the renames in an ADR; remove the git-ignored worker scripts and `.claude/` from the published layout, or mark them "local only, git-ignored".

### docs-internal-02: .env.example names secrets that nothing in the repository reads
- severity: high
- confidence: high
- category: stale-doc
- file: .env.example:3
- file: .env.example:4
- file: .env.example:6
- file: .env.example:15
- file: .env.example:17
- summary: (S1, S2) The file tells a contributor which variables to fill in, but the four runtime secrets use names the code and tests never read, and the five worker variables belong to a workflow whose scripts and document are git-ignored.
- scenario: A contributor copies the file to `.env`, fills `ACOUSTID_CLIENT_KEY`, `SOULSEEK_USERNAME` and `SOULSEEK_PASSWORD`, sets `WONDARR_LIVE_TESTS=1` and runs the live contract tests; the tests read `WONDARR_ACOUSTID_KEY`, `WONDARR_SLSKD_URL` and `WONDARR_SLSKD_API_KEY` (`grep -rn GetEnvironmentVariable tests/ --include=*.cs` lists only `WONDARR_LIVE_TESTS`, `WONDARR_ACOUSTID_KEY`, `WONDARR_SLSKD_URL`, `WONDARR_SLSKD_API_KEY`, `WONDARR_UPDATE_FIXTURES`), so the key is never used and the live tests skip or fail. `OPENROUTER_API_KEY`, `WONDARR_WORKER_MODEL`, `WONDARR_REVIEWER_MODEL`, `WONDARR_WORKER_MAX_TURNS` and `WONDARR_WORKER_TIMEOUT_MIN` have no reader in any tracked file (`grep -rIl` over the tree finds only `docs/adr/0011-agentic-build-workflow.md`). The comments send the reader to `docs/build/AGENT_WORKFLOW.md` and `scripts/worker.py`, both excluded by `.gitignore`. Only `PLEX_TOKEN` is read, by `tests/fixtures/plex/record.py`.
- evidence: "# --- AI build workflow (docs/build/AGENT_WORKFLOW.md) ---" / "# OpenRouter key used by worker runs (scripts/worker.py)." / "ACOUSTID_CLIENT_KEY=          # register at https://acoustid.org/new-application" / "SOULSEEK_USERNAME=            # dedicated account for the bundled slskd"
- fix: Keep only variables the tracked code reads, under their real names (`WONDARR_LIVE_TESTS`, `WONDARR_ACOUSTID_KEY`, `WONDARR_SLSKD_URL`, `WONDARR_SLSKD_API_KEY`, `WONDARR_UPDATE_FIXTURES`, `PLEX_TOKEN`), and move the worker block to the git-ignored workflow document it belongs to.

### docs-internal-03: Phase 4 schema names a tool path `tools/the fake yt-dlp` that does not exist
- severity: high
- confidence: high
- category: stale-doc
- file: tests/gate/phase4-scenario.schema.md:3
- file: tests/gate/phase4-scenario.schema.md:31
- summary: (S1, R8) A search-and-replace of the old tool name left a broken path in code font and a sentence that starts in lower case. The stand-in yt-dlp is `tools/FakeSlskd/FakeYtDlp.cs`, run as `FakeSlskd --fake-ytdlp`.
- scenario: A contributor who wants to change what the fake yt-dlp answers opens `tools/` looking for "the fake yt-dlp" and finds only `FakeSlskd/`; the schema never names `FakeYtDlp.cs` or the `--fake-ytdlp` switch that `scripts/smoke-test.sh:35` uses.
- evidence: "What `tools/the fake yt-dlp` (the stand-in yt-dlp) answers for each video, and what the gate expects the app" / "verification resolves it to. the fake yt-dlp registers it with the stub right after generating the"
- fix: "What the fake yt-dlp (`tools/FakeSlskd/FakeYtDlp.cs`, started as `FakeSlskd --fake-ytdlp`) answers for each video, and what the gate expects Wondarr to do with it." and "The fake yt-dlp registers it with the stub right after it generates the file".


## Medium findings

### api-01: Two slow POST /login connections lock every user out of login indefinitely
- severity: medium
- confidence: high
- category: security
- file: src/Wondarr.Api/Extensions/LoginRateLimiting.cs:56
- file: src/Wondarr.Api/Extensions/LoginRateLimiting.cs:27
- file: src/Wondarr.Api/Authentication/AuthenticationController.cs:60
- summary: The global concurrency limiter (partition `"login"`, `PermitLimit = 2`, `QueueLimit = 0`) is acquired by the rate-limiting middleware before routing and released only after the response completes, so the lease also covers reading the form body, which `[FromForm]` model binding does inside the action. Two unauthenticated connections that send a POST body slowly hold both permits, and every other `POST /login` or `POST /login/setup` is answered 429 ("busy") for as long as they stay open.
- scenario: From any address open two TCP connections and send `POST /login` with `Content-Type: application/x-www-form-urlencoded` and `Content-Length: 30000000`, then trickle ~300 bytes/s (Kestrel defaults: `MinRequestBodyDataRate` 240 B/s, `MaxRequestBodySize` 30 MB, no `[RequestSizeLimit]` on the login actions, so one body lasts ~34 h; reconnect when it ends). The owner's `POST /login` with the right password now gets 429 and the "Wondarr is busy checking other sign-ins" page. The per-address window (10/min) does not help: the two held requests are only two attempts, and the cap is global across all callers.
- evidence: `PartitionedRateLimiter.Create<HttpContext, string>(context => IsLoginPost(context.Request) ? RateLimitPartition.GetConcurrencyLimiter("login", _ => new ConcurrencyLimiterOptions { PermitLimit = ConcurrentHashes, QueueLimit = 0 }) : ...)` (LoginRateLimiting.cs:56-60); `public async Task<IActionResult> Login([FromForm] LoginResource resource, ...)` (AuthenticationController.cs:60-64); `app.UseRateLimiter();` runs before `UseRouting()` (Program.cs:103-105).
- fix: Cap the PBKDF2 work itself rather than the whole request (a `SemaphoreSlim(2)` around the hash in `CredentialStore.VerifyAsync`/`TrySetInitialAsync`, or acquire the lease in the action after `Request.ReadFormAsync`), and put a small `[RequestSizeLimit]` (a few KB) on `/login` and `/login/setup`.

### api-02: `auth: none` serves the API key (/initialize.json) to any Host, so DNS rebinding reads it
- severity: medium
- confidence: high
- category: security
- file: src/Wondarr.Api/Authentication/UiAuthorizationPolicyProvider.cs:47
- file: src/Wondarr.Api/Authentication/NoAuthenticationHandler.cs:136
- file: src/Wondarr.Api/Authentication/UiAuthorizationHandler.cs:86
- file: src/Wondarr.Api/Authentication/InitializeController.cs:363
- summary: The Host rule (R-009: a rebound public name is never trusted) lives only in `UiAuthorizationHandler`, which runs the `LocalCallerRules` check solely for `auth_required: disabledForLocalAddresses`. With `auth: none` the UI policy's scheme is `None`, whose handler authenticates every request, so `/initialize.json` — which carries the API key — is served for any `Host`. A DNS-rebinding page served from the same port can read the key and then use the whole API with `X-Api-Key`.
- scenario: config `server.auth: none` (docs: "Only use it on a network you trust"). A LAN user opens `http://evil.example:1077/` (attacker's server on the same port as Wondarr); the attacker's DNS then answers `192.168.1.10` for `evil.example`; the page runs `fetch('/initialize.json')` → `GET /initialize.json` with `Host: evil.example:1077` from a private socket address → `200 {"apiRoot":"/api/v1","apiKey":"<32 hex>",...}`, readable because it is same-origin; the page then calls `PUT /api/v1/library/{id}`, `POST /api/v1/downloadclient`, `DELETE /api/v1/song/{id}` with that key. Under the default `forms` + `disabledForLocalAddresses` the identical request is refused (`HostNotTrusted`), which `LocalBypassTests.Initialize_json_is_refused_to_a_rebinding_host_from_a_private_address` checks; `UiAuthenticationTests.Initialize_json_serves_the_key_when_authentication_is_none` is the only `none` test and uses a local host.
- evidence: `var policy = new AuthorizationPolicyBuilder(_serverOptions.CurrentValue.Auth.ToString()).AddRequirements(new BypassableDenyAnonymousAuthorizationRequirement());` (UiAuthorizationPolicyProvider.cs:47-48); `return Task.FromResult(AuthenticateResult.Success(ticket));` for every caller (NoAuthenticationHandler.cs:148); `if (options.AuthRequired == AuthenticationRequired.DisabledForLocalAddresses && ... LocalCallerRules.IsLocalCaller(httpContext, options))` is the only place the Host is checked (UiAuthorizationHandler.cs:86-88).
- fix: For the `None` scheme, fail the UI policy (or at least `/initialize.json`) when `LocalCallerRules.IsLocalHost(Request.Host.Host, options.AllowedHosts)` is false, the same rule the bypass uses; `External` is excluded because behind the authenticating proxy the Host is legitimately public. Add a test mirroring the rebinding test for `auth: none`.

### api-03: `?page=2147483647` is a 500 on /api/v1/log and returns page 1 on the other lists
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Api/Paging/PagingResource.cs:57
- file: src/Wondarr.Core/Paging/PagingSpec.cs:42
- file: src/Wondarr.Core/Logging/LogFileReader.cs:144
- file: src/Wondarr.Core/Logging/LogFileReader.cs:341
- summary: `ToPagingSpec` passes the raw `page` through; `PagingSpec` clamps it only from below, and `Skip` (and `LogFileReader`'s own `skip`) is `(page - 1) * pageSize` in unchecked `int` arithmetic, so a page near `int.MaxValue` wraps negative. In `LogFileReader` the negative value becomes `new List<LogEntry>(capacity)` and throws `ArgumentOutOfRangeException` → HTTP 500; in the EF-backed lists `.Skip(negative)` is an `OFFSET` SQLite treats as 0, so the last possible page number returns the first page's rows instead of an empty page.
- scenario: `GET /api/v1/log?page=2147483647&pageSize=200` with the key: `skip = 2147483646 * 200` wraps to −400, `capacity = skip + pageSize = −200`, `new List<LogEntry>(-200)` throws → 500 problem details (for any clamped page size `p`, `(2^31−2)·p ≡ −2p mod 2^32`, so every size fails). `GET /api/v1/history?page=2147483647&pageSize=1000`: `Skip = −2000`, the records of page 1 come back labelled `"page": 2147483647`. Same for queue, blocklist, wanted, song, importlistitem, referencelibrary and matchqueue, which all do `.Skip(paging.Skip)`. The `PagingSpec` doc claims the values "are clamped on construction, so a hostile query string cannot ask for page -3 of a million rows".
- evidence: `return new PagingSpec(ReadInt(query["page"], DefaultPage), ReadInt(query["pageSize"], DefaultPageSize), ...)` (PagingResource.cs:57-61); `public int Skip => (Page - 1) * PageSize;` (PagingSpec.cs:42); `var skip = (Math.Max(query.Page, 1) - 1) * pageSize; var keepEntries = skip + pageSize <= MaxBufferedEntries; var capacity = keepEntries ? skip + pageSize : 0;` (LogFileReader.cs:144-146); `public List<LogEntry> Page { get; } = new(capacity);` (LogFileReader.cs:341).
- fix: Clamp `Page` to `int.MaxValue / MaxPageSize` in `PagingSpec` (or compute the skip as `long` and treat overflow as "past the end": empty page), and have `LogFileReader` use `PagingSpec.Skip` rather than its own multiplication.

### core-import-01: Restart recovery never finds an in-place upgrade, so the upgraded file is re-imported over
- severity: medium
- confidence: high
- category: data-loss
- file: src/Wondarr.Core/Importing/ImportRecovery.cs:151
- file: src/Wondarr.Core/Importing/ImportRecovery.cs:153
- file: src/Wondarr.Core/Importing/ImportService.cs:274
- file: src/Wondarr.Core/Importing/ImportService.cs:786
- file: src/Wondarr.Core/Organizer/FilePlacer.cs:119
- summary: `FindLandedFileAsync` excludes any candidate whose path equals the song's current `song_file.path`, regardless of its mtime. For an upgrade the placer recycles the old file and then places the new one at the very same path (same template, same tags), so after a crash between the move and the record step the landed upgrade is treated as "the song's own old file", the item is reset, and the import then fails "Downloaded file is missing" with `allowNextAttempt: true`.
- scenario: Song has `/lib/A/B/01 - T.mp3` (MP3 V0). An automatic upgrade grabs an MP3-320 (same extension, same template, so the same path); the organizer recycles the old file and moves the new one to exactly `held.Path`; the process is killed before line 964 commits. On restart `RecoverAsync` -> `FindLandedFileAsync`: `File.Exists(DownloadPath)` is false (it was moved), the candidate at `held.Path` is skipped by the `!(song.File is { } held && PathRules.AreEqual(held.Path, candidate))` clause even though its mtime is newer than `since`; `matches` is empty -> item reset to Completed -> `ImportCoreAsync` -> line 274 `File.Exists(downloadPath)` false -> `FailAsync(..., allowNextAttempt: true)` -> `TryNextAsync` grabs the next candidate of the run. That candidate's import passes `ReplacesPath = song.File.Path` (line 786), which is now the unrecorded upgraded file, so the placer recycles the good upgrade and files the next (ranked lower) candidate in its place; the song row never learns the first upgrade happened. The recovery mechanism exists for exactly this window and silently does nothing for every upgrade whose new path equals the old one.
- evidence: `if (File.Exists(candidate) && !(song.File is { } held && PathRules.AreEqual(held.Path, candidate)) && File.GetLastWriteTimeUtc(candidate) >= since)` (ImportRecovery.cs:153-155); the placer recycles `replaces` first (FilePlacer.cs:119-133) and then `ResolveCollision(target)` finds the now-free same path (FilePlacer.cs:173).
- fix: Drop the "row points at it" exclusion and rely on the mtime test (the tag writer rewrites the file after `StateChangedAt`, so an old file at that path is already excluded by `>= since`), or treat "row points at it AND mtime >= since" as the landed upgrade. The existing tests (`Records_the_file_an_interrupted_import_had_already_placed`, `..._replacement_of_a_reference_file_...`) only cover a song without a library file or with a reference file elsewhere; add a case where `song.File.Path == landed`.

### core-import-02: The Opus-to-.ogg rename is not saved before verification, so a shutdown strands the download
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Importing/ImportService.cs:387
- file: src/Wondarr.Core/Importing/ImportService.cs:412
- file: src/Wondarr.Core/Importing/ImportService.cs:446
- file: src/Wondarr.Core/Importing/ImportService.cs:218
- summary: With a YouTube rule whose `opusContainer` is `ogg`, the import renames `<id>.opus` to `<id>.ogg` on disk and sets `item.DownloadPath = renamed` but does not save; the next save is only reached after the verification (ffprobe, full ffmpeg decode, fpcalc, AcoustID HTTP). The sibling transcode branch saves immediately with `CancellationToken.None` for exactly this reason (lines 444-446); the rename branch does not.
- scenario: Library output policy `youtube: { codec: opus, opusContainer: ogg }`. A YouTube download finishes; the import renames the file, then the container is stopped (SIGTERM) while `VerifyAsync` is running; `OperationCanceledException` propagates through line 218-221 and nothing is saved: the row stays `Importing` with `DownloadPath = .../<id>.opus`. On restart `RecoverAsync` -> `FindLandedFileAsync` sees the `.opus` gone and nothing landed -> resets to Completed; `ImportCoreAsync` then fails at line 274-288 "Downloaded file is missing" with `allowNextAttempt: true` and grabs the next candidate. The perfectly good `<id>.ogg` stays orphaned in the per-grab folder (FailAsync never deletes it) and a second download is made.
- evidence: `File.Move(downloadPath, renamed, overwrite: true); ... importPath = renamed; extension = "ogg"; item.DownloadPath = renamed;` (387-412) with no `SaveChangesAsync` until the deferral/reject/fail/record paths; compare `await _database.SaveChangesAsync(CancellationToken.None)` right after the transcode's `File.Delete(downloadPath)` (437-446).
- fix: Save the item (with `CancellationToken.None`) immediately after the rename, as the transcode branch does. The only test of this branch (`Keep_with_the_ogg_container_renames_the_remux`) is the happy path; there is no test for a cancellation between the rename and the first save.

### core-import-03: A move whose source cannot be unlinked leaves an unrecorded copy in the library
- severity: medium
- confidence: medium
- category: data-loss
- file: src/Wondarr.Core/Organizer/FilePlacer.cs:339
- file: src/Wondarr.Core/Organizer/FilePlacer.cs:341
- file: src/Wondarr.Core/Organizer/FilePlacer.cs:226
- file: src/Wondarr.Core/Organizer/DiskOperations.cs:112
- file: src/Wondarr.Core/Importing/ImportService.cs:826
- summary: `TransferMode.Move` calls `File.Move(source, target, overwrite: false)`. On Unix, .NET's own cross-device path copies (or hard-links) to `target` and then unlinks `source`; if the unlink fails the copy is left at `target` and an `IOException` surfaces. The placer's fallback filter `when (!_disk.FileExists(target) && ...)` is then false, the plain `catch (IOException)` returns `Failed(..., RollBack(...))` with `FinalPath == null`, and the import fails the item without recording the file that is now in the library. The placer's own explicit fallback (lines 357-366) handles the same outcome correctly by throwing `PlacementTransferException(target)`; only the path through .NET's internal EXDEV handling is missed.
- scenario: Download folder on another filesystem than the library (CIFS/NFS share for slskd, local disk for the library). The move copies the file to `/lib/A/B/01 - T.flac`; `unlink` of the download fails (CIFS "text file busy" while slskd still holds the file, or the per-grab directory is not writable by Wondarr's uid while the file is). Result: `PlacementResult(false, FinalPath: null, ...)`, the old file is rolled back from the bin, `ImportCoreAsync` takes the "placement failure" branch (826-866), fails the item with no next attempt, and the library now holds an untracked copy at the exact target path that Plex will pick up. The next grab of the song is placed beside it as `(2)`, and the orphan is never cleaned up.
- evidence: `catch (IOException) when (!_disk.FileExists(target) && !_disk.AreSameFile(source, target))` (341) followed by `catch (IOException exception) { return Failed(exception.Message, RollBack(recycled, replaces)); }` (226-229); the import treats `FinalPath == null` as nothing placed (826-866). What would confirm: dotnet/runtime `FileSystem.Unix.cs` `MoveFile` EXDEV branch (`CopyFile(...); DeleteFile(sourceFullPath);` with no cleanup of the destination when the delete throws).
- fix: In the plain `IOException` catch of `Transfer`/`PlaceAsync`, check whether `target` now exists with the source's size (and is not the same inode as the source) and, if so, return the same "placed but download not consumed" result as `PlacementTransferException`, so the import records the file. `FilePlacerTests.Reports_the_target_when_the_file_landed_but_the_download_could_not_be_removed` only exercises the explicit fallback through a fake disk; there is no test for an `IOException` from `MoveFile` that leaves the target behind.

### core-import-04: A recycled file whose time stamp could not be set is deleted on the next Housekeeping, not after the retention
- severity: medium
- confidence: medium
- category: data-loss
- file: src/Wondarr.Core/Organizer/RecycleBin.cs:154
- file: src/Wondarr.Core/Organizer/RecycleBin.cs:156
- file: src/Wondarr.Core/Organizer/RecycleBin.cs:236
- summary: The cleanup clock is the recycled file's mtime, stamped after the move. When the stamp fails (logged and swallowed) the file keeps its original mtime, and `CleanupAsync` deletes every file whose mtime is older than `recycle_bin_cleanup_days` (default 7). A library file replaced by an upgrade is nearly always older than 7 days, so it is deleted at the next daily Housekeeping instead of a week later.
- scenario: Library files owned by uid 1000 (ripped by the user, or imported by another app) with group write; Wondarr runs as uid 1001 in that group. Recycling is a rename (directory permission only) and succeeds; `File.SetLastWriteTimeUtc` -> `utimensat` with explicit times needs ownership and fails with EPERM -> `StampFailed` warning. The next Housekeeping run (`HousekeepingCommandHandler` "recycle bin" step, daily) sees `GetLastWriteTimeUtc(file) < cutoff` and deletes the replaced file within 24 h of the upgrade. The user who meant to retrieve it from the bin finds it gone, and the only notice was a warning line in the log.
- evidence: `_disk.SetLastWriteTimeUtc(recycled, ...)` inside `catch (... IOException or UnauthorizedAccessException) { RecycleBinLog.StampFailed(...); }` (152-159) and `if (_disk.GetLastWriteTimeUtc(file) >= cutoff) { continue; } ... _disk.DeleteFile(file);` (236-243). What would confirm: `utimensat(2)` EPERM for a non-owner setting explicit times.
- fix: Record the recycle time independently of the file's mtime when the stamp fails (for example a tiny `<name>.recycled` marker written by Wondarr's own uid, or a manifest in the bin root), and have `CleanupAsync` use that time; alternatively skip files the stamp failed on unless a marker says they are old enough. There is no test for a cleanup after a failed stamp.

### core-data-02: Deleting a song leaves its reference row `Identified` with no song; the file is never offered or identified again
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Persistence/DomainModelConfiguration.cs:413
- file: src/Wondarr.Core/Songs/SongService.cs:528
- file: src/Wondarr.Core/Songs/SongEditorService.cs:297
- file: src/Wondarr.Core/References/ReferenceScanner.cs:233
- file: src/Wondarr.Core/References/ReferenceIdentifier.cs:159
- summary: `reference_file.song_id` is `ON DELETE SET NULL`, but the row's `state` stays `Identified`. The scanner leaves an unchanged file in whatever state it has, the identifier only takes `Pending` rows, and the Match queue lists only `Ambiguous`/`Unmatched`, so the row is stuck: counted as "identified" with no song, invisible to the user, and never re-identified while the file stays unchanged on disk.
- scenario: A reference file was identified as song 42 (row `Identified`, `song_id = 42`, `song_file` reference row). The user deletes song 42 (`DELETE /api/v1/song/42` or the mass editor); the cascade removes the `song_file`, the FK sets `reference_file.song_id = NULL`, state remains `Identified`. Later an import list (or the user) adds the same recording again as song 77: `IsHeldThroughReferenceAsync(77)` finds no row with `song_id = 77`, so the song is searched and downloaded although the user's own copy sits in the reference library; the library's counts keep reporting the file as identified; nothing ever re-runs identification on it (ReferenceScanner.cs:233-245 only touches `LastSeenAt` for an unchanged file; ReferenceIdentifier.cs:159-161 selects `State == Pending` only).
- evidence: `entity.HasOne(x => x.Song).WithMany().HasForeignKey(x => x.SongId).OnDelete(DeleteBehavior.SetNull);` (DomainModelConfiguration.cs:413-416); `_database.Songs.Remove(song); await _database.SaveChangesAsync(...)` with no reference-row handling (SongService.cs:528-529); `.Where(row => row.ReferenceLibraryId == referenceLibraryId && row.State == ReferenceFileState.Pending)` (ReferenceIdentifier.cs:160-161).
- fix: When a song is deleted (SongService.DeleteAsync, SongEditorService.DeleteAsync), set the `Identified` rows that point at it back to `Pending` (and clear `IdentifiedBy`/`Confidence`) in the same SaveChanges; alternatively have the scan treat `Identified && SongId == null` as `Pending`. No test covers deleting a song a reference row identifies (tests/Wondarr.Core.Tests/Songs/SongServiceTests.cs and References/* have none).

### core-data-03: Skip, re-resolve and re-identification release a song without following its second Identified copy
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/References/ReferenceMatchService.cs:678
- file: src/Wondarr.Core/References/ReferenceMatchService.cs:799
- file: src/Wondarr.Core/References/ReferenceIdentifier.cs:288
- file: src/Wondarr.Core/References/ReferenceScanner.cs:395
- file: src/Wondarr.Core/Wanted/WantedService.cs:50
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1484
- summary: The scanner and the library delete repoint a leaving song to another `Identified` row of the same song before releasing it (and the architecture doc promises "a song with a second copy ... is never released when one copy goes"). The three other release paths (`SkipAsync`, a re-resolve to a different song in `ReferenceMatchService.LinkAsync`, and `ReferenceIdentifier.ReleaseAsync` when a changed file is no longer that song) delete the `song_file` outright. The second copy's row stays `Identified`, so the song is treated as owned (hidden from Wanted, never searched) yet holds no file, and nothing repairs it while the second file stays unchanged.
- scenario: Two reference files of one recording: row A (linked, `song_file` points at A) and row B (`Identified`, message "duplicate: the song already has a file"). The user opens the Match queue entry for A and chooses Skip (or accepts another candidate). `ReleaseAsync` deletes the song's `song_file`; A becomes Skipped. Now `WantedService.GetMissingAsync` excludes the song because B is `Identified`, `IsHeldThroughReferenceAsync` returns true so no search runs, the song list shows it without a file, and the next scan does nothing because B is unchanged (`sameFile`). The song stays file-less until B itself is deleted on disk and ages past the grace.
- evidence: `if (row.SongId is { } songId) { await ReferenceOwnership.ReleaseAsync(_database, songId, AbsolutePath(library, row), cancellationToken)...; } row.SongId = null; row.State = ReferenceFileState.Skipped;` (ReferenceMatchService.cs:678-686) with no lookup of other Identified rows, versus the scanner's `var other = await database.ReferenceFiles...Where(candidate => candidate.SongId == songId && candidate.State == ReferenceFileState.Identified && candidate.Id != row.Id)` + `RepointAsync` (ReferenceScanner.cs:395-443). Docs: "A song with a second copy (another identified row ...) is never released when one copy goes" (docs/architecture/LIBRARY_OUTPUT.md, §7.6 "A reference file that goes missing").
- fix: Factor the scanner's "find another Identified row and `RepointAsync`, else `ReleaseAsync`" into ReferenceOwnership and use it from SkipAsync, the match service's LinkAsync and ReferenceIdentifier.ReleaseAsync. No test in tests/Wondarr.Core.Tests/References/ReferenceMatchServiceTests.cs or ReferenceIdentifierTests.cs covers a release while a second Identified copy exists.

### core-search-01: SoulseekFilenameParser throws OverflowException on a 10+ digit dash segment
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Sources/SoulseekFilenameParser.cs:398
- file: src/Wondarr.Core/Sources/SoulseekFilenameParser.cs:144
- file: src/Wondarr.Sources.Slskd/SoulseekSourceProvider.cs:134
- file: src/Wondarr.Sources.Slskd/SoulseekSourceProvider.cs:165
- file: src/Wondarr.Core/Sources/ContainerMatcher.cs:133
- file: src/Wondarr.Core/References/ReferenceIdentifier.cs:866
- summary: `SplitSegments` matches a dash segment with `^\d+$` and then calls `int.Parse` on it; any run of ten or more digits above 2 147 483 647 throws `OverflowException` out of a parser documented as "Never throws". The slskd provider maps a whole query's responses in one `Map` call inside a `catch (Exception) … continue`, so one such file name from one peer discards every candidate of that query.
- scenario: A peer shares `Artist - 1700000000 - Title.mp3` (a Unix-timestamp or catalogue number as its own ` - ` field). `SoulseekFilenameParser.Parse` → `SplitSegments` → `int.Parse("1700000000")` overflows. In `SoulseekSourceProvider.SearchAsync` the exception lands in the generic catch after `submitted.Add(query)` already ran, so the query counts as submitted, its candidates (all other peers' files too) are dropped, and if the same peer answers every query the run ends `NoResults` with no message and the song's backoff steps up. The same path runs per file in `ContainerMatcher.Find` (a torrent file list, including inside `ContainerBundler.FindAsync` under the slot gate: the whole grab fails as `Failed`) and in the reference-library identifier.
- evidence: `if (DigitsOnlyRegex.IsMatch(trimmed)) { trackNumber ??= TrackNumberFrom(int.Parse(trimmed, CultureInfo.InvariantCulture)); continue; }` (parser 398-402); `/// Never throws and never returns <c>null</c>` (parser 144); provider: `submitted.Add(query); foreach (var candidate in SoulseekCandidateMapper.Map(result.Responses, query))` then `catch (Exception exception) { firstError ??= …; LogQueryFailed(…); continue; }`.
- fix: Use `int.TryParse` (or cap the digit run, e.g. `^\d{1,4}$`) and treat an unparsable or over-long number as a plain segment, not a track number; add a golden case with a 10-digit segment. Optionally make the mapper skip the one file that fails to parse instead of losing the query.

### core-search-02: Tier early-stop ignores peer reputation, so YouTube is skipped when every Soulseek hit is on cooldown
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Searching/SongSearchService.cs:537
- file: src/Wondarr.Core/Searching/SongSearchService.cs:544
- file: src/Wondarr.Core/Decisions/DecisionEngine.cs:335
- file: docs/architecture/ARCHITECTURE.md:42
- summary: The decision whether to ask the next tier is taken with `context` before `Reputation` is loaded, so a candidate that the final evaluation rejects with `UserOnCooldown` (or whose reputation adjustment pushes it below `MinScore`) still stops the ladder; the run then ends `NoAcceptableCandidate` without YouTube or the indexers ever being asked, which also lengthens the song's backoff.
- scenario: Song X, Soulseek and YouTube enabled. Soulseek returns three files, all from user `bob`, who failed twice in the last 24 h (`FailuresLast24Hours >= 2`). The tier check `_engine.Evaluate(context, candidates).Any(d => d.Accepted)` is true (no reputation in `context`), the loop `break`s, `GetReputationAsync` then runs, the final `Evaluate` rejects all three (`UserOnCooldown`), `accepted == 0` → outcome `NoAcceptableCandidate`. ARCHITECTURE §5.2 step 4 says YouTube runs "only if [Soulseek] yields nothing acceptable", and it did yield nothing acceptable. The same holds for the per-query `IsPoolGoodEnough` callback.
- evidence: `if (trigger != SearchTrigger.Manual && _engine.Evaluate(context, candidates).Any(decision => decision.Accepted)) { break; }` (537-541) precedes `var reputation = await _users.GetReputationAsync(…)` and `context = context with { Reputation = reputation };` (544-553); the code comment at 534-536 acknowledges "a candidate this check accepts can still be rejected by the final evaluation".
- fix: Load the reputation of the tier's providers before the tier check (one `GetReputationAsync` per tier, cheap), or re-evaluate after loading it and continue to the next tier when nothing survives. No test covers a cooldown peer with a second tier (tests/Wondarr.Core.Tests/Searching/SourceTierOrderingTests.cs has only accepted/rejected cases).

### core-external-02: The all-zero GUID passes LookupInput but crashes the resolver with ArgumentException
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Identity/LookupInput.cs:110
- file: src/Wondarr.Core/Metadata/MusicBrainz/MusicBrainzClient.cs:307
- file: src/Wondarr.Core/Identity/IdentityResolver.cs:271
- file: src/Wondarr.Core/ImportLists/PasteListService.cs:293
- summary: `LookupInput.Parse` accepts `00000000-0000-0000-0000-000000000000` as an `MbRecordingId` (`Guid.TryParse` succeeds), but `MusicBrainzClient.NormalizeMbid` refuses `Guid.Empty` with `ArgumentException`, which no caller in the paste/sync pipeline catches.
- scenario: A pasted list containing the line `00000000-0000-0000-0000-000000000000` (the id Wondarr itself advertises as the test song's `mbRecordingId` in the Webhook test payload, WebhookProvider.cs:185). `ResolveAsync` -> `ResolveRecordingAsync` -> `GetIdentityAsync` -> `GetRecordingAsync` throws before any request; `ResolveLineAsync` does not catch `ArgumentException`; `ProcessAsync` aborts, the line stays `Pending`, and every later `BulkAddSongs` of that list fails at the same line. A ListenBrainz row carrying that MBID would do the same to a scheduled sync.
- evidence: `if (Guid.TryParse(text, out var guid)) { return Recording(guid.ToString("D")); }` (LookupInput.cs:110-113) versus `if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var guid) || guid == Guid.Empty) { throw new ArgumentException(` (MusicBrainzClient.cs:307-311).
- fix: Have `LookupInput.Parse` treat `Guid.Empty` as `Unsupported` (and `RecordingMbid` helpers likewise), and catch `ArgumentException` per line in `ResolveLineAsync` as in core-external-01.

### sources-01: NzbFileList's yEnc regex timeout is uncaught and aborts the whole song search
- severity: medium
- confidence: high
- category: correctness
- file: src/Wondarr.Sources.Torznab/Parsing/NzbFileList.cs:40
- file: src/Wondarr.Sources.Torznab/Parsing/NzbFileList.cs:177
- file: src/Wondarr.Sources.Torznab/Parsing/NzbFileList.cs:213
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.cs:399
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Nzb.cs:195
- file: src/Wondarr.Sources.Torznab/Searching/ReleasePushHandler.cs:115
- file: src/Wondarr.Core/Searching/SongSearchService.cs:523
- summary: `NzbFileList` gives its three patterns a 1 s `MatchTimeout` but never catches
  `RegexMatchTimeoutException` (unlike `ReleaseTitleParser`, which does at lines 165-173). The yEnc
  pattern `(?:\S+[ ])*\S+[.][A-Za-z0-9]{2,4}[ ]+yEnc` is quadratic on a subject that has many
  dotted tokens and no `yEnc`, so one over-long `<file subject="…">` in an NZB makes the exception
  escape every caller, none of which lists it in its catch filters.
- scenario: An indexer (buggy or hostile; the download cap is 10 MiB) answers an NZB whose one
  `<file>` subject is ~30 KB of space-separated tokens with dots and no `yEnc`. Measured with .NET's
  `Regex` and the exact pattern/options: 1 000 tokens (9.9 KB) take 275 ms, 3 000 tokens (32 KB)
  throw `RegexMatchTimeoutException` at 1 000 ms. In a search, `ListNzb` → `NzbFileList.Parse`
  throws inside the shared flight; `ReadContainersAsync`'s filter (`IndexerException or
  HttpRequestException or TorrentMetainfoException or XmlException or InvalidDataException`) does
  not match, `Parallel.ForEachAsync` rethrows, `IndexerSourceProvider.SearchAsync` throws, and
  `SongSearchService` (no try/catch around `provider.SearchAsync` at :523) loses the whole run for
  that song, including the candidates the other tiers already found. In a grab, `DownloadNzbAsync`
  catches only `XmlException`, so the grab throws; because `FetchNzbAsync` only calls
  `_nzbCache.Put` after `ListNzb` succeeded, every retry downloads the NZB again from the
  indexer's daily quota and fails the same way. `release/push` (:115 filter) answers 500.
  CHANGELOG 0.1.15 records exactly this failure shape ("the limit counts time spent waiting for the
  CPU … the whole indexer search failed") and raised the limit instead of catching it.
- evidence:
  ```
  private static readonly Regex YEncNameRegex = new(
      @"(?<name>(?:\S+[ ])*\S+[.][A-Za-z0-9]{2,4})[ ]+yEnc", ... MatchTimeout);
  ...
  var yEnc = YEncNameRegex.Match(subject);          // ExtractFileName, no try/catch
  return stem.Length >= 16 && ObfuscatedStemRegex.IsMatch(stem);   // IsObfuscatedName
  ```
  `dotnet fsi` run: `n=1000 len=9889 matched=false 275ms` / `n=3000 len=31889 TIMEOUT after 1000ms`.
- fix: Catch `RegexMatchTimeoutException` in `ExtractFileName` (treat as "no name") and
  `IsObfuscatedName` (treat as obfuscated), and/or refuse to regex a subject longer than a sane cap
  (a few hundred characters) before matching; add `RegexMatchTimeoutException` to the catch lists at
  IndexerSourceProvider.cs:399 and Nzb.cs:195 so a pathological release is counted "unreadable"
  instead of ending the search. Test gap: tests/Wondarr.Sources.Tests/Torznab/Parsing/NzbFileListTests.cs
  has no over-long-subject case; nothing in the tests mentions `RegexMatchTimeout`.

### sources-02: A listed NZB file SABnzbd renamed by sanitising is deleted and re-grabbed, never blocklisted
- severity: medium
- confidence: medium
- category: data-loss
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:845
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:850
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:887
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:919
- file: src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:1016
- file: src/Wondarr.Sources.Torznab/Clients/SabnzbdProxy.cs:207
- summary: When a grab carries a `FilePath` (the name read from the NZB subject) and the finished
  folder holds no file of exactly that name, the only fallback is `FindRenamedAsync`, which tries
  `<job name>.<ext>` and `<folder name>.<ext>` (SABnzbd's deobfuscation rename). Any other rename —
  SABnzbd replacing a character the host file system forbids (`?`, `:`, `*`, `"`, `<`, `>`, `|` on a
  Windows SABnzbd, or with its "sanitise" switch on) — ends in "The song is not in the downloaded
  post": the job is removed from history with `del_files=1`, the completed folder is deleted by
  `CleanJobFolderAsync`, and the item fails with `blocklistCandidate: false`, so the next search grabs
  the same post again.
- scenario: SABnzbd runs on Windows (Wondarr in Docker with a remote path mapping, a supported setup).
  The NZB subject names `"01 - What Is Love?.flac"`; the search read that name and stored it as the
  grab's `FilePath`. SABnzbd writes `01 - What Is Love_.flac`. On completion `UsenetStatusAsync`
  (:845-847) finds no exact name, `FindRenamedAsync` looks only for `<jobname>.flac` /
  `<folder>.flac` (:929-935) and returns null, so :862 calls `FailedJobAsync(…, finishedFolder:
  folder)`: `RemoveJobAsync` sends `mode=history&name=delete&del_files=1` (SabnzbdProxy.cs:207-215,
  which deletes the job's files), then `CleanJobFolderAsync` deletes the folder. The item fails,
  nothing is blocklisted, and the following search re-downloads the NZB (quota) and the post, and
  deletes it again. `MatchUnpacked` (:965-977), which would have found the file by the matcher, is
  only reached when `grab.FilePath is null`.
- evidence:
  ```
  var source = grab.FilePath is { } path
      ? files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), Path.GetFileName(path), ...))
          ?? await FindRenamedAsync(services, grab, job, folder, files, path, cancellationToken)
      : MatchUnpacked(folder, files, grab);
  ...
  return await FailedJobAsync(services, client, grab, jobId, message, cancellationToken,
      blocklistCandidate: grab.FilePath is null && hasAudio, finishedFolder: folder)
  ```
- fix: When the listed name is absent, fall back to `MatchUnpacked` (the matcher over the folder) or
  compare names after applying SABnzbd's illegal-character substitution, before giving up; and do
  not delete a completed job's folder for "listed file not found" (the client's doing, as the comment
  at :852-855 itself says). Test gap: IndexerSourceGrabTests.cs:1413-1474 covers the deobfuscation
  rename only; no case has a sanitised name. Confidence is medium because SABnzbd's exact
  substitution set per platform was not re-verified against its source here; the Windows-illegal
  characters are certain.

### frontend-03: Removing a queue item with blocklist does not refresh the blocklist shown beside it
- severity: medium
- confidence: high
- category: correctness
- file: frontend/src/api/queue.ts:130
- file: frontend/src/api/queue.ts:183
- file: frontend/src/pages/SongPage.tsx:221
- file: frontend/src/api/wanted.ts:24
- summary: `useRemoveQueueItem` (with `blocklist: true`) only invalidates `['queue']`; the entry it adds to the blocklist lives under `['blocklist']`, which nothing invalidates (no SignalR message covers it either). `useGrabRelease` likewise writes a `grabbed` history event but only invalidates `['queue']`.
- scenario: Song page → "Queue & blocklist" tab, where `QueuePage` and `BlocklistTab` are both mounted and visible. Row menu → "Remove and blocklist" → confirm. The queue row disappears but the Blocklist table directly below still says "Nothing is blocked." until the tab is left and re-opened (`refetchOnWindowFocus` is off app-wide). Same on the song page with the interactive panel open above the History tab: a grab shows no `grabbed` row. (Activity → tabs are not affected: Mantine 9.7 hides inactive panels in a React `Activity`, whose queries unsubscribe and refetch when revealed.)
- evidence: `onSuccess: () => queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY })` (queue.ts:130, 183); `BLOCKLIST_QUERY_KEY = ['blocklist']` is only invalidated by `useDeleteBlocklistItem` (wanted.ts:149); SongPage.tsx:221-233 renders `<QueuePage songId>` and `<BlocklistTab songId>` together.
- fix: In `useRemoveQueueItem.onSuccess` also invalidate `BLOCKLIST_QUERY_KEY` (and `HISTORY_QUERY_KEY`/`WANTED_QUERY_KEY`, since the song becomes wanted again) when `blocklist` was true; in `useGrabRelease.onSuccess` invalidate `HISTORY_QUERY_KEY`.

### frontend-04: Deleting the open library leaves Settings → Library with no panel shown
- severity: medium
- confidence: high
- category: correctness
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:645
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:662
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:674
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:709
- summary: The active tab is kept as the library id in `active` state and only falls back to the first row while `active` is null. The Delete button is inside the library's own panel, so a delete always removes the active library; after the refetch `selected` names an id no `Tabs.Panel` has, and the card shows the tab strip with an empty body.
- scenario: Two libraries; open the non-default one, "Delete library…" → "Delete library". The modal closes, `LIBRARIES_QUERY_KEY` refetches, the tab disappears, `selected` is still its id, every `Tabs.Panel` is inactive and the form area is blank until the user clicks another tab.
- evidence: `const selected = active ?? String(rows[0].id);` (662) with `<Tabs value={selected} onChange={setActive}>` (674); the delete control is rendered inside `rows.map(library => <Tabs.Panel …>)` (688-712); `DeleteLibraryModal` only calls `onClose` on success (616).
- fix: Resolve the selection against the rows (`rows.some(r => String(r.id) === active) ? active : String(rows[0].id)`), or reset `active` in the delete's `onSuccess`.

### frontend-05: `['songs']` prefix invalidation refetches the song page's external lookups on every library change
- severity: medium
- confidence: medium
- category: correctness
- file: frontend/src/api/songs.ts:109
- file: frontend/src/api/songs.ts:343
- file: frontend/src/api/songs.ts:370
- file: frontend/src/api/songs.ts:191
- file: frontend/src/app/signalr.ts:140
- summary: `useSongDetails` (`['songs','details',id]`, which the server answers from MusicBrainz, Deezer and Last.fm) and `useSongLyrics` (`['songs','lyrics',id]`, an LRCLIB lookup) are children of `SONGS_QUERY_KEY = ['songs']`. `invalidateLibrary` runs on every song add/edit/delete and on every `song` SignalR push, so each of those re-runs the external lookups for whichever song page is open.
- scenario: On a song's page with the Lyrics tab open, flip the Monitored switch. `useUpdateSong.onSuccess → invalidateLibrary → invalidateQueries(['songs'])` refetches `['songs','one',id]` (wanted) but also `['songs','details',id]` and `['songs','lyrics',id]`, i.e. up to four outbound calls, against providers the coding standard rate-limits at 1/s (MusicBrainz) and 200–500 ms gaps (LRCLIB). The same happens for every import that lands anywhere while the page is open (`song` push).
- evidence: `void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY })` in `invalidateLibrary` (songs.ts:110) and in the `song` branch (signalr.ts:140); `queryKey: [...SONGS_QUERY_KEY, 'details', id]` (343); `queryKey: [...SONGS_QUERY_KEY, 'lyrics', id]` (370) with the comment "a lookup can reach the network" (364-367).
- fix: Put the external-source queries under their own root keys (`['song-details', id]`, `['song-lyrics', id]`) or give them a long `staleTime` and invalidate the list key with a predicate. Confidence is medium only because the server may cache the upstream answers; the extra round trips to the API itself are certain.

### frontend-11: "saves the monitored flag" test does not assert the flag that was sent
- severity: medium
- confidence: high
- category: test-gap
- file: frontend/src/pages/SongPage.test.tsx:750
- summary: The test only checks that some PUT went to `/api/v1/song/12`; it never reads the request body, so it would pass if the switch sent `monitored: true`, `null` (the "leave alone" value `useUpdateSong` sends for an omitted member) or an unrelated field.
- scenario: Change `onMonitored={(monitored) => update.mutate({ id: songId, monitored })}` to `update.mutate({ id: songId })` (or invert the flag) and the test still passes.
- evidence: `expect(put?.url).toContain('/api/v1/song/12');` is the only assertion (SongPage.test.tsx:758-765).
- fix: Read the body (`await put.json()`) and assert `{ monitored: false, qualityProfileId: null }` for a song fixture that starts monitored, as the Match queue tests do with `lastBody(...)`.

### ops-01: Release tags move on Phase 0/1 only; the Phase 2–7 gates are not in the promote chain
- severity: medium
- confidence: high
- category: test-gap
- file: .github/workflows/release.yml:33
- file: .github/workflows/release.yml:147
- file: .github/workflows/release.yml:161
- file: .github/workflows/ci.yml:3
- file: .github/workflows/ci.yml:22
- file: docs/architecture/DEPLOYMENT.md:23
- summary: `promote` (which writes `:develop`, `:<version>`, `:0.1` and `:latest`) needs `test` (ci.yml called with `tests-only: true`, so its `docker` job and the full `scripts/smoke-test.sh` are skipped) and `smoke` (which sets `SMOKE_PHASE2..7=off`). The Phase 2–7 gates (imports, fingerprint traps, upgrade, backup/restore, lists, torrent/usenet) only ever run in ci.yml's push/PR `docker` job, which nothing in release.yml waits for, and ci.yml does not trigger on `v*` tags at all.
- scenario: Push A to `main`, then push B ten minutes later. ci.yml's group `ci-CI-refs/heads/main` has `cancel-in-progress: true`, so A's full gate is cancelled mid-run; release.yml (its own group, `cancel-in-progress: false`) finishes A and moves `:develop` to A. Tag `v0.1.17` on B: ci.yml never runs for the tag, release's `test` runs unit tests only, `smoke` runs Phase 0 and 1, and `:latest` moves even if B's Phase 2–7 gate in the CI run was red or cancelled. DEPLOYMENT.md says "a red image never moves `:0.1`", which is only true for Phase 0/1 and the unit suites.
- evidence: release.yml 34–36 `uses: ./.github/workflows/ci.yml` / `tests-only: true`; 149–154 `SMOKE_PHASE2: "off"` … `SMOKE_PHASE7: "off"`; ci.yml 3–5 `on: push: branches: [main]` (no `tags:`); ci.yml 81 `… && !inputs.tests-only` skips the only job that runs the full gate.
- fix: Either have the amd64 smoke in release.yml run the full gate (drop the `SMOKE_PHASE*=off` on the amd64 leg, or add a third matrix leg), or trigger ci.yml on `v*` tags and make `promote` depend on a full-gate job; at minimum correct the DEPLOYMENT.md sentence.

### ops-02: HEALTHCHECK hardcodes port 1077, so a changed `server.port` makes the container permanently unhealthy
- severity: medium
- confidence: high
- category: correctness
- file: docker/Dockerfile:192
- file: docker/Dockerfile:211
- file: website/src/content/docs/reference/configuration.md:26
- file: website/src/content/docs/getting-started/install.md:126
- summary: The image's HEALTHCHECK probes `http://localhost:1077/ping`, but `server.port` (config.yml) / `APP__SERVER__PORT` is a documented setting and the Dockerfile itself says the app binds "the port from config.yml". Nothing re-points the probe, and the docs do not warn.
- scenario: A user sets `port: 8080` under `server:` in config.yml (or `-e APP__SERVER__PORT=8080`) and maps `-p 8080:8080`. The app starts fine, but every probe fails; after the 60 s start period the container is `unhealthy` for ever. `compose depends_on: condition: service_healthy`, Portainer/Unraid health badges, autoheal-style restarters and `scripts/smoke-test.sh` (`wait_healthy` fails on `unhealthy`) all treat it as down.
- evidence: Dockerfile 211–212 `HEALTHCHECK … CMD wget -q --spider http://localhost:1077/ping || exit 1`; 192–193 "Wondarr binds the port from config.yml (server.port, 1077 by default)"; configuration.md 26 `| port | 1077 | HTTP port (1 to 65535). |`; install.md 126 "The image checks `http://localhost:1077/ping` every 30 seconds." The TrueNAS template already works around it (templates/docker-compose.yaml 12–13 sets its own healthcheck from the chosen port).
- fix: Make the probe read the port (a tiny `/usr/local/bin/healthcheck` that takes `APP__SERVER__PORT` or greps `server.port` from `/config/config.yml`, default 1077), or document in install.md and the configuration reference that `server.port` must stay 1077 inside the container and only the host mapping should change.

### ops-03: smoke-test.sh cleanup leaves the Phase 7 container running and the p6/p7 work trees behind on failure
- severity: medium
- confidence: high
- category: resource-leak
- file: scripts/smoke-test.sh:69
- file: scripts/smoke-test.sh:72
- file: scripts/smoke-test.sh:275
- file: scripts/smoke-test.sh:278
- summary: `cleanup` removes `$NAME`, `-fresh`, `-ext`, `-extfake` and `-p6` but not `${NAME}-p7`, and its in-container `rm -rf` covers `/w/config /w/data /w/fresh /w/ext` but not `/w/p6` or `/w/p7`. `run_phase7` only removes its container on success (line 295).
- scenario: Any Phase 7 check fails → `fail` → `exit 1` → `cleanup`. The `wondarr-smoke-<pid>-p7` container keeps running, holding host port `$PORT+4` (1081) and its bind mounts. The next run on that host reaches Phase 7, `docker run -p 1081:1077` fails with "port is already allocated", `set -e` aborts the run, and the leak repeats. Separately, `$WORK/p6` and `$WORK/p7` hold files created inside the container as `PUID:PGID` (1234:2345) under 0775 directories, so the host-side `rm -rf "$WORK"` (line 73, `|| true`) silently leaves `/tmp/tmp.XXXX/p6/...` and `p7/...` behind on every run that reaches Phase 6, even successful ones (on CI runners this is harmless; on a developer box it accumulates).
- evidence: line 69 `$DOCKER rm -f "$NAME" "${NAME}-fresh" "${NAME}-ext" "${NAME}-extfake" "${NAME}-p6"`; line 72 `… -c 'rm -rf /w/config /w/data /w/fresh /w/ext'`; line 275 `P7="${NAME}-p7"`; line 241 `mkdir -p "$WORK/p6/config" …`; line 278 `mkdir -p "$WORK/p7/config" …`.
- fix: Add `"${NAME}-p7"` to the `rm -f` list and `/w/p6 /w/p7` to the in-container `rm -rf` (or simply `rm -rf /w/*`).

### ops-11: TrueNAS template never sets PUID/PGID; it relies on the apps library injecting them from `run_as`
- severity: medium
- confidence: low
- category: correctness
- file: packaging/truenas/wondarr/templates/docker-compose.yaml:7
- file: packaging/truenas/wondarr/templates/docker-compose.yaml:9
- file: packaging/truenas/wondarr/questions.yaml:76
- summary: The template comments "applies PUID/PGID (set from run_as)" and the question text says the UID "that Wondarr runs as and that owns its files", but the template only calls `c1.set_user(0, 0)` and adds `APP__SERVER__PORT`, `APP__SOULSEEK__LISTEN_PORT`, the Soulseek credentials and user envs; no `add_env("PUID", values.run_as.user)` appears. Whether library 2.3.17 injects `PUID`/`PGID` from `values.run_as` automatically could not be verified here (the truenas/apps repository is not attached to this session).
- scenario: If the library does not inject them, the container runs the app as uid 1000 (the image default) while the `permissions` container chowns `/config` and `/data` to 568:568 (`perms_config … "mode": "check"`). `init-wondarr-user` re-owns `/config` to 1000, but `/data` is "deliberately left alone" (init script line 6), so the first import fails to write under `/data`. The TrueNAS CI test described in packaging/README.md waits for the Docker healthcheck (`/ping`) only, which would not catch it.
- evidence: docker-compose.yaml 9–10 `{% do c1.set_user(0, 0) %}` / `add_caps([...])`; 13–21 the only `add_env` calls; `app.yaml` 38–43 `run_as_context … uid: 0 gid: 0` (generated from the compose `user:`).
- fix: Confirm with the library (`library/2.3.17/environment.py`, or render with `ci.py` and inspect the compose `environment:` for `PUID`/`PGID`). If they are absent, add `c1.environment.add_env("PUID", values.run_as.user)` and `add_env("PGID", values.run_as.group)`; if they are present, say so in the template comment so the next reader does not have to re-derive it.

### docs-user-02: install.md says Wondarr takes no backup before an upgrade; it has since 0.1.1
- severity: medium
- confidence: high
- category: stale-doc
- file: website/src/content/docs/getting-started/install.md:154
- file: website/src/content/docs/operations/tasks-backups-logs.md:39-41
- file: CHANGELOG.md:338
- summary: The Upgrading section states "Wondarr does not take a backup first", while the Tasks page and the 0.1.1 changelog describe the automatic `wondarr_pre-<version>` backup that runs before every migration (and stops the start when it fails). The same paragraph also names `/config/backups` for scheduled backups, which go to `/config/backups/scheduled`.
- scenario: A reader of the install page believes no safety net exists and either skips the upgrade or builds their own pre-upgrade routine; a reader of both pages sees the site contradict itself on a data-safety point.
- evidence: install.md:154 "The database migrates itself when the new version starts. Wondarr does not take a backup first, so make one before a major upgrade ... Scheduled backups also run weekly into `/config/backups`." tasks-backups-logs.md:41 "When a new version has database changes to apply, Wondarr first makes a backup named `wondarr_pre-<previous version>_<time>.zip` in `/config/backups/scheduled` ... If that backup fails, the upgrade stops". src/Wondarr.Core/Backup/BackupService.cs:95 `CreateCoreAsync(BackupType.Scheduled, $"wondarr_pre-{safe}", ...)`.
- fix: Replace the sentence with the behaviour the Tasks page describes (automatic pre-migration backup, keep twice the database size free) and point at that section; name `/config/backups/scheduled`.

### docs-user-03: install.md says `:latest` follows every release; the workflow moves it only for the highest stable release
- severity: medium
- confidence: high
- category: stale-doc
- file: website/src/content/docs/getting-started/install.md:152
- file: .github/workflows/release.yml:62-71
- summary: "`:latest` follows every release" is wrong for a release candidate (`v0.2.0-rc.1`) and for a backport tagged after a higher release; both leave `:latest` where it is. The 0.1.1 changelog entry (line 346) states the real rule.
- scenario: A user on `:latest` reads that it follows every release, expects to receive an announced `-rc` and does not; or expects a `v0.1.17` tagged after `v0.2.0` to move `:latest` back.
- evidence: install.md:152 "If the image is pinned to a tag such as `:0.1`, change the tag to move to a new minor version; `:latest` follows every release." release.yml:62 "# :latest follows the highest stable release only: tagging a v0.1.5 backport after v0.2.0" and :68 `if [[ "$GITHUB_REF" == refs/tags/v* && "$GITHUB_REF_NAME" != *-* ]]`. CHANGELOG.md:346 "`:latest` follows only the highest stable release".
- fix: "`:latest` follows the highest stable release; a release candidate never moves it."

### docs-user-04: first-run.md and the README say Prowlarr needs Wondarr's API key; the API page says Wondarr is not a Prowlarr app
- severity: medium
- confidence: high
- category: stale-doc
- file: website/src/content/docs/getting-started/first-run.md:28
- file: README.md:88
- file: website/src/content/docs/reference/api-and-integrations.md:58-60
- summary: Two pages list Prowlarr among the tools that send Wondarr's API key, but the integration page (and the code: there is no Prowlarr "app" endpoint) says Prowlarr does not push to Wondarr; Wondarr reads Prowlarr with Prowlarr's key.
- scenario: A user copies the key, adds Wondarr as an application in Prowlarr (Lidarr type), gets a sync error, and only the API page explains why.
- evidence: first-run.md:28 "the **API key** that tools such as Prowlarr, autobrr, Homepage and Unpackerr need"; README.md:88 "so tools like Prowlarr, autobrr and Homepage plug in the way you'd expect"; api-and-integrations.md:60 "Wondarr is not a Prowlarr "app", so Prowlarr does not push indexers to it. Instead, Wondarr uses Prowlarr as a source of indexers."
- fix: Drop Prowlarr from both lists (autobrr, Homepage and Unpackerr are the tools that send the key).

### docs-user-05: first-run.md's navigation list is missing the Updates item
- severity: medium
- confidence: high
- category: stale-doc
- file: website/src/content/docs/getting-started/first-run.md:6
- summary: The list of navigation items omits **Updates**, which sits between System and Tasks since 0.1.0 (the update check feature).
- scenario: A reader counting the items against the UI finds one the page did not mention; a later sentence in install.md relies on "**System → Updates**" existing.
- evidence: first-run.md:6 "The navigation on the left has Library, Add songs, Wanted, Match, Activity, Settings, System, Tasks, Backup and Logs."; frontend/src/components/AppLayout.tsx:47-49 `label: 'System'`, `label: 'Updates'`, `label: 'Tasks'`.
- fix: "... Settings, System, Updates, Tasks, Backup and Logs."

### docs-user-06: README "AI disclaimer" paragraph has four typos
- severity: medium
- confidence: high
- category: style
- file: README.md:13
- summary: R8. "decsions", "absolutley", "siad" and a lower-case "i" in the README's second paragraph.
- scenario: The first thing a visitor reads after the pitch is a paragraph with three misspellings.
- evidence: "All architecture and security decsions human-made ... I have absolutley no intention ... That siad if you find anything i didn't, please [create an issue]".
- fix: "All architecture and security decisions are human-made; code generation was hand-held and reviewed. I have absolutely no intention of this project becoming cooked like Huntarr. That said, if you find anything I didn't, open an issue or a PR." (also removes "please", R1).
- outcome: confirmed on re-judgement against the real /writing-style skill: spelling errors are errors in any style.

### docs-internal-04: ADR-0011 says scripts/worker.py "stays in the repo"; it is git-ignored
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/adr/0011-agentic-build-workflow.md:18
- file: docs/adr/0011-agentic-build-workflow.md:16
- summary: (S1) The 2026-10-09 amendment states the worker runner stays in the repository; `.gitignore` excludes `scripts/worker.py`, `scripts/worker_agent.py`, `scripts/test_worker_agent.py` and `scripts/openrouter_value.py`, and `git ls-files scripts` lists none of them. The 2026-10-05 amendment names `.claude/agents/worker.md` and `reviewer.md`, also git-ignored.
- scenario: A reader of the ADR expects to find and resume the worker runner from a fresh clone; there is nothing to resume.
- evidence: "`scripts/worker.py` stays in the repo, unused while the pause lasts." / `.gitignore`: "scripts/worker.py", "scripts/worker_agent.py", "scripts/test_worker_agent.py", "scripts/openrouter_value.py", ".claude/"
- fix: Amend the ADR once more: the worker scripts and agent definitions are kept locally and git-ignored, so a fresh clone does not carry them.

### docs-internal-05: ADR index does not reflect the amendments of ADR-0011
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/adr/README.md:17
- file: docs/adr/README.md:14
- file: docs/adr/0011-agentic-build-workflow.md:3
- file: docs/adr/0011-agentic-build-workflow.md:18
- summary: (S4) The index marks ADR-0008 "Accepted (amended 2026-10-07)" but ADR-0011 plain "Accepted", although ADR-0011 carries three amendments, and its index title still says "cheap OpenRouter workers" while the last amendment pauses OpenRouter. Inside ADR-0011 the Status line names two amendments and omits the third (2026-10-09).
- scenario: A reader of the index takes 0011 as unamended and acts on the OpenRouter design; a reader of the Status line misses the pause.
- evidence: "| 0011 | Agentic build workflow: Opus 5.5 orchestrator, cheap OpenRouter workers, review gates | Accepted |" vs "**Status:** Accepted, amended 2026-10-05 (workers) and 2026-10-06 (orchestrator reverted; see Amendments)" and "- **2026-10-09 (owner), delegation:** OpenRouter is paused."
- fix: Give 0011 the same "Accepted (amended 2026-10-05, 2026-10-06, 2026-10-09)" treatment as 0008 in the index and in its own Status line.

### docs-internal-06: ADR-0003 still decides YoutubeDLSharp and a TagLib# fallback; neither is used
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/adr/0003-stack-dotnet-react.md:9
- file: docs/architecture/STACK.md:25
- file: docs/architecture/STACK.md:23
- summary: (S4, S1) STACK.md records that YoutubeDLSharp "was evaluated and dropped (2026-10-05)" in favour of a direct subprocess, but ADR-0003 has no amendment and still lists it as the decision. Both documents name "TagLib# as fallback" for tagging; `Directory.Packages.props` has `z440.atl.core` only and `grep -r TagLibSharp src` finds nothing.
- scenario: A contributor reading the ADR adds a `YoutubeDLSharp` package reference or looks for the TagLib# fallback path; neither exists in the code.
- evidence: ADR-0003: "ATL for tags (TagLib# fallback), `fpcalc` + AcoustID HTTP, YoutubeDLSharp for yt-dlp with Deno bundled" / STACK.md: "YoutubeDLSharp (BSD-3) was evaluated and dropped (2026-10-05)" / STACK.md: "TagLib# as fallback"
- fix: Add an amendment to ADR-0003 (direct `IProcessRunner` subprocess for yt-dlp; ATL only, no TagLib# fallback) and drop the fallback from STACK.md §8.3.

### docs-internal-07: STACK.md says self-contained trimmed publish; the image is framework-dependent
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/STACK.md:31
- file: docs/architecture/DEPLOYMENT.md:27
- file: docker/Dockerfile:56
- summary: (S4) STACK.md's Packaging row contradicts DEPLOYMENT.md and the Dockerfile, which publishes with `--no-self-contained` onto `mcr.microsoft.com/dotnet/aspnet:10.0-noble`.
- scenario: A contributor sizing the image or debugging trimming warnings follows STACK.md and looks for `PublishTrimmed`/self-contained settings that are not there.
- evidence: STACK.md: "Multi-stage build; self-contained trimmed publish on `mcr.microsoft.com/dotnet/aspnet:10.0` (or Alpine)" / DEPLOYMENT.md: "built framework-dependent on `mcr.microsoft.com/dotnet/aspnet:10.0-noble`" / Dockerfile: "--no-self-contained"
- fix: Rewrite the STACK.md row to match DEPLOYMENT.md: a framework-dependent publish on `aspnet:10.0-noble`, multi-arch.

### docs-internal-08: DEPLOYMENT.md's image-contents table omits yt-dlp
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/DEPLOYMENT.md:29
- file: docs/architecture/DEPLOYMENT.md:36
- summary: (S4) The "Image contents (Phase 0)" table lists Wondarr, s6-overlay, slskd, ffmpeg, fpcalc and Deno, but the Dockerfile also pins and verifies yt-dlp 2026.08.19 (`docker/Dockerfile:119-125`) and NOTICE.md lists it as a bundled program. The pinned versions that are listed (s6-overlay 3.2.3.2, slskd 0.26.0, ffmpeg 9.0.2 with digest, fpcalc 1.6.1, Deno 2.9.7) match the Dockerfile.
- scenario: A maintainer bumping bundled tools from this table forgets yt-dlp, the tool most likely to need a bump.
- evidence: "| Deno | 2.9.7 | `/usr/local/bin/deno` |" is the last row; NOTICE.md: "| yt-dlp | 2026.08.19 | Unlicense | Unmodified binary (the official standalone executable) ... |"
- fix: Add a yt-dlp row (2026.08.19, `/usr/local/bin/yt-dlp`, Unlicense) and say that NOTICE.md and the Dockerfile are the sources of truth for the pins.

### docs-internal-09: Plex playlists are not written through `/playlists/upload`
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/STACK.md:28
- file: docs/architecture/LIBRARY_OUTPUT.md:67
- file: docs/architecture/ARCHITECTURE.md:203
- summary: (S4) STACK.md and LIBRARY_OUTPUT.md §7.4 say playlists go through Plex's `/playlists/upload` (server-side `.m3u8` path); ARCHITECTURE.md §5.6 describes the built mechanism, `POST /playlists?type=audio&smart=0&uri=...` with in-place reconciliation by rating key. `grep -r "playlists/upload" src` finds nothing.
- scenario: Someone debugging a playlist sync against a Plex proxy log looks for an upload call that is never made.
- evidence: STACK.md: "playlists via `/playlists/upload`" / LIBRARY_OUTPUT.md: "Optional playlist sync per import list via `/playlists/upload` (server-side `.m3u8` path) or rating keys" / ARCHITECTURE.md: "made once (`POST /playlists?type=audio&smart=0&uri=server://{machine}/com.plexapp.plugins.library/library/metadata/{keys}`) and then reconciled in place"
- fix: Make STACK.md and §7.4 point at §5.6's description (rating keys, create once, reconcile in place) and drop `/playlists/upload`.

### docs-internal-10: ARCHITECTURE §5.4 lists three tables that do not exist and omits four that do
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:109
- file: docs/architecture/ARCHITECTURE.md:110
- file: docs/architecture/ARCHITECTURE.md:128
- summary: (S1) The data model names `source_profile`, `source_instance` and `slskd_state`; the EF model snapshot has none of them (sources are the `indexer` and `download_client` rows plus options; slskd state is the in-memory `SlskdStatus`). The snapshot's `command`, `indexer`, `soulseek_user` and `notification_digest_entry` tables are not in the table.
- scenario: A contributor writing a migration or a query for "source instances" or "slskd state" finds no such entity; the real tables are `album_context, artist, blocklist, candidate, command, compact_move, custom_filter, download_client, history, import_list, import_list_item, indexer, job, library, match_candidate, metadata_cache, notification, notification_digest_entry, quality, quality_profile, queue_item, reference_file, reference_library, search_run, setting, song, song_artist, song_file, soulseek_user`.
- evidence: "| `source_profile` | `name`, `order` (json: source instance ids), `rules` (json: per-source overrides) |" / "| `source_instance` | `type` (slskd/youtube/torznab/newznab), `name`, `settings` (json), `enabled`, `priority`, `download_client_id?` |" / "| `slskd_state` | `version`, `logged_in`, `sharing`, `shared_dirs` (json), `restart_required`, `last_checked_at` |"
- fix: Regenerate the table list from `WondarrDbContextModelSnapshot.cs` and describe sources as `indexer` and `download_client` rows plus the `soulseek`/`youtube` option sections.

### docs-internal-11: ARCHITECTURE §5.6 sketch names endpoints and commands that are not in the API
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:179
- file: docs/architecture/ARCHITECTURE.md:186
- file: docs/architecture/ARCHITECTURE.md:187
- file: docs/architecture/ARCHITECTURE.md:188
- file: docs/architecture/ARCHITECTURE.md:196
- file: docs/architecture/ARCHITECTURE.md:198
- summary: (S1) Compared with `docs/api/openapi.json` (which matches every controller route): `GET /api/v1/artist/{id}/toptracks`, `/api/v1/sourceprofile`, `/api/v1/source` and `POST /api/v1/library/{id}/scan` do not exist (the real rows are `/api/v1/indexer` and `/api/v1/downloadclient`; adoption is `POST /api/v1/referencelibrary/{id}/scan`); the events channel is a SignalR hub at `/signalr/events` (`Program.cs:113`), not `WS/SSE /api/v1/events`; the command names `CutoffUnmetSearch`, `RescanLibrary` and `RefreshSong` are not registered (the real set is `Heartbeat, CheckHealth, CheckForUpdates, Backup, AddAlbum, BulkAddSongs, RescanSoulseekShares, MoveSongs, RefileSong, SongSearch, ConvertFiles, ApplyReplayGain, CompactLibrary, ImportListSync, ReferenceAdopt, ReferenceLibraryScan, MissingSearch, UpgradeSearch, Housekeeping`). Everything else in the sketch resolves.
- scenario: A dashboard or script author posts `{"name":"CutoffUnmetSearch"}` to `/api/v1/command` and gets an unknown-command error; a client subscribes to `/api/v1/events` and gets a 404.
- evidence: "GET    /api/v1/artist  …  GET /api/v1/artist/{id}/toptracks" / "POST   /api/v1/command  {name: SongSearch|MissingSearch|UpgradeSearch|CutoffUnmetSearch|ImportListSync|RescanLibrary|RefreshSong…}" / "GET/POST/PUT/DELETE /api/v1/source | /downloadclient | /importlist | /notification | /library" / "WS/SSE /api/v1/events  (queue progress, imports, health)"
- fix: Replace the sketch with the real paths (or a pointer to `/docs` and `docs/api/openapi.json`), the real command names, and the SignalR hub path.

### docs-internal-12: ARCHITECTURE §5.5 lists a weekly "Metadata refresh" job that does not exist
- severity: medium
- confidence: medium
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:143
- file: docs/architecture/ARCHITECTURE.md:139
- file: docs/architecture/ARCHITECTURE.md:203
- summary: (S1, S4) `ScheduledTaskCatalog.cs` registers Heartbeat, CheckHealth, MissingSearch, UpgradeSearch, ReferenceLibraryScan, Housekeeping, ImportListSync (hourly), Backup and CheckForUpdates; there is no metadata-refresh command or task. The same table says import-list sync runs "per list (default 12 h)" while §5.6 and the catalog say `ImportListSync` runs hourly and each list carries its own `sync_interval_hours`.
- scenario: A user told "cover art is re-pulled weekly" waits for a job that never runs; a contributor changing the list-sync cadence edits the wrong number.
- evidence: "| Metadata refresh | weekly | Re-pull cover/ISRC/durations for songs missing them |" / "| Import list sync | per list (default 12 h) | Adds new items; policy decides about removals |" vs "`ImportListSync` runs hourly and syncs every enabled list whose interval has passed"
- fix: Drop the Metadata refresh row (or mark it as not built) and make the Import list sync row say "hourly runner; each list has `sync_interval_hours`".

### docs-internal-13: QUALITY_DEFINITIONS output-policy table is the version-1 shape and names a quality that does not exist
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/QUALITY_DEFINITIONS.md:29
- file: docs/architecture/QUALITY_DEFINITIONS.md:35
- file: docs/architecture/QUALITY_DEFINITIONS.md:19
- summary: (S4, S1) The `codec` row lists `keepOpus`, `aac`, `mp3`; `OutputPolicy.cs` writes `keep` (with `keepOpus` accepted only as the old spelling) and also has `opus`, `flac`, `alac` and `opusContainer`, as LIBRARY_OUTPUT.md §7.7 says. The detection paragraph maps YouTube itag 140 to `AAC-128`, a name absent from `SeedData.cs` (the AAC tiers are AAC-192, AAC-256, AAC-320, AAC-VBR); `YouTubeCandidateMapper` ranks every YouTube grab as `Opus160QualityId = 28`.
- scenario: A contributor validating a policy against this table rejects `"codec": "opus"` as unknown, or looks up `AAC-128` in the seed and finds nothing.
- evidence: "| `codec` | `keepOpus`, `aac`, `mp3` — a lossless target (`flac`, `alac`, `wav`, `ape`, `wv`) is refused" / "YouTube format ids (251 → OPUS-160, 140 → AAC-128)" / `OutputPolicy.cs:94` "OutputCodec.Keep => \"keep\"" / LIBRARY_OUTPUT.md:113 "`keep` (no conversion; `keepOpus` is accepted as the old spelling)"
- fix: Replace the table with a pointer to §7.7 (or copy §7.7's key list) and drop the itag-140 mapping, or state that every YouTube grab is ranked OPUS-160.

### docs-internal-14: CONFIG_EXAMPLES.md documents config.yml keys and sections that do not exist
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/CONFIG_EXAMPLES.md:35
- file: docs/architecture/CONFIG_EXAMPLES.md:40
- file: docs/architecture/CONFIG_EXAMPLES.md:42
- file: docs/architecture/CONFIG_EXAMPLES.md:58
- file: docs/architecture/CONFIG_EXAMPLES.md:70
- file: docs/architecture/CONFIG_EXAMPLES.md:90
- file: docs/architecture/CONFIG_EXAMPLES.md:97
- summary: (S1, S3) The "config.yml (subset)" is largely a design sketch. The path is given as `/config/<app>/config.yml` (real: `/config/config.yml`, DEPLOYMENT §9.5). `metadata.musicbrainz.user_agent`, `rate_limit_rps`, `acoustid.min_score`, `lyrics.provider`, `write_lrc`, `embed_unsynced` have no counterpart (`MetadataOptions`: base URLs, `ContactUrl`, `MusicBrainzMirrorIntervalMs`; `AcoustIdOptions`: `ClientKey`, `BaseUrl`, `AcceptScore`, `ReviewScore`, `Strict`, `RequestsPerSecond`; `LyricsOptions`: `Enabled`, `BaseUrl`, `RequestIntervalMs`, `TimeoutSeconds`). The `sources:` list with `prefer_topic_channel`, `cookies_file`, `po_token_provider`, `output`, `max_concurrent_downloads`, `preferred_formats`, `ignore_users`, `partial_download` does not exist (`YouTubeOptions`: `Enabled`, `CookiesPath`, `PoTokenBaseUrl`, `AllowVideos`, `SearchLimit`, `BaseUrl`, `DownloadsDir`; indexers and download clients are API rows). `libraries:`, `reference_libraries:` and `quality_profiles:` are database rows edited through the API, not config. `sidecars.artist_jpg` has no reader. The comment "(§7.4)" for the output policy points at the wrong section (it is §7.7).
- scenario: A user hand-edits `config.yml` with `sources:` or `lyrics: { write_lrc: false }` and nothing changes; a contributor searches for `rate_limit_rps` to tune the MusicBrainz spacing and finds nothing.
- evidence: "`/config/<app>/config.yml` (subset):" / "  musicbrainz: { user_agent: \"<app>/0.1 (you@example.com)\", rate_limit_rps: 1 }" / "sources:\n  - type: slskd" / "    # output policy (§7.4): fully configurable; also overridable per library and per grab" / "    sidecars: { cover_jpg: true, lrc: true, artist_jpg: false }"
- fix: Keep only the sections the options classes bind (`server`, `metadata`, `acoustid`, `lyrics`, `soulseek`, `youtube`, `import`, `search`, `backup`, `update`, `feedback`, `plex`, `lastfm`, `log`) with their real keys, and say that libraries, profiles, indexers and clients live in the database.

### docs-internal-15: LIBRARY_OUTPUT describes artist.jpg, per-grab output overrides and an ID3v2.3 option that are not built
- severity: medium
- confidence: medium
- category: stale-doc
- file: docs/architecture/LIBRARY_OUTPUT.md:63
- file: docs/architecture/LIBRARY_OUTPUT.md:65
- file: docs/architecture/LIBRARY_OUTPUT.md:91
- file: docs/adr/0008-quality-model.md:9
- summary: (S1) §7.4 says the preset "optionally" writes `artist.jpg` from fanart.tv/TheAudioDB, offers a "per-song override in the interactive grab" and a "per-source override (... SoundCloud → MP3-320)", and §7.5 promises "a v2.3 option for old car stereos". `grep -ri "artist.jpg\|fanart\|audiodb" src` finds nothing; the grab request is `GrabRequestResource(long CandidateId)` only (`src/Wondarr.Api/Release/ReleaseResource.cs:97`); the output policy has per-library rules per source class, not per instance; `grep -ri "id3v2.3\|v2_3" src` finds nothing. ADR-0008 also promises "per-grab overrides".
- scenario: A user looks in the interactive search for a per-grab codec choice or in Settings for ID3v2.3; neither exists. SoundCloud is not a source at all.
- evidence: "optionally `artist.jpg` in the artist folder from fanart.tv/TheAudioDB" / "per-source override (e.g. YouTube → AAC-256, SoundCloud → MP3-320) and per-song override in the interactive grab" / "ID3v2.4 UTF-8 by default with a v2.3 option for old car stereos."
- fix: Rewrite §7.4 around what §7.7 describes (three rules per library, no per-grab override, no artist image), and either remove the v2.3 sentence or label these as not built.

### docs-internal-16: DISTRIBUTION.md contradicts itself on Docker Hub and GitHub metadata
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/build/DISTRIBUTION.md:5
- file: docs/build/DISTRIBUTION.md:12
- file: docs/build/DISTRIBUTION.md:30
- file: docs/build/DISTRIBUTION.md:11
- file: docs/build/DISTRIBUTION.md:35
- file: docs/build/DISTRIBUTION.md:124
- summary: (S4) The Status table (dated 2026-10-25) says Docker Hub is live and GitHub metadata is done; the introduction, the gates table (dated 2026-10-24) and channel sections 0 and 2 still say GHCR-only, "All empty", and list the Docker ID, token, workflow step and Hub README as missing. `.github/workflows/mirror-dockerhub.yml` and `docker/DOCKERHUB.md` exist and `release.yml:207` calls the mirror.
- scenario: A maintainer reading section 2 sets out to create the Docker ID and workflow step that already exist.
- evidence: "Today Wondarr ships one way: `ghcr.io/wulfftech/wondarr`" / "| Docker Hub | Live: [`wulfftech/wondarr`](https://hub.docker.com/r/wulfftech/wondarr). `mirror-dockerhub.yml` copies every promoted tag. |" / "| Image | GHCR only, multi-arch, smoke-tested on both architectures before tags move |" / "| GitHub description, homepage, topics | Done |" vs "| GitHub description, topics, homepage | All empty |" / "- **Missing:** the Docker ID, a token stored as the `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` Actions secrets, the workflow step, a Hub README, and doc lines naming both registries."
- fix: Bring the introduction, the gates table and sections 0 and 2 up to the Status table's date, or move the superseded "Missing" lists into a "Done on 2026-10-25" note.

### docs-internal-17: tests/fixtures/README.md draws a golden-suite layout that does not exist
- severity: medium
- confidence: high
- category: stale-doc
- file: tests/fixtures/README.md:9
- file: tests/fixtures/README.md:10
- file: tests/fixtures/README.md:21
- summary: (S1) The layout shows golden suites as directories (`naming/`, `quality/`, `scoring/`, `album-policy/`, `mapping/`) holding `*.json`; the real golden fixtures are flat files at the root (`album-policy.json`, `container-matches.json`, `decisions.json`, `filenames.json`, `naming.json`, `soulseek-quality.json`, `version-flags.json`) plus `logging/` and `importlists/`, referenced one by one from the test `.csproj` files. The "sibling `.meta.json`" convention holds only for `ytmusic/` (14 files); `musicbrainz/`, `deezer/`, `slskd/`, `acoustid/`, `torznab/`, `sabnzbd/` and the rest carry none.
- scenario: A worker told to "add a case without touching code" creates `tests/fixtures/naming/cases.json`; no test reads it, and the `.csproj` does not copy it to the output directory.
- evidence: "├── <golden suite>/          # e.g. naming/, quality/, scoring/, album-policy/, mapping/" / "- **Contract tests** — the recorded response verbatim, plus a sibling `.meta.json` recording the request URL, status, and the date it was captured."
- fix: Draw the real layout (flat golden files at the root, one directory per service, `media/`), name the `.csproj` entry that copies each file, and say which directories carry `.meta.json` files (or add them).

### docs-internal-18: CODING_STANDARDS names tooling the build does not use and a release step no workflow runs
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/build/CODING_STANDARDS.md:16
- file: docs/build/CODING_STANDARDS.md:21
- file: docs/build/CODING_STANDARDS.md:23
- summary: (S1) `Directory.Build.props` turns on the SDK analyzers with `AnalysisLevel=latest-recommended`; neither `StyleCop.Analyzers` nor a `Microsoft.CodeAnalysis.NetAnalyzers` package is referenced in `Directory.Packages.props` or any `.csproj`, so the sentence's first two alternatives are not what is enforced. The "`IFileSystem`-style abstraction" exists under another name (`IDiskOperations`, plus `IFilePlacer`, `IFileConverter`); `grep -r IFileSystem src` finds nothing. "OpenAPI generated and committed to `docs/api/openapi.json` on each release" is not backed by any workflow or script (`grep -rn openapi .github scripts` finds nothing); the file was last regenerated on 2026-10-22 (commit 026e5be) while 0.1.15 (2026-10-27) and 0.1.16 (2026-11-03) shipped. It is nevertheless current: every controller route is in it. `Microsoft.Extensions.Http.Resilience` is in use (Core, Torznab), so that claim holds.
- scenario: A contributor adds StyleCop suppressions or a `stylecop.json` that nothing reads; a release manager skips the regeneration because "CI does it".
- evidence: "analyzers on (`Microsoft.CodeAnalysis.NetAnalyzers`, `StyleCop.Analyzers` or the .NET SDK analyzers with `AnalysisLevel=latest-recommended`)" / "file system via an `IFileSystem`-style abstraction so tests do not touch disk" / "OpenAPI generated and committed to `docs/api/openapi.json` on each release" / Directory.Build.props: "<AnalysisLevel>latest-recommended</AnalysisLevel>"
- fix: State what is on: ".NET SDK analyzers at `AnalysisLevel=latest-recommended`, warnings as errors"; name `IDiskOperations`; say when and how `openapi.json` is regenerated (the command, and that `npm run gen:api` consumes it), or add a CI check that it matches the build.

### docs-internal-19: Architecture docs and ADRs reference numbered documents that are not in the repository
- severity: medium
- confidence: high
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:3
- file: docs/architecture/ARCHITECTURE.md:50
- file: docs/architecture/ARCHITECTURE.md:201
- file: docs/architecture/ARCHITECTURE.md:205
- file: docs/architecture/MATCHING_ENGINE.md:48
- file: docs/architecture/MATCHING_ENGINE.md:64
- file: docs/architecture/MATCHING_ENGINE.md:67
- file: docs/architecture/MATCHING_ENGINE.md:87
- file: docs/architecture/LIBRARY_OUTPUT.md:12
- file: docs/architecture/LIBRARY_OUTPUT.md:103
- file: docs/architecture/LIBRARY_OUTPUT.md:118
- file: docs/architecture/DEPLOYMENT.md:40
- file: docs/build/CODING_STANDARDS.md:3
- file: docs/build/CODING_STANDARDS.md:41
- file: tests/fixtures/acoustid/README.md:14
- file: scripts/safe-merge.sh:13
- file: scripts/tests/test-check-notice.sh:2
- file: scripts/tests/test-safe-merge-parsing.sh:2
- summary: (S2, R12) The five architecture documents keep the section numbers of the original plan (ARCHITECTURE starts at "### 5.1", MATCHING_ENGINE at "6.1", LIBRARY_OUTPUT at "7.1", STACK at "8.1", DEPLOYMENT at "9.1"), and cross-reference each other by bare number ("section 6", "§7.3", "section 7.4", "§9.5") without a file name. They also cite decision and review ids and phase/task files that `.gitignore` excludes (`docs/PLAN.md`, `docs/DECISIONS.md`, `docs/reviews/`, `docs/build/PHASES.md`, `docs/build/tasks/`): ARCHITECTURE "code review 2026-10-10, R-007, R-019 to R-021, R-074, R-076", "(#8)", "(#9)", "(#10)", "Phase 5/6/7"; MATCHING_ENGINE "R-070", "R-062" (twice), "R-067", "M-44", "P7-03a/b", "P7-04", "P7-07", "owner question 20/21/22", "Phase 5", "Phase 8"; LIBRARY_OUTPUT "R-046", "DECISIONS build session 14 #32/#33", "(#5)" (twice), "Phase 3/4/6/8"; DEPLOYMENT "Appendix A", "Phase 0/2/5"; QUALITY_DEFINITIONS "Phase 6"; REPO_LAYOUT "Phase 0"; ADR-0009 "Phase 7"; ADR-0012 "Phase 3", "Phase 6"; acoustid README "M-35" (twice); the script headers "R-044", "R-093", "R-094", "M-50". CODING_STANDARDS is written for "workers" with "task files", a "done-report", "the orchestrator" and "the reviewer agent" (the git-ignored workflow that ADR-0011's last amendment paused). The ADRs' "§8.4–8.5"-style references do resolve, because STACK.md keeps those headings.
- scenario: A contributor reads "(owner question 20)" or "R-062" and has no document to open; "section 6" in ARCHITECTURE.md gives no file name; "Phase 7" has no definition anywhere in the clone.
- evidence: "### 5.1 Components" (first heading of ARCHITECTURE.md) / "Reject candidates that violate hard rules; score the rest; pick the best (section 6)." / "**What a sync trusts, and what it does for items that return (code review 2026-10-10, R-007, R-019 to R-021, R-074, R-076).**" / "An automatic grab stops at the cutoff (owner question 20, as in Lidarr)" / "The reference `docker-compose.yml` is a single service ... (Appendix A)" / "Acceptance criteria in the task file are met and demonstrated (test or command output pasted in the done-report)."
- fix: Either publish the decision log the ids point at, or replace each id with the decision in one clause and each "§n.n" with the file name and heading ("MATCHING_ENGINE.md, Hard rejections"); renumber the headings from 1 per file, or add a one-line note at the top of each document explaining where the numbering comes from; rewrite CODING_STANDARDS's audience as "contributors" and its definition of done without task files and done-reports.

### docs-internal-20: smoke-test.sh header omits the Phase 5 gate it runs and repeats a parenthetical
- severity: medium
- confidence: high
- category: stale-doc
- file: scripts/smoke-test.sh:2
- file: scripts/smoke-test.sh:35
- file: scripts/smoke-test.sh:374
- summary: (S1, R8, R11) The header lists Phases 0, 1, 2, 3, 6, 7 and 4 (in that order) with their `SMOKE_PHASE*` switches, but the script also runs `phase5-gate.py upgrade`, `backup-take` and `backup-verify` behind `SMOKE_PHASE5` (lines 374-416), which the header never mentions, although it refers to "Phases 2-5". Lines 35-36 stack two parentheticals from an unfinished edit.
- scenario: A contributor sets the switches the header lists, wonders why a Phase 5 container starts, and does not know `SMOKE_PHASE5=off` exists.
- evidence: "#     (FakeSlskd --fake-ytdlp)\n#     (a stand-in yt-dlp producing real encoded Opus); songs missing on Soulseek are filled from" / "if [ \"${SMOKE_PHASE5:-on}\" = off ]; then" (not in the header)
- fix: Add "Phase 5 (scripts/phase5-gate.py; SMOKE_PHASE5=on (default) | off): a song below a FLAC cutoff is upgraded when a FLAC appears, a backup restores into a fresh container, and the Phase 2 gate passes in external-slskd mode", order the phases 0 to 7, and merge the two parentheticals into "the fake yt-dlp (`FakeSlskd --fake-ytdlp`, a stand-in producing real encoded Opus)".

### docs-internal-21: FakeSlskd README does not describe the InnerTube, Plex and yt-dlp stubs the gates depend on
- severity: medium
- confidence: high
- category: stale-doc
- file: tools/FakeSlskd/README.md:1
- file: tools/FakeSlskd/README.md:41
- file: tools/FakeSlskd/README.md:73
- summary: (S1) The tool also hosts `InnertubeStubApp.cs` (`FAKE_INNERTUBE_PORT`, default 5032; `FAKE_INNERTUBE_FIXTURES`, `FAKE_YT_SCENARIO`), `PlexStubApp.cs` (`FAKE_PLEX_PORT`, default 5033; `FAKE_PLEX_SECTIONS`) and the `--fake-ytdlp` mode (`FakeYtDlp.cs`), which the Phase 4 and Phase 6 gates use (`tests/gate/phase4-scenario.schema.md:47`, `scripts/phase6-gate.py`); the README's endpoint table and environment variables cover only the slskd API, the AcoustID stub (5031) and the container stub (5034).
- scenario: A contributor extending the Phase 4 gate reads the README, finds no InnerTube stub, and looks for the fixture mapping in the Wondarr source instead.
- evidence: "It is a test tool: never part of the image, never shipped." then "## What it answers" lists only `/api/v0/*` and `/fake/*` endpoints; "## AcoustID stub" and "## Container stub (Phase 7)" are the only extra sections.
- fix: Add "InnerTube stub" (port, fixtures, `FAKE_YT_SCENARIO`), "Plex stub" (port, `FAKE_PLEX_SECTIONS`, `/fake/plex`) and "Fake yt-dlp (`--fake-ytdlp`)" sections, and list all `FAKE_*` variables with their defaults in one table.


## Low findings

### api-04: `UseForwardedHeaders()` is a no-op, but the comment and the docs say proxy headers can be processed
- severity: low
- confidence: high
- category: stale-doc
- file: src/Wondarr.Api/Program.cs:93
- file: src/Wondarr.Api/Program.cs:88
- file: website/src/content/docs/reference/configuration.md:45
- summary: No `ForwardedHeadersOptions` is configured anywhere in `src/` (grep), so `ForwardedHeaders` stays `None` and the middleware rewrites nothing; Wondarr can never trust a proxy's `X-Forwarded-For`. That is the right choice for `LocalCallerRules` (which fail closed on any proxy header), but the pipeline comment lists "forwarded headers" as a real stage and the configuration page says the login window is shared "unless the proxy's forwarded headers are processed", pointing at a switch that does not exist.
- scenario: An owner behind nginx reads configuration.md, looks for how to process forwarded headers so that `LoginRateLimiting` partitions per visitor, and finds nothing; every visitor behind the proxy keeps sharing one `PartitionKey(context.GetRemoteIp())` window (10 attempts a minute), so one remote attacker holds the owner out of `/login` a minute at a time (and, with api-01, indefinitely).
- evidence: `app.UseForwardedHeaders();` with no `Configure<ForwardedHeadersOptions>` or `KnownProxies` anywhere; comment "The *arr pipeline order (Lidarr's Startup.Configure): forwarded headers, request logging, ..."; doc line 45: "Behind a reverse proxy every visitor shares the proxy's address unless the proxy's forwarded headers are processed".
- fix: Either remove the call and the comment and state in the doc that forwarded headers are never trusted, or add a `server.trusted_proxies` option that fills `KnownProxies`/`KnownNetworks` and use the forwarded address only for the rate-limit partition (never for `LocalCallerRules`).

### api-05: slskd webhook's "came through a proxy" check looks at one header, not the shared proxy-header list
- severity: low
- confidence: high
- category: security
- file: src/Wondarr.Api/Slskd/SlskdWebhookController.cs:198
- summary: The loopback gate treats a request as proxied only when `X-Forwarded-For` is present, while `LocalCallerRules.HasProxyHeader` (the rule the rest of the app uses for the same question) recognises twelve headers. A reverse proxy on the Wondarr host that sets `X-Real-IP` or `Forwarded` instead passes the "straight from the bundled slskd" check for internet traffic, leaving the webhook token as the only barrier.
- scenario: nginx on the Wondarr host proxies `https://wondarr.example.com` → `http://127.0.0.1:1077` with only `proxy_set_header X-Real-IP $remote_addr`. `POST https://wondarr.example.com/api/v1/slskd/webhook` from the internet arrives with `RemoteIpAddress = 127.0.0.1` and no `X-Forwarded-For`, so the 403 branch is skipped and the token is compared: a wrong token gets 401 instead of 403 (the endpoint becomes an existence oracle), a token that ever leaks from `slskd.yml` lets a remote caller publish `SlskdDownloadCompletedEvent`s. The intended behaviour per the comment is a 403 for anything forwarded.
- evidence: `if (remote is null || !IPAddress.IsLoopback(remote) || Request.Headers.ContainsKey("X-Forwarded-For"))` under the comment "A forwarded request came through a proxy, not straight from the bundled slskd." (SlskdWebhookController.cs:197-198); compare `LocalCallerRules.ProxyHeaders` (LocalCallerRules.cs:25-39).
- fix: Use `LocalCallerRules.HasProxyHeader(Request)` here.

### api-06: OpenAPI snapshot test comment says /api/v1/system/status does not exist yet
- severity: low
- confidence: high
- category: stale-doc
- file: tests/Wondarr.Api.Tests/OpenApiSnapshotTests.cs:34
- summary: The comment says the status endpoint "is owned by P0-04b and is not on this branch yet. Add the assertion back", but `SystemController` (src/Wondarr.Api/SystemInfo/SystemController.cs:41) serves it and `docs/api/openapi.json` contains the path; the dedicated assertion was never restored. The snapshot itself is strict (full-text `Should().Be(committed)`), so only the comment is wrong.
- scenario: n/a: a reader of the test concludes the endpoint is missing or the branch is behind.
- evidence: `// /api/v1/system/status is owned by P0-04b and is not on this branch yet. Add the assertion back — and regenerate the snapshot — once P0-04b has merged.`; `grep -c '"/api/v1/system/status"' docs/api/openapi.json` → 1.
- fix: Delete the comment or restore `document.Should().Contain("\"/api/v1/system/status\"")`.

### tests-01: Nine ffmpeg-gated tests fail on a host whose ffmpeg is not the image's 9.0.2
- severity: low
- confidence: high
- category: test-gap
- file: tests/Wondarr.Core.Tests/Media/DamagedMp3.cs:18
- file: tests/Wondarr.Core.Tests/Media/ReferenceDecodeToleranceLiveTests.cs:47
- file: tests/Wondarr.Core.Tests/References/ReferenceScanToleranceLiveTests.cs:54
- file: tests/Wondarr.Core.Tests/References/ReferenceAdopterTests.cs:217
- file: docker/Dockerfile:75
- summary: `FfmpegFact`/`FfmpegTheory` run whenever any `ffmpeg` and `ffprobe` are on PATH, but the decode-tolerance checks (error-line counting, "Header missing", the 98 % threshold) are calibrated to the static ffmpeg 9.0.2 the image pins. On Ubuntu's ffmpeg 6.1.1 the damaged MP3s decode without the expected errors, so eight tests fail with "Expected Decodable to be False" and one with a NullReferenceException from a substitute fingerprinter that the test expected never to be reached. CI never sees this because its runner has no ffmpeg (the tests skip there), so the tests only ever pass inside the image.
- scenario: `dotnet test` on a developer box with a distro ffmpeg (this session: Ubuntu, ffmpeg 6.1.1, 5 222 passed, 9 failed, all in this group). A contributor concludes the decode tolerance is broken, or learns to ignore red tests.
- evidence: Test output: `Failed ReferenceDecodeToleranceLiveTests.A_file_with_more_than_twenty_error_lines_is_unreadable_for_a_reference_scan ... Expected reference.Decodable to be False, but found True.` and `Failed ...The_download_verifier_still_rejects_a_file_with_one_bad_frame ... System.NullReferenceException ... DownloadVerifier.cs:line 193` (the test's `Substitute.For<IFingerprinter>()` returns null once the probe unexpectedly says the file decodes). `DamagedMp3.ToolsAvailable` checks only that the tools are on PATH.
- fix: Have `FfmpegFactAttribute` skip unless `ffmpeg -version` reports the major version the image pins (or an explicit `WONDARR_FFMPEG_TESTS=1`), and name the version in the skip reason; or make the fixture independent of ffmpeg's error wording.

### tests-02: TagWriterSafetyTests.Leaves_a_read_only_file_untouched fails when the suite runs as root
- severity: low
- confidence: high
- category: test-gap
- file: tests/Wondarr.Core.Tests/Tagging/TagWriterSafetyTests.cs:17
- summary: The test sets the read-only attribute and expects the write to fail, but root ignores file mode bits on Linux, so the write succeeds and the assertion fails. Containers and CI images commonly run as root.
- scenario: `dotnet test` inside a container as uid 0 (this session): `Expected result.Success to be False, but found True.` at line 26.
- evidence: `File.SetAttributes(path, FileAttributes.ReadOnly); ... result.Success.Should().BeFalse();` with no skip for uid 0; the sibling `PlatformFactAttributes.cs` already skips tests that need Unix modes on Windows but has no "not as root" variant.
- fix: Add a `NotRootFact` (skip when `Environment.UserName == "root"` or `geteuid() == 0`) and use it here, or assert the read-only path through a fake disk instead of the real file mode.

### style-01: Two "not X, but Y" framings in the internal docs (Style 1: no "not this, but this")
- severity: low
- confidence: high
- category: style
- file: docs/architecture/LIBRARY_OUTPUT.md:16
- file: docs/build/DISTRIBUTION.md:199
- summary: Style 1 bans the "not this, but this" framing. A scan of every Markdown file in the repository finds two sentences built on it; every other "not ... but" hit is literal ("a path there is not refused, but Health shows an error").
- scenario: Style only.
- evidence: LIBRARY_OUTPUT.md:16 "not as a hard requirement, but in practice yes for a library you want to behave"; DISTRIBUTION.md:199 "Not a package repository, but it's where self-hosters hear about new apps first".
- fix: Commit to the statement: "In practice a library you want to behave in Plexamp needs it." / "Self-hosters hear about new apps there first."

### style-02: The skill's code-comment rules and the repository's comment practice disagree everywhere
- severity: low
- confidence: high
- category: style
- file: .claude/skills/writing-style/SKILL.md:94
- file: docs/build/CODING_STANDARDS.md:14
- file: src/Wondarr.Core/Verification/DownloadVerifier.cs:150
- summary: The skill says code comments are lowercase, one line each, WHY only, with no comment blocks and no docstrings, and that this holds "whichever style is active". The repository documents every public C# member with `/// <summary>` XML blocks (6 090 of them in `src/`), writes `//` comments as sentences more often than not (1 852 sentence-case against 1 142 lowercase in `src/`), and the frontend has 1 612 JSDoc blocks. Nothing enforces the XML docs: `Directory.Build.props` does not set `GenerateDocumentationFile`, so CS1591 never fires, and `CODING_STANDARDS.md` does not mention comments at all.
- scenario: A session that applies the skill to code, as the skill tells it to, would rewrite thousands of comments; a session that follows the codebase ignores the skill. Either way the next reviewer cannot say which is the misstep.
- evidence: SKILL.md "Comment the WHY. Never the WHAT. ... Lowercase comments by default ... One line per comment. No comment blocks. No docstrings." versus, for example, DownloadVerifier.cs:150-160 "// A tool that ran out of time has said nothing about the file: a long FLAC on a NAS busy with\n// a Plex transcode times out and is perfectly good. Ask again later, never blocklist it." (a WHY comment, but sentence case and two lines) and the `/// <summary>` on every public type.
- fix: A decision for the owner, not a worker: either amend the skill's comment section for this repository (for example "XML doc summaries on public members and sentence-case WHY comments are the convention here; the one-line lowercase rule applies to inline comments only"), or add the skill's rules to `CODING_STANDARDS.md` and accept a large mechanical pass. Until decided, workers leave comment style as they find it.

### core-import-05: Removing a Completed queue item can race the import that already started on it
- severity: low
- confidence: medium
- category: concurrency
- file: src/Wondarr.Core/Importing/QueueActions.cs:110
- file: src/Wondarr.Core/Importing/QueueActions.cs:143
- file: src/Wondarr.Core/Importing/ImportService.cs:193
- file: src/Wondarr.Core/Importing/ImportService.cs:301
- summary: `RemoveAsync` only refuses items that are already `Importing`, and `ImportCoreAsync` writes `Importing` some six database reads after it loaded the item as `Completed`. `queue_item` has no concurrency token (no `IsConcurrencyToken`/`IsRowVersion` in Persistence), so both sides read `Completed`, and the remove deletes the download (`blocklist: true`) under the running import, or its `Cancelled` save is later overwritten by the import's `Imported` save.
- scenario: The poll calls `ImportAsync` for item 42 (Completed); the user clicks Remove with blocklist and retry off at the same moment. QueueActions loads 42 as Completed, cancels at the source, adds the blocklist row, `DeleteDownload` deletes the file, saves `Cancelled`. The import, past line 303, probes a missing file -> `RejectAsync` -> adds a second blocklist row, marks the item `Failed` and `TryNextAsync` grabs the next candidate although the user asked for no retry. If instead the file had already been moved into the library, the import's final `SaveChangesAsync` writes `Imported` over `Cancelled` while the user's blocklist row for that very file stays.
- evidence: `if (item.State == QueueItemState.Importing) { throw new QueueItemBusyException(item.Id); }` (121-126) guards only the state saved at ImportService.cs:301-303; the item is loaded at 193-196 and `LoadSongAsync`, profile, library, `File.Exists` and `HasUnfinishedCompactionMoveAsync` run in between.
- fix: Claim the item atomically: `ExecuteUpdate` `state = Importing WHERE id = @id AND state = Completed` (and bail out as NotReady when 0 rows changed) before loading the tracked entity, or add a row version to `QueueItem` so the loser's save fails.

### core-import-06: A download whose decode check always times out is deferred every 15 minutes for ever
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Importing/ImportService.cs:484
- file: src/Wondarr.Core/Importing/ImportService.cs:641
- file: src/Wondarr.Core/Importing/ImportService.cs:620
- file: src/Wondarr.Core/Media/MediaProbe.cs:161
- summary: A probe or fingerprint that times out is deferred with `NextCheckAt = now + 15 min` and no counter, while the AcoustID-key deferral is capped (`MaxKeyDeferrals`). `MediaProbe` decodes the whole file with `ffmpeg -xerror` under `media.timeout_seconds` (default 120 s, max 600 s), and the same file takes the same time on every retry, so an item whose file is simply too long for the configured timeout cycles Completed -> Importing -> Completed indefinitely with a download folder that is never released.
- scenario: A 90-minute live set grabbed for a song on a low-powered NAS: the decode needs > 120 s, `probed.TimedOut` is true at line 484, `DeferAsync` puts the item back; 15 minutes later the identical decode times out again. The queue shows "Could not check the file in time" permanently, the song never fails over to the next candidate, and the Housekeeping vacuum is skipped for ever because a `Completed` item exists (`HousekeepingCommandHandler.cs:398-409`).
- evidence: `if (probed.TimedOut) { return await DeferAsync(item, $"Could not check the file in time: ...", now, cancellationToken); }` with `item.NextCheckAt = now + DeferralDelay;` (1087-1103) and no attempt count, versus `_keyState.CountDeferral(item.Id) > AcoustIdKeyState.MaxKeyDeferrals` (620-622).
- fix: Count tool-timeout deferrals per item (as `AcoustIdKeyState` does for the key) and fail the item without blocklisting after a bound, telling the user to raise `media.timeout_seconds`.

### core-import-07: Rejecting a converted download with clean_leftover_folders off leaves an empty grab folder behind
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Importing/ImportService.cs:360
- file: src/Wondarr.Core/Importing/ImportService.cs:547
- file: src/Wondarr.Core/Importing/ImportService.cs:1276
- file: src/Wondarr.Core/Importing/ImportService.cs:1785
- summary: The `.wondarr-convert.<ext>` temp lives in the per-grab folder and is deleted only when the `using var convertTemp` scope ends, after `RejectAsync` has already run `DeleteDownload` -> `RemoveOwnFolder` -> `RemoveFolderWhenEmpty`. With `import.clean_leftover_folders: false` the folder still holds the temp at that moment, so it is kept; the temp is deleted a moment later and `wondarr/<guid>/` is left empty for good.
- scenario: Lossless rule converts a FLAC download to MP3 into `wondarr/<guid>/x.wondarr-convert.mp3`; verification then fails the fingerprint; `RejectAsync` deletes `x.flac`, finds the folder non-empty, keeps it; `ConvertTempFile.Dispose` deletes the temp. Every such rejection leaves one empty folder under the download root.
- evidence: `using var convertTemp = new ConvertTempFile();` (360) is disposed at method exit, while `DeleteDownload(item, item.DownloadPath);` (1276) and `RemoveFolderWhenEmpty(folder)` (1764/1785-1791) run inside `RejectAsync`.
- fix: Call `convertTemp.Delete()` before `DeleteDownload` in the reject and fail paths (or pass the temp path into `DeleteDownload`).

### core-import-08: CompactExecutor.CleanUpFolderAsync walks the folder recursively for a value it never uses
- severity: low
- confidence: high
- category: style
- file: src/Wondarr.Core/Compaction/CompactExecutor.cs:1061
- summary: `var files = _disk.EnumerateFiles(folder).ToList();` is dead: `SongFileRules.HoldsOnlyCovers` enumerates the same folder again on the next line and `files` is never read. `EnumerateFiles` is recursive (`DiskOperations.Walk`), so an album folder that is really an artist root is walked twice per compacted folder.
- scenario: A compaction whose old folders are the library root (a flat layout) enumerates the whole library twice for each of up to `MaxFoldersPerLibrary` folders, for nothing.
- evidence: `var files = _disk.EnumerateFiles(folder).ToList();` (1061) immediately followed by `if (!SongFileRules.HoldsOnlyCovers(_disk, folder, out var covers))` (1065), with no further use of `files`.
- fix: Delete the line.

### core-import-09: No test covers restart recovery of an upgrade that landed at the old file's path
- severity: low
- confidence: high
- category: test-gap
- file: tests/Wondarr.Core.Tests/Importing/QueueTrackerTests.cs:445
- file: tests/Wondarr.Core.Tests/Importing/QueueTrackerTests.cs:469
- summary: The recovery tests seed a song with no file, a reference file at another path, or a file owned by another song. None seeds a library `song_file` whose `path` equals the landed candidate, which is the shape of every in-place upgrade and the case core-import-01 shows is mishandled.
- scenario: Add `AddSongFileAsync(seed.SongId, landed, SourceTypes.Soulseek, oldQualityId)` before `RecoverAsync` with the landed file's mtime after the import began; the item must end `Imported` and the row must carry the new codec/quality.
- evidence: `Records_the_file_an_interrupted_import_had_already_placed` (469) seeds no song file; `Records_the_placed_replacement_of_a_reference_file_...` seeds a reference file at a temp path; `Does_not_record_a_file_another_song_already_owns` points the other song at it.
- fix: Add the in-place upgrade case alongside the fix for core-import-01.

### core-import-10: No test covers a cancellation between the Opus-to-.ogg rename and the first save
- severity: low
- confidence: high
- category: test-gap
- file: tests/Wondarr.Core.Tests/Importing/ImportServiceTranscodeTests.cs:104
- file: tests/Wondarr.Core.Tests/Importing/ImportServiceFileRecordTests.cs:246
- summary: `Keep_with_the_ogg_container_renames_the_remux` only checks the happy path, and `The_new_download_path_is_saved_even_when_the_import_then_fails` covers the transcode branch, not the rename branch. The path core-import-02 describes (cancel during verification after the rename) has no test, so the missing save is not caught.
- scenario: Configure `opusContainer: ogg`, make the verifier throw `OperationCanceledException`, then assert the stored `DownloadPath` ends in `.ogg`.
- evidence: `grep -n "ogg\|rename\|cancel" ImportServiceTests.cs ImportServiceTranscodeTests.cs` matches only the happy-path test at line 104.
- fix: Add the test with the fix for core-import-02.

### core-data-04: A file that changes and becomes unreadable keeps owning its song for good
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/References/ReferenceScanner.cs:256
- file: src/Wondarr.Core/References/ReferenceScanner.cs:605
- file: src/Wondarr.Core/References/ReferenceIdentifier.cs:159
- file: src/Wondarr.Core/References/ReferenceMatchService.cs:892
- summary: `RescanAsync` keeps `SongId` by design ("the file is the same song, only its bytes moved"), but when the probe fails it sets the state to `Unreadable` and stops: the identifier never looks at `Unreadable` rows, so the old-song link is neither confirmed nor released, and the song's `song_file` keeps pointing at a file that does not decode. The Match queue refuses `Unreadable` rows (`RequireResolvable`), so the user cannot skip it to free the song either.
- scenario: A reference FLAC identified as song 42 is overwritten by a truncated copy (new size/mtime). Scan: changed -> `RescanAsync` -> ffprobe fails -> `State = Unreadable`, `SongId = 42` kept, `song_file` still at that path. Every later scan re-probes it (still unreadable). Song 42 shows a file, is not wanted and is not searched (its file is a non-replaceable reference file), although nothing playable exists. Only replacing the file with a decodable one, or deleting it and waiting out the 24 h grace, ends the state.
- evidence: `row.Probe = null; row.LikelyYouTubeRip = false; row.State = ReferenceFileState.Unreadable;` (ReferenceScanner.cs:607-610, `SongId` untouched); identifier query `row.State == ReferenceFileState.Pending` (ReferenceIdentifier.cs:161); `RequireResolvable` allows only Ambiguous/Unmatched/Identified/Skipped (ReferenceMatchService.cs:892-909).
- fix: In `RescanAsync`, when the probe fails and the row carried a `SongId`, add the row to the scan's `leaving` handling (repoint to another Identified copy or release), or let the Match queue skip an Unreadable row.

### core-data-05: Docs and XML docs say adopt "moves/renames" the user's files; the adopter copies them
- severity: low
- confidence: high
- category: stale-doc
- file: docs/architecture/LIBRARY_OUTPUT.md:95
- file: src/Wondarr.Core/Domain/ReferenceLibrary.cs:11
- file: src/Wondarr.Core/Domain/ReferenceFile.cs:20
- file: src/Wondarr.Core/References/ReferenceAdopter.cs:23
- summary: The adopter stages a copy (`KeepSource: true`, organizer copies to `<target>/.wondarr-staging/`) and states the original "is never written, moved or deleted"; the architecture doc and the two enum summaries still describe adopt as moving/renaming the original "with the recycle bin as the safety net". A user deciding whether to enable adopt mode is told the opposite of what happens to their folder (and the on-disk result is a duplicate, not a move).
- scenario: User reads §7.6, enables adopt expecting the reference folder to be emptied into the library; after the run both copies exist and the reference rows are `Adopted` (the scanner keeps them that way even when the original later disappears, ReferenceScanner.cs:311).
- evidence: "**adopt** (the app re-tags and moves/renames them into a managed library layout, with the recycle bin as the safety net)" (LIBRARY_OUTPUT.md:95); `/// <summary>Adopt: identified files are re-tagged and moved into a managed library.</summary>` (ReferenceLibrary.cs:11); `/// Each one is <em>copied</em> — the user's original is never written, moved or deleted` (ReferenceAdopter.cs:25-26).
- fix: Update the doc sentence and the two enum summaries to say the file is copied and the original left in place.

### core-search-03: Bundler's "busy" filter disagrees with HasActiveForSongAsync, leaving a stray accepted record on the lead song's run
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Searching/ContainerBundler.cs:69
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1165
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1383
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1011
- file: src/Wondarr.Core/Sources/QueueService.cs:137
- summary: `ContainerBundler.FindAsync` treats only Queued/RemotelyQueued/Downloading as busy, but the grab's pre-check `HasActiveForSongAsync` (and the unique index) also count Completed and Importing. A bundled song whose download is waiting to be imported is therefore added to `AlsoWanted`, gets a candidate record saved on the lead song's run, and then its grab throws `AlreadyDownloadingException`; the catch clears the links but leaves the record `Accepted = true, Grabbed = false`.
- scenario: Song A's torrent is grabbed; song B (same artist, file in the torrent) has a queue item in `Completed` (downloaded, import pending). B passes the bundler (not busy), its file is selected on the torrent (`AlsoWanted`), `GrabBundledAsync` saves B's record (score = A's grabbed score), `GrabCandidateAsync` → `HasActiveForSongAsync(B)` true → `AlreadyDownloadingException` → record kept. Later the tracker fails A's item and calls `GrabBestAsync(A.run, 2)`: the list is ordered by score, A's top candidate is now `Grabbed`, so B's record can be `candidates[0]`, and `IsOwnedThroughReferenceAsync(candidates[0].SongId)` and the blocklist keys are read for B instead of A (the record itself is skipped by `LinkIsGone`). A also downloads B's file for nothing.
- evidence: bundler `Where(item => item.State == Queued || RemotelyQueued || Downloading)`; service `if (await _queue.HasActiveForSongAsync(record.SongId, …)) throw new AlreadyDownloadingException` where `HasActiveForSongAsync` lists five states; catch: `else if (exception is AlreadyDownloadingException && …) { ClearLinks(record); await _database.SaveChangesAsync(…); }`.
- fix: Use the same five states in the bundler's busy query, and on a refused bundled grab mark the record `Grabbed = true` (or `Accepted = false`) so it never heads a later `GrabBestAsync`; have `GrabBestAsync` take the song id from the run (or filter candidates to the run's song) rather than from `candidates[0]`.

### core-search-04: The attempt budget is per GrabBestAsync call, so one run can make up to 10 grab attempts with max_auto_attempts_per_search = 4
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1047
- file: src/Wondarr.Core/Searching/SongSearchService.cs:1127
- file: src/Wondarr.Core/Searching/SearchOptions.cs:30
- file: src/Wondarr.Core/Importing/QueueTracker.cs:727
- summary: `budget = Max(1, MaxAutoAttemptsPerSearch - attempt + 1)` counts only the grab-time failures inside one call, and every item written in that call carries the same `Attempt`. The tracker's cap (`item.Attempt >= Max`) is on the attempt number, not on how many candidates were tried, so a run can try 4 + 3 + 2 + 1 candidates before it gives up, not the 4 the option documents.
- scenario: Default options. Attempt 1: four peers refuse the enqueue (four Failed items, all `Attempt = 1`), budget spent. The fifth candidate downloads but fails verification; the tracker calls `GrabBestAsync(run, 2)` with budget 3, and so on down to attempt 4 with budget 1: up to ten candidates grabbed for one run.
- evidence: `var budget = Math.Max(1, _options.CurrentValue.MaxAutoAttemptsPerSearch - attempt + 1);` … `failures++; if (failures >= budget) { LogBudgetSpent(…); break; }`; option doc: "How many candidates one search run may try before giving up on it."
- fix: Count the run's existing failed queue items (or `Grabbed` candidates) against the budget, or bump `attempt` per candidate tried, so the per-run total is what the option says.

### core-search-05: Push grabs (JudgeAsync) bypass max_active_downloads and the slot gate
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Core/Searching/SongSearchService.cs:413
- file: src/Wondarr.Sources.Torznab/Searching/ReleasePushHandler.cs:168
- file: docs/architecture/ARCHITECTURE.md:42
- summary: `JudgeAsync` calls `GrabBestAsync` directly, never `HasFreeSlotAsync` or `TryGrabNowAsync`, so a release pushed by autobrr is grabbed regardless of how many downloads are in flight, while §5.2 step 6 says an automatic grab starts only below `search.max_active_downloads`.
- scenario: `max_active_downloads = 3`, three Soulseek downloads running. autobrr pushes five releases that each match a wanted song; all five are grabbed at once (eight in flight), and the next Missing Search then waits for a slot behind them.
- evidence: `long? queueItemId = grab && accepted > 0 ? await GrabBestAsync(run.Id, 1, cancellationToken).ConfigureAwait(false) : null;` (JudgeAsync); no slot check anywhere on that path.
- fix: Route the push grab through `TryGrabNowAsync` (fail fast as the batch loops do, recording `NoFreeSlot`), or state in ARCHITECTURE §5.2 that pushed releases are exempt from the slot limit.

### core-search-06: Missing Search selects songs that are already Completed/Importing, spending batch places and slot waits on them
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:164
- file: src/Wondarr.Core/Searching/UpgradeSearchCommandHandler.cs:170
- file: src/Wondarr.Core/Sources/QueueService.cs:137
- summary: The missing loop excludes only Queued/RemotelyQueued/Downloading items, whereas the upgrade loop and `HasActiveForSongAsync` exclude Completed and Importing too. A song whose download is awaiting import is selected, takes one of `missing_batch_size` places and a `WaitForSlotAsync`, and `SearchAsync` then returns "Already downloading".
- scenario: Imports are deferred (a compaction in progress, a root folder the import keeps deferring on) and 50 songs sit at Completed. The scheduled Missing Search fills its 50-song batch with them (they sort first: never searched, oldest rows), every one ends `Cancelled`/skipped, and songs that could have been searched are pushed to the next run.
- evidence: Missing: `item.State == Queued || RemotelyQueued || Downloading`; Upgrade: the same plus `|| item.State == QueueItemState.Completed || item.State == QueueItemState.Importing`.
- fix: Use the same five active states in the missing loop's `wanted` query.

### core-search-07: A missed Quartz fire is skipped, not run late
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Core/Jobs/ScheduledTaskService.cs:94
- file: src/Wondarr.Core/Jobs/ScheduledTaskService.cs:294
- file: src/Wondarr.Core/ServiceCollectionExtensions.cs:335
- summary: Every trigger is a `SimpleSchedule().WithInterval(…).RepeatForever()` with the default (smart) misfire policy, which for a repeat-forever simple trigger is `RescheduleNextWithRemainingCount`: a fire the scheduler could not deliver within the misfire threshold (default 60 s) is dropped and the trigger moves to the next interval boundary after now. The restart case is covered by `NextRunAt`, but a host that is suspended (laptop sleep, container pause) across a fire time is not.
- scenario: MissingSearch every 6 h, next fire 03:00; the host sleeps 02:00–09:00. At 09:00 Quartz sees a misfire, reschedules to 15:00, and no missing search runs until then (12 h gap); the job row still shows `NextRunAt = 03:00` until a run finishes.
- evidence: `.WithSimpleSchedule(schedule => schedule.WithInterval(definition.Interval).RepeatForever())` with no `WithMisfireHandlingInstruction…`; `services.AddQuartz();` with no misfire threshold configured.
- fix: Add `.WithMisfireHandlingInstructionFireNow()` to both trigger builders (or `NowWithRemainingCount`), so an overdue task runs once as soon as the scheduler is back. Confirm with a `ScheduledTaskServiceTests` case that advances the fake clock past a fire while the scheduler is paused.

### core-external-03: A cancelled waiter at the spacing gate leaves a dead slot that delays every later caller
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Core/Metadata/Http/RequestSpacingGate.cs:38
- file: src/Wondarr.Core/Metadata/Http/RequestSpacingGate.cs:54
- summary: `RequestSpacingGate.WaitAsync` reserves the slot (`_nextFreeSlot = slot + _minInterval`) before waiting, and a waiter that is cancelled never gives it back, so each abandoned request costs the host one full interval of silence that later callers, including background syncs, queue behind.
- scenario: Ten Add-songs lookups are started and abandoned while queued (the controller passes `RequestAborted`, so a closed tab or a superseded request cancels the wait). Each reserved a MusicBrainz slot 1.05 s apart; the next import-list sync's first MusicBrainz request waits ~10 s for slots nothing uses. The interactive fail-fast rule only covers Retry-After cooldowns, not reserved-but-unused slots.
- evidence: `/// <param name="cancellationToken">Cancels the wait; the slot stays reserved.</param>` (RequestSpacingGate.cs:38); `_nextFreeSlot = slot + _minInterval; delay = slot - now;` (RequestSpacingGate.cs:54-55) with no compensation on cancellation.
- fix: On cancellation inside `WaitUntilAsync`, pull `_nextFreeSlot` back by one interval when the cancelled slot was the last one reserved (or track reserved slots so a cancelled one can be reused by the next waiter).

### core-external-04: Five spacing handlers are registered without a provider key, so fail-fast errors say "unknown"
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:215
- file: src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:254
- file: src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:326
- file: src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:373
- file: src/Wondarr.Core/Lyrics/LyricsServiceCollectionExtensions.cs:57
- file: src/Wondarr.Core/Metadata/Http/RequestSpacingHandler.cs:23
- summary: Only the MusicBrainz and Deezer handlers pass `ProviderKeys.*`; AcoustID, Cover Art Archive, iTunes, Last.fm and LRCLIB fall back to the constructor default `"unknown"`, which is what the interactive fail-fast `MetadataProviderException` names in `Provider` and in its message.
- scenario: iTunes answered 403/429 with `Retry-After: 60` a moment ago; a person searches on the Add-songs page; `ITunesReferenceLengthAsync` catches the fail-fast and logs the Warning `itunes did not answer the search, carrying on without it: unknown asked to be left alone for 58 more seconds.` The `Provider` property on the exception is also `"unknown"` for anyone switching on it.
- evidence: `public RequestSpacingHandler(RequestSpacingGate gate, string provider = "unknown")` (RequestSpacingHandler.cs:23) and `$"{_provider} asked to be left alone for ... more seconds."` (RequestSpacingHandler.cs:45); the five registrations pass one argument only.
- fix: Pass the provider key at every registration (`"acoustid"`, `CoverArtArchiveClient.Provider`, `ITunesClient.Provider`, `"lastfm"`, `"lrclib"`) and drop the default so the compiler enforces it.

### core-external-05: Per-list Last.fm API keys are never registered with the redactor, and `api_key=` escapes the cleanser
- severity: low
- confidence: low
- category: security
- file: src/Wondarr.Core/ImportLists/ImportListService.cs:699
- file: src/Wondarr.Core/Notifications/NotificationSecretRegistrar.cs:56
- file: src/Wondarr.Core/Logging/CleanseLogMessage.cs:21
- file: src/Wondarr.Core/ImportLists/LastFm/LastFmList.cs:148
- summary: Notifications register every `Secret: true` field with `ISecretRegistry` on save, test and startup; import lists (`apiKey` on the Last.fm loved/top lists) never do (`ApplyAsync` only merges and validates, and the startup registrar reads only `Notifications`). The generic fallback `CleanseLogMessage` does not match `api_key=` either: its URL rule needs one of `?&: ;` immediately before the parameter name and lists `apikey`/`key` but not `api_key`, so `&api_key=abc...` is left as is. The key travels in the Last.fm query string (`LastFmList.cs:150`).
- scenario: No log line or exception text at Warning or above carries the Last.fm request URI today (`System.Net.Http.HttpClient` and `Polly` are overridden to Warning, `LastFmFetch` swallows transport exceptions, `ImportListService.SyncAsync` rewrites exception text), so this is a defence-in-depth gap rather than a demonstrated leak. What would confirm it as a real leak: any future log or feedback text that includes a Last.fm request URL, or a user pasting a sync error with the URL into the public feedback form (`FeedbackService` redacts only registered secrets plus the cleanser).
- evidence: `NotificationSecrets.Register(_secrets, NotificationSecrets.Read(notification.Settings), provider.Fields);` exists only for notifications (NotificationSecretRegistrar.cs:63, NotificationService.cs:241); `ImportListService.ApplyAsync` (lines 699-755) has no `Register` call; cleanser rule `(?<=[?&: ;])((?:api|auth|pass)?key|...|api|[a-z_]*apikey|...)=` (CleanseLogMessage.cs:21) has no alternative that matches `api_key`. `CleanseLogMessageTests` has no `api_key=` case.
- fix: Call `NotificationSecrets.Register` for the provider's secret fields in `ImportListService.CreateAsync/UpdateAsync` and in a startup pass over `ImportLists` (or widen `NotificationSecretRegistrar`); add `api[-_]?key` to the cleanser's URL rule with a test.

### core-external-06: `ImportListItemJson.ReadLine` doc promises a fallback the code does not have
- severity: low
- confidence: high
- category: stale-doc
- file: src/Wondarr.Core/ImportLists/PasteListService.cs:797
- summary: The XML doc says the method returns "the raw JSON when it cannot be read", but only a `null` deserialisation result falls back; a `JsonException` propagates. `ReadEntry` directly below does catch it. Not reachable today because `Raw` is always written by `ImportListItemJson.Write` (and the column has defaulted to `{}` since the first migration), so this is a misleading comment rather than a crash, but the first code path that stores anything else in `Raw` will abort a whole run from this method.
- scenario: A row whose `raw` column holds non-JSON (no writer does this now) makes `ResolveLineAsync` throw `JsonException` from `ReadLine` before its try block, aborting `ProcessAsync`.
- evidence: `/// <returns>The stored line; the raw JSON when it cannot be read.</returns>` then `return JsonSerializer.Deserialize<ImportListLine>(item.Raw, StoredJson) ?? new ImportListLine(item.Raw, null, null);` (PasteListService.cs:797-804) with no `catch (JsonException)`, unlike `ReadEntry` (lines 836-843).
- fix: Either catch `JsonException` and return `new ImportListLine(item.Raw, null, null)` as the doc says, or correct the doc.

### sources-03: A Soulseek username or password that looks like YAML null is rendered unquoted
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Sources.Slskd/SlskdConfigRenderer.cs:45
- file: src/Wondarr.Sources.Slskd/SlskdConfigRenderer.cs:119
- file: src/Wondarr.Sources.Slskd/SlskdConfigRenderer.cs:10
- file: Directory.Packages.props:29
- summary: The serializer is built without `WithQuotingNecessaryStrings()`, which in YamlDotNet
  18.1.0 is opt-in ("Put double quotes around strings that need it, for example Null, True, False, a
  number"). Values that are YAML's null forms are emitted as plain scalars, so slskd reads them as
  no credential. The class remark ("a value containing a colon, a quote or a newline cannot break
  the file") is true but does not cover this.
- scenario: A user whose Soulseek password is literally `null`, `Null`, `NULL` or `~` (or a username
  of that form) saves it. Verified with the package DLL and the renderer's exact builder:
  `password: null`, `password: ~`, `password: true`, `password: 123`, `password: yes` are all written
  plain (a `"` or `: ` value is correctly single-quoted). slskd's YAML configuration provider
  (NetEscapades-style) maps a plain `~`/`null` scalar to a null value, so slskd starts with
  `no_connect` semantics / an empty password, and the health page shows "logged out" with no hint
  why. The typed-looking forms (`true`, `123`, `yes`) survive because `IConfiguration` values are
  strings.
- evidence:
  ```
  private readonly ISerializer _serializer = new SerializerBuilder()
      .WithNamingConvention(UnderscoredNamingConvention.Instance)
      .WithNewLine("\n")
      .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
      .Build();
  ```
  `dotnet fsi` output: `---- password "null" => … password: null` / `---- password "~" => … password: ~`.
- fix: Add `.WithQuotingNecessaryStrings(quoteYaml1_1Strings: true)` as the first builder call (the
  XML doc says it must come before the other `With` calls) and add golden cases for `null`, `~`,
  `true`, `123` to SlskdConfigRendererTests (currently only `pass"quote` is covered, :145-152). What
  would raise confidence to high: a check of slskd 0.26's `YamlConfigurationFileParser` null handling.

### sources-04: YouTube ReadOnlyFields names nested ytdlp keys in a spelling the UI never matches
- severity: low
- confidence: high
- category: correctness
- file: src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs:518
- file: src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs:225
- file: src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs:505
- file: src/Wondarr.Sources.Slskd/SoulseekSettingsService.cs:598
- file: frontend/src/api/youtube.ts:24
- file: frontend/src/pages/settings/YouTubeSettingsPage.tsx:176
- summary: `YouTubeSettingsService.CamelCase` splits on `_` and `.` but not on `:`, while the nested
  keys are written with `:` (`ytdlp:sleep_requests_seconds`). It therefore returns
  `ytdlp:sleepRequestsSeconds`, whereas the Soulseek service splits on `:` as well and the YouTube
  page looks for `ytdlpSleepRequestsSeconds` (youtube.ts:30-33, page :399-438).
- scenario: The operator sets `APP__YOUTUBE__YTDLP__RETRIES=3`. `GetAsync` reports
  `readOnlyFields: ["ytdlp:retries"]`; `isLocked('ytdlpRetries')` is false, so the Retries input is
  editable with no lock tooltip; on save the server refuses with "ytdlp:retries is set by the
  environment variable APP__YOUTUBE__YTDLP__RETRIES …" (:507-509), which the page shows as a general
  error under the form. The locking itself works (`IsLocked` at :514 normalises `:` correctly); only
  the reported name is wrong.
- evidence:
  ```
  var parts = key.Split('_', '.');      // YouTubeSettingsService.CamelCase
  ...
  var parts = key.Split('_', ':');      // SoulseekSettingsService.CamelCase
  ```
- fix: Split on `':'` (and `'.'`) in the YouTube `CamelCase`, matching the Soulseek one and the
  frontend's `YOUTUBE_FIELDS`. No test pins the camelCase of a nested key (grep for
  `ytdlpSleep|ytdlp:sleep` in tests finds nothing).

### sources-05: A `%` in youtube.downloads_dir is read by yt-dlp as an output-template directive
- severity: low
- confidence: medium
- category: correctness
- file: src/Wondarr.Sources.YouTube/YtDlpRunner.cs:367
- file: src/Wondarr.Sources.YouTube/YouTubeSourceProvider.cs:211
- file: src/Wondarr.Sources.YouTube/YouTubeOptions.cs:189
- summary: The destination directory is spliced into the `-o` template
  (`$"{destinationDir}/%(id)s.%(ext)s"`), and yt-dlp interprets `%` in the whole template; percent
  literals must be written `%%`. The per-grab part is safe (Core builds it as `wondarr/<32 hex>`,
  SongSearchService.cs:1220), but `DownloadsDir` is user-set and not validated.
- scenario: `youtube: downloads_dir: /data/100% music/youtube`. Every download runs with
  `-o "/data/100% music/youtube/wondarr/<hex>/%(id)s.%(ext)s"`; yt-dlp rejects the template
  (`% m` is not a valid conversion) and exits non-zero, `Classify` maps the stderr to `Unknown` →
  `RetryLater`, so each YouTube grab fails and is retried forever with no message that names the
  folder setting.
- evidence:
  ```
  "-o",
  $"{destinationDir}/%(id)s.%(ext)s",
  ```
- fix: Pass the directory through `-P <destinationDir>` (yt-dlp's `--paths`) and keep `-o` to
  `%(id)s.%(ext)s`, or escape `%` as `%%` in the directory part. A validator rule on `DownloadsDir`
  would also do. Not covered by a test (YtDlpRunnerTests asserts the argument shape with a plain path).

### sources-06: A settings restart that outlives the shutdown budget can leave a freshly launched slskd behind
- severity: low
- confidence: low
- category: concurrency
- file: src/Wondarr.Sources.Slskd/SlskdHost.cs:301
- file: src/Wondarr.Sources.Slskd/SlskdHost.cs:329
- file: src/Wondarr.Sources.Slskd/SlskdHost.cs:414
- file: src/Wondarr.Sources.Slskd/SlskdHost.cs:667
- summary: `StopAsync` waits for the lifecycle gate only until the host's token fires, then calls
  `StopChildAsync` without the gate. `ApplySettingsAsync` holds the gate across `StopChildAsync` (up
  to 4 s + 2 s) and `StartChildAsync`, which launches the new process and sets `_child` afterwards.
  If the ungated `StopChildAsync` runs in the window between the old child being cleared and
  `_child = child`, it sees no child and returns; the launch completes and Wondarr exits with slskd
  running.
- scenario: The user saves new credentials and stops Wondarr within the same few seconds on a
  non-Docker install; slskd ignores SIGTERM for the full 4 s, the tree kill waits its 2 s, the gate
  is still held when the 8 s host budget elapses (`gotGate = false`), `StopChildAsync` finds
  `_child == null`, `ApplySettingsAsync` then launches the replacement. slskd keeps ports 5030/50300
  until the next Wondarr start, which does kill it through the pid file (`WritePidFile` runs right
  after `Launch`, :669), and a Docker stop kills the whole tree anyway — which is why this is low.
- evidence:
  ```
  try { await _lifecycle.WaitAsync(cancellationToken) } catch (OperationCanceledException) { gotGate = false; }
  ...
  await StopChildAsync(cancellationToken, skipGraceful)   // without the gate when gotGate is false
  ...
  var child = _launcher.Launch(BuildRequest(options)); WritePidFile(child.Id); lock (_processGate) { ... _child = child; }
  ```
- fix: Set a "stopping" flag in `StopAsync` before the gate wait and have `StartChildAsync` refuse to
  launch (under the gate) once it is set; or, when the gate could not be taken, re-check `Child` once
  more after `StopChildAsync` and kill it. What would confirm: a harness test where the fake
  launcher's `RequestStop` never exits and `ApplySettingsAsync` races `StopAsync` with a short token.

### frontend-06: The Library page's Artist filter is never invalidated
- severity: low
- confidence: high
- category: correctness
- file: frontend/src/api/songs.ts:40
- file: frontend/src/api/songs.ts:109
- file: frontend/src/api/songs.ts:419
- file: frontend/src/pages/LibraryPage.tsx:337
- summary: `ARTISTS_QUERY_KEY = ['artists']` is read by the Artist filter but no mutation, `invalidateLibrary`, or SignalR branch invalidates it, so within one visit to the Library page the artist list goes stale.
- scenario: On the Library page delete (row menu or mass delete) the only song of an artist. The table updates; the Artist dropdown still offers that artist, and picking it yields the empty state. It stays wrong until the page is left and re-entered (`refetchOnWindowFocus` is off).
- evidence: `invalidateLibrary` lists SONGS, TAGS, WANTED, IMPORT_LISTS, IMPORT_LIST_ITEMS (songs.ts:109-115) but not `ARTISTS_QUERY_KEY`; `grep ARTISTS_QUERY_KEY` finds only the declaration and `useArtists`.
- fix: Add `ARTISTS_QUERY_KEY` to `invalidateLibrary` (it also covers adds of a new artist by the search tab, which currently only works because the page remounts).

### frontend-07: Library filters desync from the URL when the route changes without a remount
- severity: low
- confidence: high
- category: correctness
- file: frontend/src/pages/LibraryPage.tsx:307
- file: frontend/src/pages/LibraryPage.tsx:323
- file: frontend/src/components/AppLayout.tsx:328
- summary: The filters are read from the search params once (`useState` initializer) and written back only when `filters` changes; a navigation that changes the search params while `LibraryPage` stays mounted is ignored.
- scenario: Open `/library?artistId=5` (e.g. from a song header's artist link, then the browser Back/Forward, or any link to `/library` while on the page). Click "Library" in the navbar, whose `to` is `/library`. The URL now shows no filter, but the list and the Artist select are still filtered by artist 5; a reload shows the whole library.
- evidence: `const [filters, setFilters] = useState<LibraryFilters>(() => fromViewEntries([...params.entries()]…))` (307-309); the write-back effect depends on `[filters]` only with the comment "the URL itself is read once, on mount" (333-335); `{ to: '/library', … }` (AppLayout.tsx:328).
- fix: Derive the filters from `useSearchParams` (treat the URL as the source of truth and write through `setParams`), or resync `filters` when `params` change.

### frontend-08: Two "keyed so a refetch resets the form" comments describe behaviour the key does not give
- severity: low
- confidence: high
- category: stale-doc
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:327
- file: frontend/src/pages/settings/LibrarySettingsPage.tsx:716
- file: frontend/src/pages/settings/SoulseekSettingsPage.tsx:163
- file: frontend/src/pages/settings/SoulseekSettingsPage.tsx:473
- summary: `LibraryForm` is keyed by `library.id` and `SoulseekForm` by `username-mode`; neither changes on an ordinary refetch, so the local `useState` copies of the fields are kept, not reset, contrary to what the comments say.
- scenario: After a save the server normalises a value (trims a root path, nulls an emptied `plexLibraryPath`, or refuses one field and keeps the rest); `LIBRARIES_QUERY_KEY` refetches but the form keeps showing the typed values, so what the user sees is not what is stored. Anyone relying on the comment to reason about staleness will be misled.
- evidence: "It is mounted with `key={library.id}`, so a refetch that replaces the library starts the fields from the stored values again" (LibrarySettingsPage.tsx:327-328) vs `<LibraryForm key={String(library.id)} …>` (716); "It is keyed on the settings it was mounted with, so a refetch starts it over" (SoulseekSettingsPage.tsx:163) vs `key={`${String(settings.data.username)}-${settings.data.mode}`}` (473).
- fix: Either key the forms on the data they mirror (e.g. `dataUpdatedAt` / a hash of the resource) or reword the comments to say the edits survive a refetch.

### frontend-09: `apiRequest` in the API client is dead code with a misleading contract
- severity: low
- confidence: high
- category: correctness
- file: frontend/src/api/client.ts:43
- file: frontend/src/api/types.ts:1
- summary: `apiRequest` has no callers (grep finds only its definition), its doc says it "exists only for the endpoints listed in `types.ts`" but `types.ts` lists no endpoint for it, and if it were used its `headers: { …, ...init?.headers }` would silently drop a `Headers` instance or tuple array (spreading those yields no own enumerable keys), losing the caller's headers while keeping the API key.
- scenario: A future caller passes `{ headers: new Headers({ 'Content-Type': 'application/json' }) }`; the request goes out without the content type.
- evidence: `headers: { Accept: 'application/json', 'X-Api-Key': config.apiKey, ...init?.headers }` (client.ts:46-50); `rg apiRequest frontend/src` → only client.ts:43.
- fix: Delete `apiRequest` (and the paragraph in `types.ts` that refers to it), or build the headers with `new Headers(init?.headers)` and `set` the two defaults.

### frontend-10: Schema queries are nested under list keys, so every list change refetches the "never stale" schema
- severity: low
- confidence: high
- category: correctness
- file: frontend/src/api/importLists.ts:29
- file: frontend/src/api/importLists.ts:111
- file: frontend/src/api/songs.ts:113
- file: frontend/src/api/notifications.ts:26
- file: frontend/src/api/indexers.ts:26
- file: frontend/src/api/downloadClients.ts:25
- summary: `['import-lists','schema']`, `['notifications','schema']`, `['indexers','schema']` and `['downloadClients','schema']` are children of the list keys that every create/update/delete invalidates; `invalidateLibrary` (every song add, edit or delete, and every song-changing command) invalidates `['import-lists']` too. `staleTime: Infinity` does not stop an explicit invalidation, so the schema is refetched despite the comment "fetched once and never considered stale".
- scenario: With Settings → Import lists open, add a song elsewhere (or toggle Monitored on any song): `invalidateLibrary` → `['import-lists']` → `GET /api/v1/importlist/schema` again, and the form briefly falls back to `fields: []` if the schema is in flight when a dialog opens.
- evidence: `IMPORT_LIST_SCHEMA_QUERY_KEY = ['import-lists', 'schema']` (importLists.ts:29); `void queryClient.invalidateQueries({ queryKey: IMPORT_LISTS_QUERY_KEY })` in `invalidateLibrary` (songs.ts:113); comment "fetched once and never considered stale" (importLists.ts:103-105).
- fix: Use root keys for the schemas (e.g. `['import-list-schema']`) or invalidate the lists with `exact: true`.

### frontend-12: Add songs rows keyed by `mbRecordingId` collide when the id is an empty string
- severity: low
- confidence: low
- category: correctness
- file: frontend/src/pages/AddSongsPage.tsx:227
- file: frontend/src/pages/AddSongsPage.tsx:243
- file: frontend/src/pages/AddSongsPage.tsx:280
- file: frontend/src/api/songs.ts:563
- summary: `candidateAddInput` treats `mbRecordingId === ''` as "no MBID" and adds by Deezer id, but the row key, the `added` map key and the `pressed` key use `candidate.mbRecordingId ?? \`deezer:…\``, where `''` is not nullish. For such a candidate the row key is `''`, the in-flight/error state never attaches to the row (`pressed` is `deezer:<id>`), and after one add every `''`-keyed row shows "In library" with the first song's id.
- scenario: Only if `POST /api/v1/song/lookup` can answer with `mbRecordingId: ""` (the frontend guards for it, which suggests it has been seen). Confirm by checking the lookup serialiser; if it never emits `""`, drop the `''` branch in `candidateAddInput` instead.
- evidence: `candidate.mbRecordingId !== null && candidate.mbRecordingId !== ''` (songs.ts:563) vs `const key = candidate.mbRecordingId ?? \`deezer:${String(candidate.deezerId)}\`` (AddSongsPage.tsx:280) and `variables.mbRecordingId ?? …` (243).
- fix: Derive one `candidateKey(candidate)` helper from `candidateAddInput`'s rule and use it for the row key, the `added` map and `pressed`.

### ops-04: ci.yml labels the smoke step "Phase 0 to Phase 3" but the script's defaults run Phases 0–7
- severity: low
- confidence: high
- category: stale-doc
- file: .github/workflows/ci.yml:108
- file: .github/workflows/ci.yml:112
- file: scripts/smoke-test.sh:342
- file: scripts/smoke-test.sh:494
- summary: The step name and its comment describe Phases 0, 1 and 2 (3), while `scripts/smoke-test.sh wondarr:ci` with no `SMOKE_PHASE*` variables runs Phases 4, 5, 6 and 7 too (`${SMOKE_PHASE4:-on}` … `${SMOKE_PHASE7:-on}`).
- scenario: Someone reading the workflow to understand what a red "Phase gate smoke test (Phase 0 to Phase 3)" step covers, or how long the CI job takes, is misled; a Phase 6/7 failure shows up under a step that claims not to run them.
- evidence: ci.yml 112 `- name: Phase gate smoke test (Phase 0 to Phase 3)`; smoke-test.sh 342 `if [ "${SMOKE_PHASE4:-on}" = off ]`, 374 `SMOKE_PHASE5:-on`, 494 `SMOKE_PHASE6:-on`, 500 `SMOKE_PHASE7:-on`.
- fix: Rename the step and comment to "Phase 0 to Phase 7" (or state that it runs every phase the script enables by default).

### ops-05: ci.yml still says the GHCR package is private; the release smoke job and every template pull it anonymously
- severity: low
- confidence: high
- category: stale-doc
- file: .github/workflows/ci.yml:97
- file: .github/workflows/release.yml:142
- file: README.md:37
- summary: The comment justifying the per-push image artifact says "The repository (and so the GHCR package) is private". The release `smoke` job pulls `${IMAGE}@<digest>` with no `docker/login-action` step (it has `packages: read` but never logs in), and README/Unraid/Cosmos/DockSTARTer all pull `ghcr.io/wulfftech/wondarr` without credentials, so the package is public and the comment is stale. The 3-day `wondarr-image-amd64` artifact (a gzipped full image on every push to `main`) is kept for a reason that no longer holds.
- scenario: A maintainer reading ci.yml believes the package is private and keeps the export step (storage and minutes on every push) or, worse, assumes the image is not reachable by the public.
- evidence: ci.yml 97–98 "The repository (and so the GHCR package) is private; a short-lived artifact lets the image be loaded on a test host without registry credentials there."; release.yml 142–146 `docker pull --platform … "${IMAGE}@${{ needs.image.outputs.digest }}"` with no login step in that job.
- fix: Update the comment, and either drop the export/upload steps or restate why the artifact is still wanted.

### ops-06: `.gitignore` ignores every `config/` and `data/` directory anywhere in the tree, not just the runtime ones
- severity: low
- confidence: high
- category: correctness
- file: .gitignore:8
- file: .gitignore:9
- summary: `config/` and `data/` are unanchored patterns, so they match `frontend/src/config/`, `website/src/data/`, `tests/fixtures/data/`, `src/*/config/` and so on. Verified with `git check-ignore -v`: `frontend/src/config/x.ts`, `website/src/data/x.ts`, `tests/fixtures/data/x.json` are all ignored by lines 8/9. No tracked path is affected today, so this is a latent trap.
- scenario: A contributor adds `frontend/src/config/featureFlags.ts` and `git add -A` / an IDE commit silently skips it; their local build and tests pass, CI's `npm run typecheck` fails with a missing module (or, if the file is only imported lazily, the bug ships).
- evidence: `.gitignore` lines 7–9: `config/` / `data/` under the "secrets and local config" heading, clearly meant for the compose-created `./config` and `./data` next to `docker/docker-compose.yml`.
- fix: Anchor them: `/config/`, `/data/`, `/docker/config/`, `/docker/data/`.

### ops-07: check-notice.py accepts any backticked mention anywhere in NOTICE.md as "listed", and never scans `tests/`
- severity: low
- confidence: high
- category: test-gap
- file: scripts/check-notice.py:20
- file: scripts/check-notice.py:110
- file: CONTRIBUTING.md:67
- summary: The "missing" check is `f"\`{rel}\`" not in notice` over the whole file, not over the ported-code table that `listed_files()` already knows how to parse; and `SOURCES` excludes `tests/`, so a ported test file (Lidarr's parser tests are the obvious candidate) is never checked. CONTRIBUTING.md promises the script "fails when a `Ported from` header has no entry" (a row).
- scenario: (a) A port of `src/NzbDrone.Core/Parser/X.cs` lands as `src/Wondarr.Core/Parser/X.cs` with a correct header; instead of a table row the author writes "`src/Wondarr.Core/Parser/X.cs` is adapted from Lidarr" in the intro paragraph (NOTICE.md line 7 already shows prose mentioning a backticked path). CI passes, the attribution table that NOTICE.md says is the record lacks the row. (b) `tests/Wondarr.Core.Tests/Parser/XTests.cs` is ported from Lidarr with a `// Ported from …` header and no NOTICE row: CI passes; today `grep -rniE '(ported|adapted|derived)\s+from' tests` finds no such header, so nothing is currently wrong, only unguarded.
- evidence: line 110 `missing = [rel for rel in ported_files() if f"\`{rel}\`" not in notice]`; line 20 `SOURCES = ("src", "frontend/src", "tools", "docker", "scripts")`; lines 76–90 `listed_files()` parses the `## Ported` table but is only used for the exists-check.
- fix: Build the "listed" set from `listed_files(notice)` and compare against `ported_files()`; add `"tests"` to `SOURCES` (fixtures have no `.cs/.ts/.py/.sh` suffix, so the scan stays cheap).

### ops-08: remote-run.sh aborts after uploading the tree when the last listed file is deleted locally
- severity: low
- confidence: medium
- category: correctness
- file: scripts/remote-run.sh:26
- file: scripts/remote-run.sh:79
- summary: The upload pipeline's filter is `while …; do [[ -e "$file" ]] && printf …; done`. A `while` loop's status is that of the last body command, so when the final entry from `git ls-files` does not exist on disk the loop exits 1; with `set -o pipefail` the whole pipeline is non-zero even though `tar | ssh` succeeded, and `set -e` ends the script before the lock/run step.
- scenario: No untracked files in the checkout (so the last entry is the alphabetically last tracked file, e.g. under `website/`), and that file has been deleted in the working tree (a rename in progress, a removed doc page). `scripts/remote-run.sh dotnet test` prints "sending the tree", finishes the upload, then exits 1 with no message; `safe-merge.sh` reports "the current tree is red".
- evidence: lines 79–82 `git ls-files -z … | while IFS= read -r -d '' file; do [[ -e "${file}" ]] && printf '%s\0' "${file}"; done | tar --null -T - -czf - | "${ssh_bin}" …` under `set -euo pipefail` (line 26). Confirmable with `printf 'a\0' | while IFS= read -r -d '' f; do [[ -e "$f" ]] && echo "$f"; done; echo $?` → `1`.
- fix: End the loop body with `|| true` / `:` (e.g. `if [[ -e "$file" ]]; then printf …; fi`), or filter with `git ls-files … | xargs -0 -n1 sh -c 'test -e "$0" && printf "%s\0" "$0"'`.

### ops-09: safe-merge.sh runs the full (possibly remote) check for an already-merged branch and then dies at `git commit`
- severity: low
- confidence: high
- category: correctness
- file: scripts/safe-merge.sh:256
- file: scripts/safe-merge.sh:263
- summary: `git merge --no-ff --no-commit <branch>` returns 0 with "Already up to date." when the branch is an ancestor of HEAD; the script then runs the merged-tree check (minutes, on the test host) and `git commit --no-edit -m` fails with "nothing to commit", which `set -e` turns into an exit 1 after the work was done.
- scenario: A worker branch is merged twice (a retry after a flaky run, or `wsl-safe-merge.sh` re-invoked). The second run spends the whole check and ends with git's "nothing to commit" message and a non-zero exit, with no `safe-merge:` line explaining it and no marker update.
- evidence: lines 256–259 only handle a non-zero merge ("conflicts"); line 263 `git commit --no-edit -m "${message}"` with nothing staged.
- fix: After the merge, `git rev-parse -q --verify MERGE_HEAD >/dev/null || { echo "safe-merge: ${branch} is already merged"; exit 0; }`.

### ops-10: The secondary-container wait loops in smoke-test.sh never fail on `unhealthy` or timeout
- severity: low
- confidence: high
- category: correctness
- file: scripts/smoke-test.sh:243
- file: scripts/smoke-test.sh:280
- file: scripts/smoke-test.sh:409
- file: scripts/smoke-test.sh:460
- summary: `wait_healthy` (line 84) fails fast on `unhealthy` and on the 180 s timeout, but the four inline loops for the `-p6`, `-p7`, `-fresh` and `-ext` containers only `break` on `healthy`; when the container is unhealthy or never comes up they fall through, the API key is read from a `config.yml` the app wrote before crashing, and the Python gate is started against a dead endpoint.
- scenario: The Phase 6 container crashes at start (a bad env value, a port clash). The loop spends 180 s, the key read succeeds, `phase6-gate.py plex` fails on a `URLError` traceback after its own HTTP timeouts, and the `fail` message is "Phase 6 gate (Plex)" with the Phase 6 logs, not "container unhealthy" — the cause is three screens away.
- evidence: lines 243–246 `for _ in $(seq 1 90); do [ "$($DOCKER inspect … "$P6")" = healthy ] && break; sleep 2; done` with no action after the loop; the same shape at 280–283, 409–412, 460–463.
- fix: Give `wait_healthy` a container-name parameter and use it in all five places.

### ops-12: The app and its child processes run as uid 1000 with `HOME=/root`; only slskd has a workaround
- severity: low
- confidence: low
- category: correctness
- file: docker/root/etc/s6-overlay/s6-rc.d/svc-wondarr/run:9
- file: docker/Dockerfile:205
- file: src/Wondarr.Sources.Slskd/SlskdHost.cs:908
- summary: No `USER` in the image, so Docker sets `HOME=/root`; `with-contenv` imports it and `s6-setuidgid wondarr` does not change it. The Dockerfile sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/config/slskd/.net` precisely because slskd's single-file extraction would otherwise land in `/root/.net` and fail, but nothing equivalent is done for `yt-dlp` (`~/.cache/yt-dlp`) and Deno (`DENO_DIR` → `~/.cache/deno`), and `src/` sets no `HOME`/`XDG_CACHE_HOME`/`DENO_DIR` for those children. The smoke test only runs `deno --version` as PUID (line 197–198) and Phase 4 uses the fake yt-dlp, so the real yt-dlp + Deno path is never exercised as the unprivileged user in CI.
- scenario: With the YouTube source enabled, a real download runs `yt-dlp` as uid 1000 with `HOME=/root`: yt-dlp warns it cannot write its cache on every run, and if Deno needs a writable `DENO_DIR` for the challenge solver the download fails with an EJS/JS-runtime error that the health check (`deno --version` works) does not predict.
- evidence: run script line 9 `exec s6-setuidgid wondarr dotnet /app/Wondarr.Api.dll` (no `HOME=` / `env -i`); Dockerfile 205 `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/config/slskd/.net`; `grep -rn '"HOME"\|XDG_CACHE_HOME\|DENO_DIR' src` → only the slskd bundle-dir line.
- fix: Confirm with `docker exec -u 1000:1000 <container> sh -c 'echo $HOME; yt-dlp -v --simulate <any video url>'` on the published image (look for cache warnings or a Deno error). If it misbehaves, set `HOME=/config` (and/or `XDG_CACHE_HOME=/config/cache`, `DENO_DIR=/config/cache/deno`) in `svc-wondarr/run` or as image `ENV`.

### docs-user-07: first-run.md lists four album policies; the UI and layouts page have five
- severity: low
- confidence: high
- category: stale-doc
- file: website/src/content/docs/getting-started/first-run.md:46
- file: website/src/content/docs/library/layouts-and-albums.md:31-35
- summary: S4. First run says "The others are **Singles only**, **Original album** and **Single release**"; the select also offers **Compilation**, which the layouts page documents.
- scenario: A reader of first-run.md does not learn that a library-wide Various Artists compilation layout exists.
- evidence: first-run.md:46 "The others are **Singles only**, **Original album** and **Single release**."; frontend/src/api/profiles.ts:220-240 labels `Fewest albums`, `Singles only`, `Original album`, `Single release`, `Compilation`.
- fix: Add **Compilation** to the list, or replace the list with a link to the layouts page.

### docs-user-08: Two bold UI labels differ from what the UI shows
- severity: low
- confidence: high
- category: stale-doc
- file: website/src/content/docs/library/import-lists.md:43
- file: website/src/content/docs/library/managing-the-library.md:25
- summary: S1/R4. import-lists.md bolds "**0 means only when I ask**" as the field's text; the field's description reads "0 = only when I ask". managing-the-library.md bolds "**Save view**"; the menu item is "Save view…".
- scenario: A reader searching the page for the bold text does not find it word for word.
- evidence: frontend/src/pages/settings/ImportListsSettingsPage.tsx:405 `description="0 = only when I ask"`; frontend/src/pages/library/SavedViewsMenu.tsx:74 `Save view…`.
- fix: Quote the labels as shown ("0 = only when I ask", **Save view…**), or unbold the paraphrase.

### docs-user-09: Internal "plan" and "Phase" references survive in the README, CONTRIBUTING and the changelog
- severity: low
- confidence: high
- category: stale-doc
- file: README.md:84
- file: CONTRIBUTING.md:31
- file: CHANGELOG.md:374
- file: CHANGELOG.md:422
- summary: S2. The planning documents (`docs/build/tasks/`, the phase files) are git-ignored, so "the plan", "Phase 0 gate" and "every phase of the plan (0–8)" point at nothing a reader can open. The 0.1.9 entry (line 105) says public docs no longer cite them.
- scenario: A contributor reads "Phase 0 gate smoke test" and looks for a Phase 0 document; none is in the repository.
- evidence: README.md:84 "Every part of the plan is built and it's in daily use."; CONTRIBUTING.md:31 "# Phase 0 gate smoke test against a built image"; CHANGELOG.md:374 "every phase of the plan (0–8) is in"; CHANGELOG.md:422 "Phase 0 — skeleton, foundations ..."; .gitignore:70 `docs/build/tasks/`.
- fix: README: "It is feature-complete for its first release and in daily use." CONTRIBUTING: "# smoke test against a built image". The two changelog lines are history and can stay, or lose the phase numbers.

### docs-user-10: CONTRIBUTING's repository map lists three of the sixteen scripts as if they were all
- severity: low
- confidence: high
- category: stale-doc
- file: CONTRIBUTING.md:44
- summary: The map's `scripts/` row names `fetch-slskd.sh, smoke-test.sh, check-notice.py`; the folder also holds `remote-run.sh` (used on line 26 of the same file), `metadata-replay.py`, `safe-merge.sh`, `wsl-safe-merge.sh`, seven `phaseN-gate.py`/`-scenario.py` files and a `tests/` folder.
- scenario: A contributor trusts the map, does not learn what `remote-run.sh` or the gate scripts are, and `phase*-gate.py` again points at the absent plan (S2).
- evidence: CONTRIBUTING.md:44 "scripts/                     # fetch-slskd.sh, smoke-test.sh, check-notice.py"; `ls scripts/` shows 16 entries.
- fix: Describe the folder ("helper scripts: the slskd fetch, the smoke test, the NOTICE check, the gate and replay scripts") or list every file.

### docs-user-11: CHANGELOG preamble still says "Semantic Versioning once released"
- severity: low
- confidence: high
- category: stale-doc
- file: CHANGELOG.md:3
- summary: Sixteen releases in, "once released" reads as if nothing has shipped.
- scenario: Cosmetic; a reader of the top of the file gets the wrong impression of the project's state.
- evidence: "All notable changes are recorded here (Keep a Changelog format; Semantic Versioning once released)."
- fix: "All notable changes are recorded here, in the Keep a Changelog format, under Semantic Versioning."

### docs-user-12: soulseek.md requires slskd 0.26.0 for external mode, but nothing in the code checks a version
- severity: low
- confidence: low
- category: stale-doc
- file: website/src/content/docs/sources/soulseek.md:86
- summary: "It must be version 0.26.0 or newer" is stated as a requirement, yet the only 0.26.0 mentions in src/ are comments about which slskd the log-watcher texts were verified against; no connection test or health check compares the version.
- scenario: If an older slskd works, the sentence turns users away for nothing; if it does not, the connection test gives no hint. Confirm by reading the external-mode connection test (src/Wondarr.Api/Soulseek/SoulseekController.cs) and the API calls that changed between slskd releases.
- evidence: soulseek.md:86 "It must be version 0.26.0 or newer."; `grep -rn "0\.26" src/` matches only `SlskdApplicationState.cs:31`, `SlskdLogWatcher.cs:23` and `SlskdTransferModels.cs:50`, all comments.
- fix: Either say what needs 0.26.0 ("the API Wondarr uses appeared in 0.26.0") or drop the number.

### docs-user-13: Navigation paths unbolded in the changelog (R4), 10 instances
- severity: low
- confidence: high
- category: style
- file: CHANGELOG.md:57
- file: CHANGELOG.md:82
- file: CHANGELOG.md:198
- file: CHANGELOG.md:225
- file: CHANGELOG.md:302
- summary: R4. The newest entries bold a path (line 72 "**Settings → General → Contribute with feedback**", line 145 "**Settings → Download clients → Clean up leftover folders**") while ten older lines leave the same kind of path plain: 57, 82, 198, 225, 302, 383, 390, 402, 411, 415.
- scenario: The same document shows two conventions for one thing.
- evidence: :57 "on Settings → Reference libraries, lists"; :82 "on Settings → General (the AcoustID"; :198 "**Cancel** in Tasks → Recent commands"; :225 "a new type under Settings → Notifications"; :415 "(System → Backup → Back up now)".
- fix: Bold each: "**Settings → Reference libraries**", "**Tasks → Recent commands**", "**System → Backup → Back up now**", and so forth.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill; the bold-label convention is repository practice, so this is an optional consistency fix.

### docs-user-14: Navigation paths unbolded on three website pages (R4), 5 instances
- severity: low
- confidence: high
- category: style
- file: website/src/content/docs/library/import-lists.md:22
- file: website/src/content/docs/library/import-lists.md:23
- file: website/src/content/docs/library/import-lists.md:69
- file: website/src/content/docs/reference/troubleshooting.md:21
- file: website/src/content/docs/sources/plex.md:34
- summary: R4. The rest of each page bolds its paths ("**Settings → Soulseek**" at troubleshooting.md:14), these five do not.
- scenario: Same page, two conventions.
- evidence: import-lists.md:22 "API key (optional when Settings → General has one)"; import-lists.md:69 "next to the warning in Settings → Import lists"; troubleshooting.md:21 "Check **Shared folders** under Settings → Soulseek."; plex.md:34 "Settings → Library shows a dismissable reminder about this."
- fix: "**Settings → General**", "**Settings → Import lists**", "**Settings → Soulseek**", "**Settings → Library**".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill; optional consistency fix (the same pages bold the same paths elsewhere).

### docs-user-15: README navigation paths unbolded (R4), 8 instances
- severity: low
- confidence: medium
- category: style
- file: README.md:63
- file: README.md:64
- file: README.md:66
- file: README.md:72
- file: README.md:84
- summary: R4. The "first ten minutes" list bolds **Add**, **Activity**, **YouTube** and **Download clients** but writes "Settings → Soulseek", "Settings → Reference libraries → add your existing folder", "System → Updates" and "Settings → General → Contribute with feedback" plain. The product voice is allowed to be warmer, but the mix is inside one list. Medium confidence because the README is exempt from most of the house style.
- scenario: Cosmetic inconsistency on the page most visitors see.
- evidence: :64 "**Soulseek:** Settings → Soulseek, put in a Soulseek username and password."; :66 "Settings → Reference libraries → add your existing folder" (the arrow also joins a verb to a path, R10); :84 "under System → Updates ... under Settings → General → Contribute with feedback".
- fix: "**Settings → Soulseek**", "**Settings → Reference libraries**, then **Add reference library**", "**System → Updates**", "**Settings → General → Contribute with feedback**".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill; the README is Style 1 (Default) prose and its voice is the target, not a misstep.

### docs-user-16: Em-dashes in README and CONTRIBUTING prose (R9), 8 instances
- severity: low
- confidence: medium
- category: style
- file: README.md:7
- file: README.md:59
- file: README.md:74
- file: CONTRIBUTING.md:9
- file: CONTRIBUTING.md:52
- summary: R9. Five README lines (7, 59, 64, 74, 88) and three CONTRIBUTING lines (9, 52, 70) use an em-dash where a comma, colon or full stop does the job. Medium confidence: README product voice.
- scenario: Style only.
- evidence: README.md:59 "Unraid? There's a template — see [the Unraid guide]"; README.md:74 "The full manual — install, every setting, Plex, qBittorrent, SABnzbd, troubleshooting — lives at"; CONTRIBUTING.md:52 "read them before your first change" preceded by " — "; CONTRIBUTING.md:70 "**Never copy AGPL code** — Sockseek and slskd are re-implemented".
- fix: README:59 "There is an Unraid template: see [the Unraid guide]." README:74 "The full manual (install, every setting, Plex, qBittorrent, SABnzbd, troubleshooting) lives at ..." CONTRIBUTING:52 "The binding rules are in `docs/build/CODING_STANDARDS.md`. Read them before your first change." CONTRIBUTING:70 "**Never copy AGPL code.** Sockseek and slskd are re-implemented ..."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned (Style 3 says so in words, and Style 1's own examples use them).

### docs-user-17: Em-dashes in website and changelog prose (R9), 4 instances
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/guides/end-to-end.md:26
- file: CHANGELOG.md:240
- file: CHANGELOG.md:408
- file: CHANGELOG.md:422
- summary: R9. Every other dash on the site sits inside a quoted UI string ("Stored — type to replace", "Saved — slskd restarts to apply it") or a changelog version heading, which the rules exempt; these four are prose.
- scenario: Style only.
- evidence: end-to-end.md:26 "run **Scan Library Files** on it once — Plex ignores Wondarr's partial scans for a section that has never been scanned."; CHANGELOG.md:240 "Plex sees it as moved — Plex does not document whether"; CHANGELOG.md:408 "synced import lists — CSV/Exportify"; CHANGELOG.md:422 "Phase 0 — skeleton, foundations".
- fix: end-to-end.md:26 "run **Scan Library Files** on it once: Plex ignores Wondarr's partial scans for a section that has never been scanned." CHANGELOG:240 "Plex sees it as moved. Plex does not document whether ..."; :408 "synced import lists: CSV/Exportify ...".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned.

### docs-user-18: Arrow chain and "e.g." in CONTRIBUTING (R10, R7)
- severity: low
- confidence: high
- category: style
- file: CONTRIBUTING.md:54
- file: CONTRIBUTING.md:63
- summary: R10: "One task → one branch → one reviewable diff" is an arrow chain in prose. R7: "e.g. `fix/sabnzbd-renamed-file`".
- scenario: Style only.
- evidence: :54 "- One task → one branch → one reviewable diff; never mix a refactor with a feature."; :63 "Branch names are `<type>/<slug>`, e.g. `fix/sabnzbd-renamed-file`."
- fix: "One task is one branch and one reviewable diff; never mix a refactor with a feature." / "Branch names are `<type>/<slug>`, for example `fix/sabnzbd-renamed-file`."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: neither arrows nor "e.g." are mentioned.

### docs-user-19: "and so on" on two library pages (R7)
- severity: low
- confidence: high
- category: style
- file: website/src/content/docs/library/adding-songs.md:10
- file: website/src/content/docs/library/quality-and-upgrades.md:55
- summary: R7. Both use "and so on" to close a list.
- scenario: Style only.
- evidence: adding-songs.md:10 "version flags (live, remix and so on)"; quality-and-upgrades.md:55 "version mismatch (live, remix and so on)".
- fix: "version flags such as live or remix" / "version mismatch (a live or remix take)".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: "and so on" is not mentioned.

### docs-user-20: Filler words "simply" and "just" (R1), 3 instances
- severity: low
- confidence: high
- category: style
- file: website/src/content/docs/library/managing-the-library.md:57
- file: website/src/content/docs/library/import-lists.md:12
- file: CHANGELOG.md:291
- summary: R1. "simply" twice and a parenthetical "just" that adds nothing.
- scenario: Style only.
- evidence: managing-the-library.md:57 "A source that did not answer is simply left out."; import-lists.md:12 "A line of plain text with no artist and title (just `Hallelujah`)"; CHANGELOG.md:291 "A request that fails it simply needs a login."
- fix: "A source that did not answer is left out." / "A line with no artist and title (`Hallelujah`)" / "A request that fails it needs a login."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: "simply" and "just" are not named; the nearest Style 1 rule is "don't explain what's obvious", so this is optional.

### docs-user-21: Future tense where the house style uses the present (R1), 5 instances
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/reference/configuration.md:74
- file: website/src/content/docs/operations/tasks-backups-logs.md:23
- file: website/src/content/docs/library/adding-songs.md:23
- file: website/src/content/docs/sources/sabnzbd.md:20
- file: website/src/content/docs/reference/troubleshooting.md:71
- summary: R1. The site is otherwise in the present tense; these five sentences say "will".
- scenario: Style only.
- evidence: configuration.md:74 "Wondarr will not accept values above them."; tasks-backups-logs.md:23 "**Next run** is the time the scheduler will start the task."; adding-songs.md:23 "so the Compact library task will not scatter them"; sabnzbd.md:20 "It will not grab that release for that song again."; troubleshooting.md:71 "Peers that have the whole release will send that piece".
- fix: "Wondarr refuses values above them." / "the time the scheduler starts the task" / "does not scatter them" / "It never grabs that release for that song again." / "Peers that have the whole release send that piece".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: tense is not mentioned.

### docs-user-22: Agentless passives (R5), 3 instances
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/getting-started/first-run.md:55
- file: website/src/content/docs/reference/troubleshooting.md:52
- file: website/src/content/docs/operations/tasks-backups-logs.md:31
- summary: R5. Three sentences hide who does the thing.
- scenario: Style only.
- evidence: first-run.md:55 "Two profiles are seeded, under **Settings → Quality profiles**:"; troubleshooting.md:52 "A rejected file is blocklisted for that song and the next candidate is tried."; tasks-backups-logs.md:31 "A **scheduled** backup is made weekly".
- fix: "Wondarr starts with two profiles, under **Settings → Quality profiles**:" / "Wondarr blocklists a rejected file for that song and tries the next candidate." / "The Backup task makes a **scheduled** backup weekly".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: the passive voice is not mentioned.

### docs-user-23: Parentheticals that carry a whole clause (R11), about 20 on the site
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/getting-started/first-run.md:28
- file: website/src/content/docs/library/adding-songs.md:53
- file: website/src/content/docs/library/reference-libraries.md:30
- file: website/src/content/docs/sources/plex.md:12
- file: website/src/content/docs/reference/troubleshooting.md:8
- summary: R11. A rough count finds about twenty parentheses holding a full clause or sentence; five examples. The changelog does the same on most long entries but is a record, so it is left out of the count.
- scenario: Style only.
- evidence: first-run.md:28 "(The login can also be set through the API: `PUT /api/v1/auth/user` with `{"username": …, "password": …}` and the API key.)"; adding-songs.md:53 "(if too many searches already wait, it ends at once with "No free download slot; the song stays wanted for the next Missing Search")"; reference-libraries.md:30 "(whichever is smaller, and never fewer than 3 files)"; plex.md:12 "(for a server behind a reverse proxy, or one plex.tv does not list, for example `http://plex.lan:32400`)"; troubleshooting.md:8 "(**Tasks → Check Health → Run now** runs them at once)".
- fix: Make each its own sentence: "The login can also be set through the API: ..." / "If too many searches already wait, it ends at once with ..." / "The checks run every 15 minutes; **Tasks → Check Health → Run now** runs them at once."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill as such; Style 1's "Short sentences. Often fragments." is the nearest rule, so splitting the longest ones is optional.

### docs-user-24: Headings that are sentences or imperatives rather than noun phrases (R13), 12 instances
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/sources/youtube.md:8
- file: website/src/content/docs/sources/soulseek.md:8
- file: website/src/content/docs/sources/sabnzbd.md:22
- file: website/src/content/docs/reference/troubleshooting.md:33
- file: README.md:30
- summary: R13 (medium confidence rule). Twelve headings are sentences or commands: youtube.md "## It is off by default"; soulseek.md "## Use a dedicated account", "## Use your own slskd"; sabnzbd.md "## Create the category first"; unraid.md "## Use one `/data` share"; troubleshooting.md "## A song stays wanted", "## A file was rejected", "## Hard links failing", "## A torrent stops at 98 or 99 %"; import-lists.md "### Songs wait for the reference scan"; README "## Get it running", "## Your first ten minutes". The troubleshooting ones read as symptoms and may be intentional.
- scenario: Style only.
- evidence: youtube.md:8 "## It is off by default"; soulseek.md:8 "## Use a dedicated account"; sabnzbd.md:22 "## Create the category first".
- fix: "## Off by default", "## A dedicated account", "## Your own slskd", "## The category", "## One `/data` share".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: heading shape is not mentioned, and Style 1 is happy with "Get it running".

### docs-user-25: Unexplained abbreviations MBID, CBR and VBR (R12)
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/library/import-lists.md:25
- file: website/src/content/docs/sources/youtube.md:22
- summary: R12. "MBID" appears once on the site, with no expansion; "CBR or VBR" is not spelled out anywhere (conversion.md says "Constant bitrate" and "LAME quality (VBR)" without tying the two).
- scenario: A reader new to MusicBrainz does not know MBID means a MusicBrainz id.
- evidence: import-lists.md:25 "A listenbrainz.org playlist link, or its MBID"; youtube.md:22 "the codec (**aac**, **mp3** or keep the original Opus), CBR or VBR, the bitrate (kbps)".
- fix: "or its MusicBrainz id" / "constant or variable bitrate".
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill for docs (only Style 2 level 5, formal email, spells out acronyms); optional clarity fix.

### docs-user-26: "Slskd" capitalised (R8)
- severity: low
- confidence: high
- category: style
- file: website/src/content/docs/reference/troubleshooting.md:83
- summary: R8. The project spells its name "slskd", also at the start of a sentence, everywhere else on the site.
- scenario: Style only.
- evidence: "Slskd's output is in the same log."
- fix: "slskd's output is in the same log."
- outcome: confirmed on re-judgement: a product name spelled against its owner's spelling is an error in any style.

### docs-user-27: "a 60 second window" needs a hyphen (R8)
- severity: low
- confidence: high
- category: style
- file: website/src/content/docs/library/quality-and-upgrades.md:69
- summary: R8. A compound modifier before a noun takes a hyphen.
- scenario: Style only.
- evidence: "Wondarr decodes a 60 second window of the file".
- fix: "Wondarr decodes a 60-second window of the file".
- outcome: confirmed on re-judgement: grammar, not style.

### docs-user-28: "dismissable" (R8)
- severity: low
- confidence: medium
- category: style
- file: website/src/content/docs/sources/plex.md:34
- summary: R8. The usual spelling is "dismissible".
- scenario: Style only.
- evidence: "Settings → Library shows a dismissable reminder about this."
- fix: "**Settings → Library** shows a reminder about this, which you can dismiss."
- outcome: confirmed as a minor spelling nit on re-judgement; optional.

### docs-user-29: "Configuration → feedback" link text uses an arrow for a section reference (R10)
- severity: low
- confidence: medium
- category: style
- file: CHANGELOG.md:89
- summary: R10 allows arrows for UI navigation only; this one joins a page name to a section heading.
- scenario: Style only.
- evidence: "See [Configuration → feedback](https://wulfftech.github.io/wondarr/reference/configuration/#feedback)."
- fix: "See [the `feedback` section of Configuration](...)."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: arrows are not mentioned.

### docs-user-30: DockSTARTer README closes with "please visit our support page" (R1, R14)
- severity: low
- confidence: low
- category: style
- file: packaging/dockstarter/wondarr/README.md:26
- summary: "please" (R1) and "our" (R14) in a sentence that reads as DockSTARTer's own boilerplate, which the upstream template may require word for word; low confidence for that reason.
- scenario: Style only, and only if the upstream catalogue does not mandate the sentence.
- evidence: "If you need assistance setting up this application please visit our [support page](https://dockstarter.com/basics/support/)."
- fix: Keep it if DockSTARTer requires it; otherwise "DockSTARTer's own help is on its [support page](...)."
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill, and the sentence is DockSTARTer's own boilerplate.

### docs-internal-22: MATCHING_ENGINE §6.2 names the container limit `max_container_size`; the key is `search.max_container_size_mb`
- severity: low
- confidence: high
- category: stale-doc
- file: docs/architecture/MATCHING_ENGINE.md:40
- file: docs/architecture/CONFIG_EXAMPLES.md:85
- summary: (S1) `SearchOptions` binds `search.max_container_size_mb` (CONFIG_EXAMPLES shows `max_container_size_mb: 1500`); the rejection table calls it `max_container_size` with a default of "1.5 GB".
- scenario: A contributor greps for `max_container_size` to find the validator and gets only the doc.
- evidence: "a usenet post that downloads whole ... above `max_container_size` (default 1.5 GB)"
- fix: "above `search.max_container_size_mb` (default 1500)".

### docs-internal-23: The Singles pseudo-album is titled "Singles", not "{Artist} – Singles"
- severity: low
- confidence: high
- category: stale-doc
- file: docs/adr/0007-album-layer-and-fewest-albums-policy.md:9
- file: docs/architecture/LIBRARY_OUTPUT.md:35
- file: docs/architecture/LIBRARY_OUTPUT.md:12
- summary: (S4) ADR-0007 and §7.3 item 2 name the pseudo-album `{Artist} – Singles`; §7.1 says "its folder is plain `Singles` in every preset", and `AlbumPolicyEngine.cs:22` has `PseudoAlbumTitle = "Singles"`.
- scenario: A user expects an "Adele – Singles" album in Plex and finds "Singles".
- evidence: "strays in an `{Artist} – Singles` pseudo-album" / "Everything by an artist goes into `{Artist} – Singles`" / "so its folder is plain `Singles` in every preset"
- fix: Use `Singles` (under the artist folder) in ADR-0007 and §7.3, with a note that the ADR's original wording was changed when the engine was built.

### docs-internal-24: ARCHITECTURE §5.3 and §5.6 name the import-list policies differently
- severity: low
- confidence: high
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:87
- file: docs/architecture/ARCHITECTURE.md:203
- summary: (S4) §5.3 says "add-only | add-and-unmonitor-removed | mirror"; §5.6 and the code use `AddOnly`, `AddAndUnmonitor`, `Mirror`.
- scenario: A reader searching the code for `add-and-unmonitor-removed` finds nothing.
- evidence: "# sync policy: add-only | add-and-unmonitor-removed | mirror" vs "`AddOnly` leaves it, `AddAndUnmonitor` unmonitors it, `Mirror` deletes it"
- fix: Use the code's names in §5.3.

### docs-internal-25: ADR-0010 lists a `calendar` endpoint that was never built
- severity: low
- confidence: medium
- category: stale-doc
- file: docs/adr/0010-arr-conventions-and-port.md:9
- summary: (S1) The decision lists `calendar` among the Lidarr-shaped endpoints; `docs/api/openapi.json` and the controllers have no `/api/v1/calendar`. The other named endpoints (`queue`, `queue/status`, `wanted/missing|cutoff`, `history`, `blocklist`, `command`, `release`, `release/push`, `/ping`, `system/status`, `health`) exist.
- scenario: A dashboard author expecting a Lidarr calendar widget to work gets a 404.
- evidence: "Lidarr-shaped `queue`, `queue/status`, `wanted/missing|cutoff`, `history`, `blocklist`, `command`, `release`, `release/push`, `calendar`."
- fix: Amend the ADR: `calendar` was not built (a song has no air date), and the Lidarr-compatible surface is the list in ARCHITECTURE §5.6 "Ecosystem compatibility".

### docs-internal-26: ARCHITECTURE §5.1 diagram shows clients and sources ADR-0009 puts on the backlog
- severity: low
- confidence: high
- category: stale-doc
- file: docs/architecture/ARCHITECTURE.md:28
- file: docs/adr/0009-torrent-usenet-scope.md:9
- summary: (S4) The component diagram wires Transmission, Deluge/rTorrent, NZBGet, Spotify and "Wikidata/fanart opt." to the plugin interfaces; ADR-0009 says Transmission/Deluge/rTorrent/NZBGet "are backlog", ADR-0012 makes the Spotify importer optional and owner-only, and nothing in `src/` references fanart or Wikidata.
- scenario: A reader of the diagram expects a Transmission client row in Settings.
- evidence: "qBittorrent     Spotify/Deezer   Plex (scan, ... Transmission ... Deluge/rTorrent ... SABnzbd/NZBGet" / ADR-0009: "Transmission/Deluge/rTorrent/NZBGet and per-indexer tracker policies are backlog."
- fix: Draw only what is built (qBittorrent, SABnzbd, Prowlarr/Torznab/Newznab, Gazelle; Deezer, YouTube Music, CSV, Last.fm, ListenBrainz) and list the backlog in a sentence.

### docs-internal-27: phase2-scenario.py's usage omits the --reserve switch phase4-scenario.py relies on
- severity: low
- confidence: high
- category: stale-doc
- file: scripts/phase2-scenario.py:20
- file: scripts/phase4-scenario.py:5
- summary: (S1) The Phase 4 scenario builder says "`scripts/phase2-scenario.py --reserve` keeps such songs in the library"; phase2-scenario.py has `--reserve` and `--reserved-out` (argparse lines 110-113) but its own docstring and usage line list only `--keep` and `--dropped-out`.
- scenario: A contributor running the Phase 2 scenario by hand from its usage line never reserves songs, and the Phase 4 scenario has nothing to work with.
- evidence: "usage: scripts/phase2-scenario.py --url http://localhost:1077 --api-key KEY --out /tmp/scenario.json [--keep 20]\n                                  [--dropped-out /tmp/dropped.json]"
- fix: Add `[--reserve N --reserved-out FILE]` to the usage and one sentence on what it reserves.

### docs-internal-28: STACK.md carries evaluation notes that were settled long ago
- severity: low
- confidence: high
- category: stale-doc
- file: docs/architecture/STACK.md:24
- file: docs/architecture/STACK.md:26
- summary: (S1) "AcoustID.NET optional" and "the `YTMusicAPI` NuGet package is evaluated first" describe choices still open; `Directory.Packages.props` has neither package and the InnerTube client and `fpcalc` runner are built.
- scenario: A contributor reads the row and spends time evaluating a package the project already decided against.
- evidence: "`fpcalc` subprocess + AcoustID HTTP (Lidarr's `FingerprintingService` is directly portable); AcoustID.NET optional" / "the `YTMusicAPI` NuGet package is evaluated first"
- fix: State the outcome: own `fpcalc` runner and AcoustID client; own InnerTube client, no NuGet wrapper.

### docs-internal-29: R7 "e.g." and "etc." in finished prose (ARCHITECTURE, LIBRARY_OUTPUT, DEPLOYMENT, CONFIG_EXAMPLES, fixtures README)
- severity: low
- confidence: high
- category: style
- file: docs/architecture/ARCHITECTURE.md:56
- file: docs/architecture/ARCHITECTURE.md:122
- file: docs/architecture/LIBRARY_OUTPUT.md:65
- file: docs/architecture/LIBRARY_OUTPUT.md:82
- file: docs/architecture/DEPLOYMENT.md:65
- file: docs/architecture/CONFIG_EXAMPLES.md:29
- file: tests/fixtures/README.md:10
- summary: (R7) Ten instances across five files; the code-block comments in ARCHITECTURE.md:71 and :92 and CONFIG_EXAMPLES.md:72 are included.
- scenario: Style only.
- evidence: "Plex partial scan, webhooks, Discord, etc." → "Plex partial scan, webhooks, Discord and the other notifiers." / "`type` (e.g. `library`)" → "`type` (for example `library`)" / "constant bitrate (e.g. 256/320 kbps)" → "a constant bitrate, for example 256 kbps or 320 kbps" / "`…:MusicBrainz Album Id` etc." → "`…:MusicBrainz Album Id` and the matching names" / "(the absolute http(s) URL of the user's slskd, e.g. `http://slskd:5030`)" → "(the absolute http(s) URL of the user's slskd, for example `http://slskd:5030`)"
- fix: Replace each with "for example" or spell the list out.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: "e.g." and "etc." are not mentioned.

### docs-internal-30: R1 filler words ("simply", "just", "Note:")
- severity: low
- confidence: high
- category: style
- file: docs/architecture/LIBRARY_OUTPUT.md:12
- file: docs/architecture/LIBRARY_OUTPUT.md:95
- file: docs/architecture/DEPLOYMENT.md:61
- file: docs/architecture/MATCHING_ENGINE.md:66
- file: tests/fixtures/acoustid/README.md:4
- summary: (R1) Five instances; the temporal "just" in LIBRARY_OUTPUT.md:48, :54 and ARCHITECTURE.md:203 ("just left the source") is not filler and is not counted.
- scenario: Style only.
- evidence: "the parenthesised group simply vanishes when the year is unknown" → "the parenthesised group vanishes when the year is unknown" / "the files stay where they are and are simply counted as owned" → "the files stay where they are and are counted as owned" / "live-reloadable settings (speed limits, listen port) just take effect" → "live-reloadable settings (speed limits, listen port) take effect without a restart" / "so the grab can select just that file" → "so the grab can select only that file" / "Note: a recording's `duration` is a **float** in seconds (`637.333`)." → "A recording's `duration` is a float in seconds (`637.333`)."
- fix: As shown.
- outcome: partly confirmed on re-judgement against the real /writing-style skill: the `Note:` instance is forward-caveating, which Style 1 bans ("Note that..."); "simply" and "just" are not a rule and are optional.

### docs-internal-31: R2 American spellings in prose ("catalog", "organize", "organizer")
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:149
- file: docs/build/REPO_LAYOUT.md:18
- file: docs/architecture/LIBRARY_OUTPUT.md:34
- file: docs/architecture/LIBRARY_OUTPUT.md:44
- summary: (R2) "catalog" and "organize" are plain prose. "the organizer" (five times in LIBRARY_OUTPUT.md: lines 12, 34, 44, 47, 124) is a common noun for the `LibraryOrganizer` component; confidence is medium because it doubles as the component's name.
- scenario: Style only.
- evidence: "The catalog reads the interval settings live" → "The catalogue reads the interval settings live" / "# probe, verify, tag, organize, place, notify" → "# probe, verify, tag, organise, place, notify" / "retags it through the organizer" → "retags it through the organiser (`LibraryOrganizer`)"
- fix: Use British spellings in prose and the class name in code font where the component is meant.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill; the skill itself and the rest of the repository write British spellings, so this is an optional consistency fix.

### docs-internal-32: R3 "~" for "about"
- severity: low
- confidence: high
- category: style
- file: docs/architecture/STACK.md:25
- file: docs/adr/0005-source-ladder.md:6
- file: tests/fixtures/media/README.md:17
- summary: (R3) Three instances.
- scenario: Style only.
- evidence: "its option set is ~3 years stale" → "its option set is about three years stale" / "capped at Opus ~160 kbps" → "capped at about 160 kbps Opus" / "a window shorter than ~3 s of a pure tone" → "a window shorter than about 3 s of a pure tone"
- fix: As shown.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: "~" is not mentioned.

### docs-internal-33: R6/R13 rhetorical-question heading and R14 "we" in MATCHING_ENGINE
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/LIBRARY_OUTPUT.md:14
- file: docs/architecture/MATCHING_ENGINE.md:66
- summary: (R6, R13, R14) One heading is a question; one bullet addresses the reader as "we".
- scenario: Style only.
- evidence: "### 7.2 Assessment: does Plex/Plexamp still need an album folder layer?" → "### 7.2 Why the Plexamp preset keeps an album folder layer" / "We cannot search for a recording, so we search for releases that contain it" → "An indexer cannot be searched for a recording, so Wondarr searches for releases that contain it"
- fix: As shown.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: Style 1 allows a wry question, and "we" is restricted only in Style 2's Casual Technical Outreach register.

### docs-internal-34: R9 em dashes as sentence breaks in ARCHITECTURE.md (24)
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:34
- file: docs/architecture/ARCHITECTURE.md:122
- file: docs/architecture/ARCHITECTURE.md:203
- file: docs/architecture/ARCHITECTURE.md:205
- file: docs/architecture/ARCHITECTURE.md:209
- summary: (R9) 24 em or en dashes used as parenthetical breaks (the quoted UI text "Completed — …" on line 51 is a quotation and not counted).
- scenario: Style only.
- evidence: "renders its `slskd.yml` from its own Soulseek settings — credentials, shares, slots; §9.5; an external-slskd mode is Phase 5" → "renders its `slskd.yml` from its own Soulseek settings: credentials, shares and slots (DEPLOYMENT.md, Managing the bundled slskd)." / "`filters` (json array of `{key, value, type}`) — a saved view of a list page" → "`filters` (json array of `{key, value, type}`): a saved view of a list page" / "a text match whose length is more than 10 s from the list's going to the review screen instead" (line 203, the dash-free clause is unclear) → "a text match whose length differs by more than 10 s from the list's goes to the review screen instead"
- fix: Replace each dash with a comma, a colon or a full stop.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned (Style 3: "em dashes aren't banned at all"; Style 1's examples use them). The sentence length these dashes sit in is covered by docs-internal-42.

### docs-internal-35: R9 em dashes as sentence breaks in DEPLOYMENT.md (27)
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/DEPLOYMENT.md:50
- file: docs/architecture/DEPLOYMENT.md:55
- file: docs/architecture/DEPLOYMENT.md:61
- file: docs/architecture/DEPLOYMENT.md:65
- file: docs/architecture/DEPLOYMENT.md:80
- summary: (R9) 27 dashes, most of them paired parenthetical dashes inside already long sentences.
- scenario: Style only.
- evidence: "The category (default `wondarr`) must exist in SABnzbd — its API cannot create one — and the client test checks" → "The category (default `wondarr`) must exist in SABnzbd, because its API cannot create one, and the client test checks" / "Wondarr sets any of these that an earlier version created with another mode to `0600` — including the `*.pre-restore*` copies a restore sets aside, which are also made `0600` the moment they are set aside — once, and logs one line" → "Wondarr sets any of these that an earlier version created with another mode to `0600`, once, and logs one line saying how many it changed. The `*.pre-restore*` copies a restore sets aside are included, and are made `0600` the moment they are set aside."
- fix: Split the sentences; use commas or colons.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned. Sentence length is covered by docs-internal-44.

### docs-internal-36: R9 em dashes as sentence breaks in LIBRARY_OUTPUT.md (31)
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/LIBRARY_OUTPUT.md:34
- file: docs/architecture/LIBRARY_OUTPUT.md:44
- file: docs/architecture/LIBRARY_OUTPUT.md:99
- file: docs/architecture/LIBRARY_OUTPUT.md:111
- file: docs/architecture/LIBRARY_OUTPUT.md:124
- summary: (R9) 31 dashes; line 34 alone has three pairs nested in one sentence.
- scenario: Style only.
- evidence: "that together contain all of the artist's owned recordings — a greedy set cover — but only use a real album when" → "that together contain all of the artist's owned recordings (a greedy set cover), but only use a real album when" / "The re-plan ignores stickiness except for **pinned** songs — an album the user chose explicitly (`PUT /api/v1/song/{id}/albumcontext`, `SongService.SetAlbumContextAsync`) — and keeps" → "The re-plan ignores stickiness except for **pinned** songs, those whose album the user chose explicitly (`PUT /api/v1/song/{id}/albumcontext`), and keeps"
- fix: Split the sentences; use commas, colons or parentheses once per sentence.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned. Sentence length is covered by docs-internal-44.

### docs-internal-37: R9 em dashes as sentence breaks in MATCHING_ENGINE.md (21)
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/MATCHING_ENGINE.md:37
- file: docs/architecture/MATCHING_ENGINE.md:48
- file: docs/architecture/MATCHING_ENGINE.md:49
- file: docs/architecture/MATCHING_ENGINE.md:66
- file: docs/architecture/MATCHING_ENGINE.md:68
- summary: (R9) 21 dashes; the scoring bullets use "**identity** — ..." as a lead-in dash.
- scenario: Style only.
- evidence: "- **identity** — title token-sort ratio (after removing version hints" → "- **identity**: title token-sort ratio (after removing version hints" / "either way round — MusicBrainz lists both), and so does one that" → "either way round, since MusicBrainz lists both), and so does one that"
- fix: Use a colon after the bold lead phrase and commas elsewhere.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned. Sentence length is covered by docs-internal-43.

### docs-internal-38: R9 em dashes in DISTRIBUTION.md (12) and the smaller files
- severity: low
- confidence: medium
- category: style
- file: docs/build/DISTRIBUTION.md:160
- file: docs/architecture/QUALITY_DEFINITIONS.md:29
- file: docs/adr/0008-quality-model.md:19
- file: docs/adr/0011-agentic-build-workflow.md:18
- file: NOTICE.md:1
- file: tools/FakeSlskd/README.md:6
- file: tests/fixtures/README.md:19
- file: tests/fixtures/ytdlp/README.md:7
- file: tests/fixtures/listenbrainz/README.md:3
- file: tests/gate/phase4-scenario.schema.md:52
- file: tests/gate/phase2-scenario.schema.md:61
- summary: (R9) DISTRIBUTION.md 12 (ranges such as "Low–medium" and "24–34 days" are not counted; the table dash "| — |" on line 239 stands for "none"), QUALITY_DEFINITIONS.md 5, ADR-0008 2, ADR-0011 1, NOTICE.md title 1, FakeSlskd README 5, fixtures README 4 and ytdlp README 5 and listenbrainz README 4 (bullet lead-in dashes), phase4 schema 4, phase2 schema 2.
- scenario: Style only.
- evidence: "# NOTICE — third-party code and attributions" → "# Notice: third-party code and attributions" / "- **Golden tests** — one JSON file per suite" → "- **Golden tests**: one JSON file per suite" / "- `download-success.stdout` — a successful download" → "- `download-success.stdout`: a successful download" / "Every `/api/v0` request needs `X-API-Key` ... or gets 401 — unless `web.authentication.disabled` is true" → "..., or gets 401, unless `web.authentication.disabled` is true" / "| linuxserver.io | ... | — |" → "| linuxserver.io | ... | none |"
- fix: Colons after bullet lead phrases; commas elsewhere; "none" in the table.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: em dashes are not banned.

### docs-internal-39: R10 arrows and "X → Y → Z" chains in prose
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:53
- file: docs/architecture/ARCHITECTURE.md:135
- file: docs/architecture/MATCHING_ENGINE.md:64
- file: docs/architecture/MATCHING_ENGINE.md:65
- file: docs/architecture/QUALITY_DEFINITIONS.md:19
- file: docs/architecture/QUALITY_DEFINITIONS.md:35
- file: docs/architecture/LIBRARY_OUTPUT.md:111
- file: docs/adr/0005-source-ladder.md:1
- file: docs/adr/README.md:11
- file: docs/adr/0012-inputs-reference-libraries-and-lists.md:9
- file: docs/build/CODING_STANDARDS.md:7
- file: docs/build/DISTRIBUTION.md:160
- file: tools/FakeSlskd/README.md:51
- file: tests/fixtures/lrclib/README.md:8
- file: tests/gate/phase2-scenario.schema.md:59
- summary: (R10) Arrows outside code blocks and outside UI navigation paths: ARCHITECTURE 3 chains, MATCHING_ENGINE 6 (query ladder and backoff chains), QUALITY_DEFINITIONS 5, LIBRARY_OUTPUT 3, ADR-0005 title and body plus the index row, ADR-0012 1, CODING_STANDARDS 2, DISTRIBUTION 1, FakeSlskd README 3, lrclib README 4 (request → status in a table), phase2 schema 1.
- scenario: Style only.
- evidence: "Chromaprint fingerprint → AcoustID → expected recording MBID" → "a Chromaprint fingerprint, looked up at AcoustID, must resolve to the expected recording MBID" / "1 h → 6 h → 24 h → 72 h → weekly (capped)" → "1 hour, then 6 hours, 24 hours, 72 hours and weekly (capped)" / "`artist title` ... → the same with diacritics stripped → title-only" → "`artist title` ..., then the same with diacritics stripped, then title-only" / "One task → one branch/worktree → one PR-sized diff" → "One task, one branch or worktree, one PR-sized diff" / "Source ladder Soulseek → YouTube Music → torrents/usenet" → "Source ladder: Soulseek, then YouTube Music, then torrents and usenet" / "`GET /api/get?...&duration=248` → 200 (synced and plain lyrics)" → a separate "Status" column with "200"
- fix: Write the order as a sentence ("first ..., then ..."); give tables a status column.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: arrows are not mentioned.

### docs-internal-40: R12 abbreviations the reader is never given (MB, LB, CAA, YT, sr, ToS, RG2)
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:30
- file: docs/architecture/ARCHITECTURE.md:47
- file: docs/architecture/ARCHITECTURE.md:71
- file: docs/architecture/ARCHITECTURE.md:116
- file: docs/adr/0001-unit-of-work-is-a-recording.md:9
- file: docs/adr/0005-source-ladder.md:9
- file: docs/build/REPO_LAYOUT.md:16
- file: docs/build/REPO_LAYOUT.md:37
- file: docs/architecture/LIBRARY_OUTPUT.md:88
- summary: (R12) "MB" for MusicBrainz (ARCHITECTURE 47, ADR-0001 9 and 13, REPO_LAYOUT 37), "LB" for ListenBrainz (diagram), "CAA" for Cover Art Archive (diagram, REPO_LAYOUT 16), "YT" (ARCHITECTURE 71, 76), "sr" for sample rate (ARCHITECTURE 116), "ToS" (ADR-0005 9), "RG2" (LIBRARY_OUTPUT 88) appear without expansion.
- scenario: Style only.
- evidence: "fall back to Deezer/Spotify for songs MB does not have" → "fall back to Deezer or Spotify for songs MusicBrainz does not have" / "duration, codec, bitrate, sr, depth, size" → "duration, codec, bitrate, sample rate, bit depth, size" / "the YouTube source must be enabled by the user (ToS disclaimer)" → "the user must enable the YouTube source and accept the terms-of-service notice" / "ffmpeg `ebur128`, RG2 −18 LUFS" → "ffmpeg `ebur128`, ReplayGain 2.0 reference of −18 LUFS"
- fix: Spell each out on first use per document.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill for docs (only Style 2 level 5 spells out acronyms); optional clarity fix.

### docs-internal-41: R4 UI labels not bold and a ">" navigation path
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/MATCHING_ENGINE.md:74
- file: docs/architecture/CONFIG_EXAMPLES.md:89
- file: docs/architecture/LIBRARY_OUTPUT.md:34
- file: docs/architecture/DEPLOYMENT.md:61
- summary: (R4) Navigation paths and UI labels appear in plain text, in quotes, or with ">" instead of bold labels joined by the arrow.
- scenario: Style only.
- evidence: "names the key and Settings → General → Metadata services" → "names the key and **Settings → General → Metadata services**" / "Also a switch on Settings > Download clients" → "Also a switch on **Settings → Download clients**" / "a \"Compact library\" task re-plans" → "the **Compact library** task re-plans" / "turning it off shows the leech-ban warning" with "**\"Share my library\" toggle**" → "the **Share my library** toggle"
- fix: As shown.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill; the bold-label convention is repository practice, so this is an optional consistency fix.

### docs-internal-42: R11/R13 paragraph-length bullets and nested parentheses in ARCHITECTURE.md
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:51
- file: docs/architecture/ARCHITECTURE.md:201
- file: docs/architecture/ARCHITECTURE.md:203
- file: docs/architecture/ARCHITECTURE.md:205
- file: docs/architecture/ARCHITECTURE.md:209
- summary: (R11, R13) Numbered step 6 of the lifecycle is one 508-word item; the four "**Lead.**" paragraphs of §5.6 and §5.7 run to 359, 552, 667 and 792 words each, with parentheses nested two deep ("(the way the Missing and Upgrade searches do before each song)", "(so adding more than about 19 songs one by one at a busy moment searches only the first ones straight away; a batch add is one `MissingSearch` instead)").
- scenario: Style only; the slot-wait rules, the cancel semantics and the yield cap in step 6 are three topics that a reader cannot find by heading.
- evidence: "6. **Grab.** Hand the candidate to its source's grab implementation ... The batch loops (`MissingSearch`, `UpgradeSearch`) wait for a slot before each song instead; ..." (one list item, 508 words)
- fix: Keep each lifecycle step to two or three sentences and move the slot wait, the cancel rules and the import-list trust rules into their own subsections with headings.
- outcome: partly confirmed on re-judgement against the real /writing-style skill: Style 1 asks for short sentences and no over-long intros, which a 508-word list item and 350 to 800-word paragraphs fail; the nested-parentheses count is not a rule. Keep the finding for the paragraph lengths.

### docs-internal-43: R11/R13 paragraph-length bullets and nested parentheses in MATCHING_ENGINE.md
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/MATCHING_ENGINE.md:36
- file: docs/architecture/MATCHING_ENGINE.md:37
- file: docs/architecture/MATCHING_ENGINE.md:66
- file: docs/architecture/MATCHING_ENGINE.md:68
- file: docs/architecture/MATCHING_ENGINE.md:74
- summary: (R11, R13) The Torznab/Newznab bullet is 839 words and its continuation paragraph 1 824 words, the longest paragraph in the repository; verification step 3 is 562 words; the "Artist mismatch" and "Title mismatch" table cells hold nested parentheses three deep ("(the file's own title, folded, equals ... (live, acoustic, radio edit…) ... (remaster, explicit, clean) ...)").
- scenario: Style only; the usenet quota, cool-down, trimming, cleanup and ownership rules in line 68 each deserve a heading.
- evidence: "  As built (P7-04): one source per protocol (`torznab`, `newznab`), each available when ... (1 824 words, one paragraph)"
- fix: Split §6.4's torrent/usenet text into subsections (Searching, Reading file lists, Usenet quotas and cool-downs, Grabs, Cleanup, Ownership, Download links, Bundling) with two or three sentences per idea; move the long table cells into bullets under the table.
- outcome: partly confirmed on re-judgement: Style 1's "Short sentences" and "over-long intros" rules apply to the 839-word bullet and the 1 824-word paragraph; the nested-parentheses count is not a rule. Keep the finding for the paragraph lengths.

### docs-internal-44: R11/R13 paragraph-length bullets and nested parentheses in LIBRARY_OUTPUT.md and DEPLOYMENT.md
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/LIBRARY_OUTPUT.md:34
- file: docs/architecture/LIBRARY_OUTPUT.md:99
- file: docs/architecture/LIBRARY_OUTPUT.md:103
- file: docs/architecture/DEPLOYMENT.md:61
- file: docs/architecture/DEPLOYMENT.md:108
- summary: (R11, R13) LIBRARY_OUTPUT: policy item 1 is 274 words, "A reference file that goes missing" 808 words in one paragraph, "Likely YouTube rips" 285 words; DEPLOYMENT §9.5's first paragraph is 550 words and the "Before migrating" bullet 163 words.
- scenario: Style only.
- evidence: "**A reference file that goes missing.** The scan marks a row `missing` ... (808 words, one paragraph)"
- fix: Split into headed subsections (The grace period, How a release deletes, Repoints and links, The mount guard) of a few sentences each.
- outcome: partly confirmed on re-judgement: Style 1's "Short sentences" rule applies to the 274 to 808-word paragraphs; the nested-parentheses count is not a rule. Keep the finding for the paragraph lengths.

### docs-internal-45: R5 agentless passives in ARCHITECTURE.md and LIBRARY_OUTPUT.md
- severity: low
- confidence: medium
- category: style
- file: docs/architecture/ARCHITECTURE.md:34
- file: docs/architecture/LIBRARY_OUTPUT.md:28
- summary: (R5) Two sentences hide the actor; the rest of these documents name Wondarr, the scan, the import or slskd.
- scenario: Style only.
- evidence: "YouTube is handled in-process via yt-dlp." → "Wondarr runs yt-dlp itself for YouTube." / "Any later re-assignment (compaction, policy change, adoption of a reference library) is performed as an explicit task" → "Wondarr performs any later re-assignment (compaction, a policy change, adoption of a reference library) as an explicit task"
- fix: As shown.
- outcome: rejected on re-judgement against the real /writing-style skill (2026-11-04): not a rule in the skill: the passive voice is not mentioned.


## Coverage and verified-clean notes from each reviewer

### api

#### Verified and not reported (so the next reviewer need not repeat it)

- API key: `X-Api-Key`, `?apikey=`, `Authorization: Bearer`; compared with `CryptographicOperations.FixedTimeEquals`; the key is registered with the log redactor and `Microsoft.AspNetCore` is at Warning, so hosting diagnostics never log the query string; Serilog request logging has `IncludeQueryInRequestPath = false`.
- Forms cookie: `WondarrAuth`, HttpOnly, SameSite=Lax (framework default), Secure when HTTPS or first `X-Forwarded-Proto` is https, 7-day sliding, stamp claim invalidates other sessions on credential change (LoginSessionTests cover it); the Forms cookie never authenticates `/api/v1/*` (fallback policy is scheme `API` only), so the API is not CSRF-able with the cookie.
- Local bypass: `LocalCallerRules` fails closed on any of twelve proxy headers, requires a private/loopback socket address and a LAN-style Host; a spoofed `X-Forwarded-For` makes the caller less trusted, never more. First login: `TrySetInitialAsync` holds a static semaphore and re-checks inside it, so two concurrent setups yield exactly one winner; `POST /login/setup` also checks `Origin`.
- Download token: HMAC over exact path + expiry, 60 s, process-random key, GET only, only the two download paths; log file names are allow-listed against `ListFiles()`, backup ids are confined with `GetFullPath` + directory-prefix check. No other endpoint turns request data into a file-system path (library roots/reference roots are validated in Core).
- Secrets in responses: download clients, indexers, notifications and import lists mask secret fields (`"********"`), a masked value on PUT/test keeps the stored secret only for the same target (`SecretReentryException` otherwise); Plex, Soulseek, metadata, feedback and YouTube resources carry only "set" flags. Release resources expose `RemotePath` (title/filename/video id), not download URLs.
- SignalR: one hub, no server-callable methods, `RequireAuthorization("SignalR")` (key via header or `?access_token=`), broadcasts command/queue/health/update resources only.
- SPA: `index.html` hidden from static files and served only through the fallback with `Cache-Control: no-cache`; assets immutable; fallback behind the UI policy; static-file middleware rejects traversal; `__URL_BASE__` replacement uses a regex-validated value. `UrlBaseMiddleware` redirect is always under the configured base (no open redirect); slskd's webhook URL includes the base.
- Frame protection and the absence of CORS are as documented; `/docs` is anonymous by design and carries no data.

#### Coverage

Read fully: Program.cs; Authentication/** (all 17 files); Middleware/** (3); Extensions/** (4); Frontend/FrontendApplicationExtensions.cs, FrontendServiceCollectionExtensions.cs, IndexHtmlHidingFileProvider.cs, IndexHtmlProvider.cs; SignalR/** (9); Logs/LogController.cs; SystemInfo/BackupController.cs, BackupResource.cs, DownloadTokenController.cs, SystemController.cs, TaskController.cs; Commands/** (2); Release/ReleasePushController.cs, ReleaseController.cs; Slskd/SlskdWebhookController.cs; DownloadClients/** (2); Plex/** (2); Soulseek/** (2); YouTube/YouTubeSettingsController.cs; Metadata/MetadataSettingsController.cs; Importing/** (2); Profiles/LibraryPreviewController.cs; Songs/PreviewController.cs; Paging/PagingResource.cs; Health/HealthController.cs; Updates/UpdateController.cs; Feedback/FeedbackController.cs. Supporting Core files read fully: Authentication/CredentialStore.cs, ICredentialStore.cs, StoredCredentials.cs; Configuration/ServerOptions.cs; Notifications/NotificationSecrets.cs; Paging/PagingSpec.cs; Backup/BackupService.cs (Resolve/OpenRead); Logging/LogFileReader.cs (OpenRead, ReadEntriesAsync).

Skimmed (routes, path/secret-related lines only): Indexers/**, Notifications/NotificationController.cs and resources, ImportLists/** (controller, resources mapper), Release/ReleaseResource.cs, Metadata/MetadataSettingsResources.cs, YouTube/YouTubeSettingsResources.cs, Albums/AlbumController.cs, Songs/SongController.cs, SongMoveController.cs, SongEditorController.cs, SongConvertController.cs, ArtistController.cs, Profiles/LibraryController.cs, QualityProfileController.cs, QualityDefinitionController.cs, References/**, Queue/**, History/**, Wanted/**, Blocklist/**, Tags/**, CustomFilters/**, Frontend/WondarrDocumentTransformer.cs and EnumSchemaTransformer.cs (not opened; OpenAPI output checked only through docs/api/openapi.json and OpenApiSnapshotTests.cs). Tests: method names of all auth/security-related test classes; OpenApiSnapshotTests.cs read in full.

Not reached: resource records without security relevance (Albums/AlbumResources.cs, Songs/Song*Resource.cs, SongLookupIndex.cs, Queue/QueueResource.cs, History/HistoryResource.cs, Profiles/CompactPlanResource.cs, LibraryResource.cs, QualityProfileResource.cs, References/*Resources.cs, YouTube/YouTubeVideoController.cs and resources, YouTube/ServiceCollectionExtensions.cs, Health/HealthResource.cs, Updates/UpdateResources.cs, Logs/LogResource.cs, SystemInfo/SystemResource.cs, TaskResource.cs, Tags/TagController.cs body). No build or test run (per instructions).

### tests

#### Coverage

Ran in this session on Ubuntu with .NET SDK 10.0.401 and Node 22: `dotnet build -warnaserror` (0 warnings, 0 errors), `dotnet test` (Api 1 671 passed / 9 skipped; Sources 578 passed; Core 2 973 passed / 9 failed / 6 skipped), and in frontend/ `npm run lint`, `npm run typecheck`, `npm run format`, `npm test` (46 files, 435 tests, all green). `scripts/check-notice.py` was not run here (the docs-internal reviewer reads it). The 9 Core failures are the two findings above; no product defect was found behind them.

### style

#### Coverage

Scanned with grep over README.md, CONTRIBUTING.md, CHANGELOG.md, docs/**, website/src/content/docs/**, the packaging and fixture READMEs and tests/gate/*.md for: hedging words (18 hits, all the permissive or literal sense: "may show the web UI in a frame", "how long one tool may run", plus one genuine uncertainty on soulseek.md:23, which Style 1 allows); forward-caveating phrases (none); the negation formula (two real instances, style-01); "not only" and "isn't just" (literal uses only). Code-comment counts are from `grep` over `src/` and `frontend/src`. Not scanned: UI copy inside `frontend/src` components (Style 1 covers user-facing copy; a separate pass would read the label strings), and commit messages.

Re-judgement of the earlier style findings: every finding tagged R1 to R14 in docs-user.md and docs-internal.md now carries an `outcome:` line. In short: the em-dash, arrow, "e.g.", abbreviation, passive-voice, tense, heading-shape and bold-label findings are not rules in the real skill (em dashes are not banned at all); spelling and grammar findings stand; `Note:` is forward-caveating and stands; the paragraph-length findings stand under Style 1's "Short sentences" and "no over-long intros".

### core-import

#### Coverage

Read fully (every line, with line numbers):
- src/Wondarr.Core/Importing/: ImportService.cs, QueueTracker.cs, ImportRecovery.cs, SongFileLock.cs, LeftoverFolder.cs, QueueActions.cs, LibraryOrganizer.cs, CoverFetcher.cs, NamingValuesBuilder.cs
- src/Wondarr.Core/Organizer/: FilePlacer.cs, DiskOperations.cs (incl. PathRules), RecycleBin.cs, NamingTemplate.cs, NamingTokens.cs, RemotePathMapper.cs, ImportOptions.cs, SongFileRules.cs
- src/Wondarr.Core/Tagging/TagWriter.cs
- src/Wondarr.Core/Verification/DownloadVerifier.cs
- src/Wondarr.Core/Media/: ProcessRunner.cs, Fingerprinter.cs, MediaProbe.cs, AudioTranscoder.cs, FileConverter.cs, ReplayGainAnalyzer.cs, ReplayGainApplier.cs, SpectralAnalyzer.cs, ConvertFilesCommandHandler.cs, ApplyReplayGainCommandHandler.cs, MediaToolsOptions.cs
- src/Wondarr.Core/Compaction/: CompactExecutor.cs, CompactPlanner.cs, CompactMoveRules.cs, CompactResumeHostedService.cs, CompactLibraryCommandHandler.cs
- src/Wondarr.Core/Housekeeping/HousekeepingCommandHandler.cs
- src/Wondarr.Core/Backup/: BackupService.cs, PendingRestore.cs, ApplicationShutdown.cs, BackupCommandHandler.cs, BackupOptions.cs
- CHANGELOG.md 0.1.15 "Fixed" (the record-guard entries), docs/build/CODING_STANDARDS.md, CONTRIBUTING.md

Skimmed: src/Wondarr.Core/Media/CoverImageProcessor.cs (first 140 lines: dimension parsing and the in-bounds shortcut only), src/Wondarr.Core/Songs/SongService.cs (the `HasUnfinishedMoveAsync` region), src/Wondarr.Core/References/ReferenceAdopter.cs (grep for lock/KeepSource only), src/Wondarr.Core/Profiles/OutputPolicy.cs (the `OpusContainer` default only), the test files under tests/Wondarr.Core.Tests/{Importing,Organizer,Compaction} by test name, plus QueueTrackerTests.cs lines 440-625 and ImportServiceFileRecordTests.cs test names.

Not reached: src/Wondarr.Core/Organizer/AlbumPolicyEngine.cs, AlbumPolicyModels.cs, AlbumFolderConsistency.cs, LibrarySidecarOptions.cs, NamingValues.cs, NativeMethods.cs; src/Wondarr.Core/Tagging/TagReader.cs, TagSet.cs, CoverImage.cs; src/Wondarr.Core/Importing/TagSetBuilder.cs, ImportSettingsService.cs, QueueOptions.cs, QueueEvents.cs; src/Wondarr.Core/Media/SpectrumAnalysis.cs, MediaInfo.cs, MediaToolsHealthCheck.cs, the rest of CoverImageProcessor.cs; src/Wondarr.Core/Compaction/CompactPlan.cs, CompactMoveHealthCheck.cs; src/Wondarr.Core/Backup/IBackupService.cs, BackupItem.cs, BackupType.cs, KnownMigrations.cs. The reference-library side of the 0.1.15 record guards (src/Wondarr.Core/References/*, ImportLists/*) was not in scope and was not reviewed; the import side (`ReadFileForRecordAsync`, the `BEGIN IMMEDIATE` transaction via Microsoft.Data.Sqlite's default isolation plus the 60 s busy timeout) reads as correct for the cases the changelog names.

Verified-and-clean notes (no finding): ProcessRunner uses `ArgumentList` (no shell), drains both pipes before waiting, kills the tree on timeout and bounds the drain; every ffmpeg/fpcalc invocation passes the path as a separate argument after `-i`/positionally, and the transcoder uses `-n`; TagWriter writes to a same-directory temp copy, reads back, and renames over the original (atomic on one volume); NamingTemplate rejects absolute/drive/`..` templates and `CleanFileName` replaces `/` and `\`, with `PathRules.IsStrictlyInside` as a second gate in the placer and the recovery; the song lock ordering (library gate, then song lock) and the Importing/unfinished-move cross-checks between `ImportService` and `CompactExecutor.StageAsync` are consistent; PendingRestore journals every move and recovers or rolls back on the next start; the recycle-bin cleanup is gated by the marker file and the library/reference roots.

### core-data

#### Coverage

Read fully: src/Wondarr.Core/References/{ReferenceLibraryService, ReferenceScanner, ReferenceMatchService, ReferenceIdentifier, ReferenceOwnership, ReferenceAdopter, ReferenceReplacement, ReferenceMissingRules, ReferenceLibraryScanCommandHandler, ReferenceAdoptCommandHandler, ReferenceOptions, ReferenceLibraryGates, YouTubeRips}.cs; src/Wondarr.Core/Persistence/{WondarrDbContext, DomainModelConfiguration, DatabaseMigrator, DatabaseMigrationHostedService, DatabaseTimeouts, SettingsRepository, EntityBase, Job, CompactMoveConfiguration (FK part), NotificationDigestEntryConfiguration (FK part)}.cs, the raw-SQL migrations (AddReferenceReplacement, HardenImportListSync) and the diff of WondarrDbContextModelSnapshot.cs against 20261021143814_AddReferenceReplacement.Designer.cs (identical: the snapshot matches the last migration); src/Wondarr.Core/Songs/{SongService, SongQueries, SongListFilter}.cs; Albums/AlbumService.cs; Wanted/WantedService.cs; History/HistoryService.cs; Blocklisting/BlocklistService.cs; CustomFilters/CustomFilterService.cs; Paging/PagingSpec.cs; Domain/{ReferenceFile, ReferenceLibrary, MatchCandidate, Song, SongFile, DomainEnums}.cs; Housekeeping raw SQL (lines 230-335); ServiceCollectionExtensions.AddWondarrPersistence.

Checked and found sound (no finding): every sort key goes through a switch allow-list with an id tiebreak (no injection, stable paging); the LIKE term filter escapes `% _ \`; the 0.1.15 conditional `UPDATE/DELETE ... WHERE id AND source_type AND path` guards in ReferenceOwnership (Link/Repoint/Release, DecideAgain under the write lock) are consistent, assuming Microsoft.Data.Sqlite's default `BEGIN IMMEDIATE`; the scan does its `File.Exists` checks before the final transaction; WAL + `PRAGMA busy_timeout` are set; the "no file writes outside adopt" rule holds on every path I traced (scanner, identifier, match service, library delete, refile, compaction, convert, ReplayGain, library mover all skip `source_type = reference`; the adopter copies through `KeepSource`); the pre-migration backup gate; the queue-item/candidate cascade on song delete is covered by `A_song_with_only_finished_queue_items_is_deleted_with_them`.

Skimmed: Songs/{SongEditorService (DeleteAsync), SongRefiler, SongDetailsService (reference lookup)}.cs; Importing/ImportService.cs (record step, lines 850-940 and 1120-1160); Importing/LibraryOrganizer.cs (KeepSource staging, lines 195-300); Profiles/LibraryService.cs (root-change rule); HealthCheck/ReferenceMissingFilesHealthCheck.cs; Searching/SongSearchService.cs (IsHeldThroughReference); Api/References/ReferenceLibraryController.cs and ReferenceLibraryResources.cs; test names in tests/Wondarr.Core.Tests/{References,Songs,Persistence}.

Not reached: Domain/{AlbumContext, ImportList, QueueItem, SearchRun, CandidateRecord, CompactMove, Quality, Library, SeedData}.cs beyond grep; Albums/AddAlbumCommandHandler.cs and AlbumModels.cs; Songs/{SongLyricsService, SongFileSource, SongTags, ArtistService, RefileSongCommandHandler}.cs; the non-raw-SQL migration bodies; Backup/*; tests' bodies (only names and a few bodies read). No build or test was run.

### core-search

#### Coverage

Read fully: `src/Wondarr.Core/Searching/**` (SongSearchService, SlotWaiting, SlotWaitContext, SearchBackoff, SongSearchCommandHandler, MissingSearchCommandHandler, UpgradeSearchCommandHandler, ContainerBundler, SearchOptions, CancellationRules, SourceGrabFailure, ReleasePush); `src/Wondarr.Core/Jobs/**` (CommandExecutor, CommandQueue, ActiveCommands, CommandContext, ICommandHandler, CommandHandlerResolver, EnqueueCommandJob, ScheduledTaskService, ScheduledTaskCatalog, JobTableUpdater, the small records/enums and the Heartbeat/CheckHealth handlers); `src/Wondarr.Core/Decisions/**` (DecisionEngine, DecisionModels, CandidateTitleMatcher); `src/Wondarr.Core/Sources/` Candidate, ISourceProvider, SearchRunService, QueueService, SoulseekFilenameParser, SoulseekQuality, SceneFileName, VersionWords, SongAlbumNames, SoulseekUserService, ContainerMatcher, ProviderTestResult, DownloadProtocol; `src/Wondarr.Core/Profiles/` QualityProfileService, QualityDefinitionService and `Domain/Quality.cs` (cutoff/upgrade logic); `src/Wondarr.Core/Indexers/**`; `src/Wondarr.Core/DownloadClients/**`; `src/Wondarr.Core/Messaging/**`; `src/Wondarr.Core/Updates/**`; ARCHITECTURE §5.2 and §5.5; `docs/build/CODING_STANDARDS.md`, `CONTRIBUTING.md`.

Skimmed (only the parts the findings needed): `src/Wondarr.Sources.Slskd/SoulseekSourceProvider.cs` (search loop and its catches), `SoulseekCandidateMapper.cs`, `src/Wondarr.Core/Importing/QueueTracker.cs` (FailAsync/TryNextAsync), `QueueActions.cs` and `ImportService.cs` (GrabBestAsync call sites), `ReferenceReplacement.cs` signatures, `DomainModelConfiguration.cs` (queue_item unique index), `Domain/SearchRun.cs`, `Api/SignalR/CommandEventsRelay.cs`, the test names under `tests/Wondarr.Core.Tests/{Searching,Jobs,Decisions,Sources,Updates,Messaging}` and the bodies of the bundling, tier-ordering and MaxYielded tests.

Not reached: `src/Wondarr.Core/Profiles/` MoveSongsCommandHandler, SongLibraryMover, LibraryService, OutputPolicy (library/file moving, outside the quality-profile scope); the YouTube and Torznab source projects beyond the push handler call site; the torznab/newznab indexer and client type implementations; `MATCHING_ENGINE.md` beyond the tier and bundling passages.

Checked and found sound (no finding): the slot-gate hold/release/reacquire sequence and its `finally`; the user-cancel token being observed only in the slot wait, with the post-handle writes on `CancellationToken.None`; `WorkerPlace.Yield/Release/TakeBackAsync` accounting of `_yielded` and `_places`; the executor's catch ordering (stopping token before user cancel, exception filters before `cancelSource.Dispose()`); `CommandQueue` firing the cancel after releasing its gate and no handler of `CommandUpdatedEvent` re-entering the queue; `EventAggregator` isolating handlers; `SearchBackoff` ignoring `NoFreeSlot`/`Cancelled`/`Failed` and `SearchRun.Outcome` being nullable while a run is open; `SemanticVersion` precedence (numeric vs alphanumeric pre-release identifiers, `rc.10 > rc.9`) and `RunningVersion`'s develop/dev classification; `UpdateCheckService`'s gate, ETag, Retry-After and once-per-version announcement; `DecisionEngine` score ranges (no overflow, all clamped) and comparator consistency; `QualityProfile.MeetsCutoff/IsUpgrade` with unlisted qualities.

### core-external

#### Notes on things checked and found sound (not findings)

- Rate limits match CODING_STANDARDS: MusicBrainz 1.05 s (public host, mirror interval only honoured off musicbrainz.org), AcoustID floor 350 ms with the validator refusing >3/s, LRCLIB 200-5000 ms, iTunes 3 s, Deezer 120 ms, Last.fm 250 ms; every retry attempt passes the gate (handler inside the resilience pipeline). Retry-After is honoured and capped at 300 s for the metadata clients; for the Last.fm/ListenBrainz named clients `ShouldRetryAfterHeader` is uncapped but `HttpClient.Timeout` (30 s; 5 s for the song page) bounds the whole send. 429 is deliberately not retried for iTunes (403), Last.fm and ListenBrainz.
- Bodies are parsed before they are cached (captive-portal protection), cached unreadable rows are refetched, 404/no-data are cached as negatives. Deezer's in-200 error codes are unwrapped; AcoustID/LRCLIB/Last.fm/ListenBrainz parse with `JsonDocument`/`TryGet*` and never throw on odd JSON.
- Config: `config.yml` is written atomically to a 0600 temp file and renamed (`OwnerOnlyFile`, `ConfigFileWriter.Write`), matching docs/architecture/DEPLOYMENT.md; the API key is generated with `Guid.NewGuid` (CSPRNG) and compared with `CryptographicOperations.FixedTimeEquals` in the API; `APP__SECTION__KEY` wins over the file and the metadata/feedback/Soulseek settings services refuse to write a field the environment owns; `WONDARR_MEDIA_ROOT` defaults use keys that do bind (`Soulseek:DownloadsDir`, `Soulseek:IncompleteDir`, `YouTube:DownloadsDir`, `Import:ContainerStagingPath`).
- Passwords: PBKDF2-SHA512, 210k iterations, 16-byte random salt, dummy hash for unknown users, fixed-time compare.
- Redaction: configured API key, Soulseek password (Sources project), Plex account and server tokens, AcoustID key, Last.fm key, GitHub token, notification secrets (Discord webhook URL, Apprise stateless URLs/password, webhook password, SMTP password) are all registered; `System.Net.Http.HttpClient`/`Polly`/EF are held to Warning so request URIs are not logged. Plex tokens travel in `X-Plex-Token` only, redirects are disabled on both Plex clients and the notification client.
- Feedback: text, page and title are redacted with the registry plus the cleanser (home folders, `C:\Users`), capped, and the issue POST is never retried; 5 issues and 10 token checks per 10 minutes.
- Notifications: one send, one waited-out `Retry-After` of at most 60 s, delivery bounded by `DeliveryBudget`; failures log the notification name and a value-free reason.
- Import lists: dedupe by `ExternalId` (first occurrence wins), truncated/suspect reads never drive removals, Mirror re-checks under the song's file lock before deleting.
- Contract tests for AcoustID, Cover Art Archive, Deezer, iTunes and MusicBrainz live in tests/Wondarr.Sources.Tests/Metadata, so no client is untested.

#### Coverage

Read fully: src/Wondarr.Core/Metadata/Http/*, MetadataServiceCollectionExtensions.cs, MetadataCache.cs, ProviderBodies.cs, MetadataProviderException.cs, MetadataOptions.cs, PartialSearch.cs, MetadataSettingsService.cs, MusicBrainz/{MusicBrainzClient,MusicBrainzQuery,MusicBrainzModels}.cs, CoverArt/{CoverArtArchiveClient,CoverArtResolver}.cs, Deezer/{DeezerClient,DeezerQuotaHandler,DeezerModels}.cs, ITunes/ITunesClient.cs, AcoustId/{AcoustIdClient,AcoustIdOptions,AcoustIdKeyState}.cs, LastFm/{LastFmClient,LastFmBackOff,LastFmOptions}.cs; Identity/{IdentityResolver,LookupInput}.cs; Configuration/** (all 11 files); Authentication/** (all); Logging/** (all); Feedback/** (all); ImportLists/** except ReferenceScanWait.cs; Plex/** except PlexPathMapper.cs; Notifications/{NotificationService,NotificationDispatcher,NotificationSecrets,NotificationSecretRegistrar,NotificationHttp}.cs, Discord/{DiscordProvider,DiscordSettings}.cs, Webhook/{WebhookProvider,WebhookSettings}.cs, Apprise/{AppriseProvider,AppriseSettings}.cs, Email/{EmailProvider,SmtpSender,EmailSettings}.cs; Lyrics/** (all); HealthCheck/{HealthCheckService,FolderHealthCheck,ProtectedPathsHealthCheck,ImportListHealthChecks,DatabaseHealthCheck,RecycleBinHealthCheck,ReferenceMissingFilesHealthCheck}.cs.

Skimmed or grep-only: Metadata/IMusicBrainzClient.cs, Notifications/Email/EmailMessageBuilder.cs (confirmed `WebUtility.HtmlEncode` is used), Notifications/NotificationDigestService.cs (not read), Discord/Webhook/Apprise payload records, NotificationMessage/Events/Exception, src/Wondarr.Api/Songs/SongController.cs (lookup endpoint only), src/Wondarr.Api/ImportLists/ImportListResources.cs (masking only), src/Wondarr.Api/Authentication/ApiKeyAuthenticationHandler.cs (FixedTimeEquals only), test listings under tests/Wondarr.Core.Tests and tests/Wondarr.Sources.Tests/Metadata.

Not reached: Metadata/{VersionFlagParser,VersionFlags,TextFolding}.cs, Identity/{TextMatching,OriginalRecordingRanking,IdentityModels}.cs (pure scoring logic), Plex/PlexPathMapper.cs, ImportLists/ReferenceScanWait.cs, HealthCheck trivial files (IHealthCheck, HealthCheck record, ConfigFolder/LogFolder checks, HealthCheckCompletedEvent), and the test bodies themselves.

### sources

#### Coverage

Read fully (every line, with the numbered output):
- src/Wondarr.Sources.Slskd: SlskdHost.cs, SlskdConfigRenderer.cs, SlskdRuntimeSecrets.cs,
  SlskdRestartPolicy.cs, IProcessLauncher.cs, ProcessLauncher.cs, ProcessInspector.cs,
  SlskdLogWatcher.cs, SoulseekOptions.cs, SoulseekOptionsPostConfigure.cs,
  ServiceCollectionExtensions.cs, SoulseekSettingsService.cs, SoulseekSearchBudget.cs,
  SoulseekSearchHistory.cs, SlskdSearchRunner.cs, SoulseekQueryBuilder.cs, SlskdShareRescanner.cs,
  SlskdShareRescanTask.cs, SlskdDownloadCompletedHandler.cs, SlskdDownloadCompletedEvent.cs,
  SlskdDownloads.cs, SoulseekCandidateMapper.cs, SlskdClient.cs, SlskdSearchApi.cs,
  SlskdTransferApi.cs, SlskdEndpoint.cs, SoulseekSourceProvider.cs, SlskdExternalMonitor.cs,
  SlskdHealthCheck.cs, SoulseekStorageHealthChecks.cs, SlskdStatus.cs, SlskdApplicationState.cs,
  SlskdSearchModels.cs, SlskdTransferModels.cs.
- src/Wondarr.Sources.Torznab: every file under Clients/, Indexers/, Parsing/ and Searching/, plus
  ServiceCollectionExtensions.cs.
- src/Wondarr.Sources.YouTube: every file.
- Core helpers the above lean on: Configuration/OwnerOnlyFile.cs, Configuration/ProtectedPaths.cs,
  Searching/CancellationRules.cs, Media/ProcessRunner.cs (whole), Configuration/EnvironmentOverrides.cs
  (SectionKeys), Notifications/NotificationSecrets.cs (Read), Logging/ISecretRegistry.cs (first 150
  lines), Organizer/DiskOperations.cs (TryCreateHardLink), Searching/SongSearchService.cs (lines
  515-565 and the grab catch around 1250).

Skimmed (grep only): tests/Wondarr.Sources.Tests (file inventory; targeted greps for regex
timeouts, renamed-file cases, config-renderer password cases, ytdlp field names, qBittorrent
sessions), frontend/src/api/youtube.ts and YouTubeSettingsPage.tsx (field names and locking),
CHANGELOG.md 0.1.15, Directory.Packages.props.

Not reached: Core's SoulseekFilenameParser and ContainerMatcher (consumers in this area were read,
the parsers themselves were not), the test doubles/harnesses in tests/Wondarr.Sources.Tests (not
assessed for what they can catch beyond the gaps named above), docs/architecture/DEPLOYMENT.md §9.5
(the restart policy's claim that slskd reloads listen_port and speed limits live was not verified
against slskd's source), and slskd's own YAML reader (see sources-03).

Checked and found no defect worth reporting (so the next reviewer need not repeat): slskd.yml is
written owner-only via `OwnerOnlyFile.Create` + atomic rename; secrets never pass as arguments or
appear in log templates (API keys are registered with `ISecretRegistry` before every request, which
also covers `Microsoft.Extensions.Http`'s request-URI log line for SABnzbd's key-in-query and the
indexers' `apikey=`); child stdout/stderr are drained asynchronously in both `ProcessLauncher` and
`ProcessRunner`, and `ProcessRunner` kills the tree on timeout; yt-dlp receives the video id only
inside a fixed URL after `--`; the search budget's 30-per-240 s window, 2 outstanding and 5 s
spacing are enforced on a monotonic clock and persisted; qBittorrent file selection uses the
client's own `index` with priority 0/1 and version-gated `stopped`/`paused`, `start`/`resume`;
hard-link-then-copy staging and the usenet move are guarded by `UsenetJobFolder`; the NZB
cool-down, shared in-flight NZB downloads and the single retry after at most 5 s match the brief.

### frontend

#### Coverage

Read fully: `frontend/src/api/**` (all 30 modules except the generated `schema.d.ts`, which was only grepped for nullability), `frontend/src/app/{main.tsx,App.tsx,signalr.ts}`, `frontend/index.html`, `vite.config.ts`, `frontend/src/components/**` (all, including `song/*`), `frontend/src/pages/*.tsx` (Activity, AddSongs, AddAlbumTab, Library + `library/*`, MatchQueue, Queue, Settings, Song + `song/*`, Unresolved, Wanted, NotFound), every `pages/settings/*.tsx` and every `pages/system/*.tsx`. For the YouTube finding I also read `src/Wondarr.Core/Profiles/OutputPolicy.cs` (Parse/ToJson for `sampleRate`) and the update path in `src/Wondarr.Sources.YouTube/YouTubeSettingsService.cs`, and checked Mantine 9.7.3's `Tabs`/`TabsPanel` implementation in `node_modules` (keep-mounted default, React `Activity`).

Skimmed: the test files. I scanned every `*.test.ts(x)` for tests with at most one `expect` and read the bodies of the nine that looked weakest (App, signalr, LibraryPage, MatchQueuePage, SongPage, LibrarySettingsPage); the rest assert request bodies or rendered outcomes and were not read line by line. `test/fixtures.ts`, `test/helpers.tsx`, `test/setup.ts` and `theme/theme.ts` were not read.

Not reached: behaviour that needs the backend to confirm (whether `/song/{id}/details` caches upstream calls, whether the lookup ever returns `mbRecordingId: ""`, how a `urlBase` without a leading slash reaches the router's `basename`), and the XSS review found nothing to report: there is no `dangerouslySetInnerHTML`/`innerHTML` anywhere, release notes go through the React-only `Markdown` renderer with `safeHttpUrl`, Last.fm text through `plainText`, and all outbound links through `safeUrl`/`safeHttpUrl`. Every page I read has loading, empty and error states (via `QueryState`/`PagedTable` or inline), so nothing to report under item 4.

### ops

#### Coverage

Read fully:
- docker/Dockerfile, docker/docker-compose.yml, docker/DOCKERHUB.md, every file under docker/root/etc/s6-overlay/, scripts/fetch-slskd.sh, .dockerignore. The slskd checksum is really checked: `sha256sum -c -` under `set -eu`, against a constant pinned in the script (not fetched from the release page), and the Dockerfile verifies s6-overlay, Deno, yt-dlp and fpcalc the same way with `set -eux`; ffmpeg/ffprobe are digest-pinned. PUID/PGID are validated as integers, `chown` is scoped to `/config` with `-h` and `-xdev`, `/data` is untouched, `S6_BEHAVIOUR_IF_STAGE2_FAILS=2` ends the container if the oneshot fails. Everything before `s6-setuidgid` runs as root, which is the hotio/LSIO pattern.
- .github/workflows/ci.yml, release.yml, docs.yml, mirror-dockerhub.yml, record-gate-metadata.yml, .github/dependabot.yml. Every action is SHA-pinned with a version comment; top-level `permissions: contents: read` with `packages: write` only on `image`/`promote`, `contents: write` only on `github-release`, `pages`/`id-token` only on the docs deploy; no `pull_request_target`; no `${{ github.event.* }}` interpolated into `run:` (the only interpolations in `run:` are `needs.image.outputs.digest` and `matrix.arch`); the `tests-only` input behaves correctly for `workflow_call` and for push/PR (`inputs` is empty there, so `!inputs.tests-only` is true); `promote`'s `if:` has no status function, so `success()` is implied and a failed smoke matrix does skip it (ops-01 is about what the matrix covers, not about a bypass); GHA build caches are per-scope (`release` vs default) so a fork PR cannot poison `main`'s; `secrets: inherit` only reaches the in-repo mirror workflow.
- scripts/smoke-test.sh, safe-merge.sh, wsl-safe-merge.sh, remote-run.sh, check-notice.py, metadata-replay.py, phase1-gate.py, phase2-gate.py, phase2-scenario.py, phase3-gate.py, phase4-gate.py, phase4-scenario.py, phase5-gate.py, phase6-gate.py, phase7-gate.py, scripts/tests/test-check-notice.sh, test-safe-merge-parsing.sh and all seven sample logs. The safe-merge flaky re-run logic refuses the re-run whenever the named failures do not equal the summary totals, a project printed no summary, or a host aborted mid-run, and requires the re-run to have actually run at least as many tests as failed; I found no path that lets a real failure through. The gate scripts build their shell commands from smoke-test.sh's own strings and `shlex.quote` every path taken from the API. The remote-run lock (`flock` on fd 9, fd closed for the command so build servers cannot hold it) is correct.
- packaging/README.md, packaging/truenas/wondarr/** (app.yaml, item.yaml, ix_values.yaml, questions.yaml, templates/docker-compose.yaml, test_values/basic-values.yaml, README.md), packaging/cosmos/Wondarr/cosmos-compose.json and description.json, every packaging/dockstarter/wondarr/* file, unraid/wondarr.xml, unraid/README.md, ca_profile.xml. Ports (1077, 50300), volumes (/config, /data; DockSTARTer uses `WONDARR_MEDIA_ROOT=/storage` instead of /data, as its README says), env names (PUID/PGID/UMASK/TZ, `APP__SOULSEEK__USERNAME/PASSWORD`, `APP__SERVER__AUTH=external` which is a real `AuthenticationMethod` value) and image names (`ghcr.io/wulfftech/wondarr:latest`, `docker.io/wulfftech/wondarr`) agree with docker-compose.yml, README.md and DOCKERHUB.md; the Unraid icon and TemplateURL targets exist in the tree; the Unraid support/project URLs are the GitHub issues/repo pages. TrueNAS pins 0.1.14 by digest (two releases behind, which packaging/README.md's upkeep note accepts; the digest itself cannot be verified offline).
- tools/FakeSlskd: README.md, Program.cs, FakeSlskdOptions.cs, Scenario.cs, AcoustIdStubApp.cs. The AcoustID stub answers only fingerprints the fake itself generated (score 0.96) and `/v2/register` is loopback-only; a missing scenario file yields empty searches (gate fails, not passes). I found nothing that lets the Phase 2 gate pass without exercising the app.
- Directory.Build.props, Directory.Packages.props, global.json (10.0.401 / latestPatch, matching CONTRIBUTING.md and CI's `global-json-file`), .editorconfig, .gitattributes (`text=auto eol=lf` plus an explicit binary list; git's content sniffing covers the formats not listed), .gitignore (`src/Wondarr.Api/wwwroot/` is build output copied by the Dockerfile; `docs/api/openapi.json` is tracked and consumed by the Dockerfile and `npm run gen:api`; `tools/FakeSlskd/bin|obj` on disk are ignored, not tracked), .config/dotnet-tools.json, .env.example.
- Secrets sweep: the requested grep matched four files — frontend/src/pages/settings/FeedbackSettingsCard.test.tsx, tests/Wondarr.Api.Tests/FeedbackApiTests.cs, tests/Wondarr.Core.Tests/Feedback/FeedbackServiceTests.cs, tests/Wondarr.Core.Tests/Feedback/FeedbackSettingsServiceTests.cs — all `ghp_`-prefixed English-word placeholders (25–37 characters), not real tokens. tests/fixtures and tests/gate: the only e-mail-shaped strings are `@example.com` poster addresses in tests/fixtures/nzb/*.nzb; no Plex tokens, private IPs or key-shaped values found. The hardcoded `phase5-external-gate-key-…` in smoke-test.sh is a test fixture for the fake slskd.

Skimmed only:
- tools/FakeSlskd/FakeSlskdApp.cs, FakeSlskdState.cs, ContainerStubApp.cs, InnertubeStubApp.cs, PlexStubApp.cs, FakeYtDlp.cs, SearchText.cs, WebhookSender.cs, AudioGenerator.cs, SlskdConfiguration.cs (not opened; judged from the README and the files above).
- docs/architecture/DEPLOYMENT.md (grep for the container, port, healthcheck, PUID and promotion paragraphs only), website/src/content/docs/getting-started/install.md (the compose block and the Healthcheck section), reference/configuration.md (the `port`/`auth` rows), docs/build/DISTRIBUTION.md (the Cosmos section), README.md (the Docker section).
- src/Wondarr.Core/Configuration/ConfigFileInitializer.cs and ServerOptions.cs, src/Wondarr.Api/Extensions/ServiceCollectionExtensions.cs (Data Protection keys go to `/config/asp`, so the `HOME` issue in ops-12 does not affect auth cookies), src/Wondarr.Sources.Slskd/SlskdHost.cs line 908 — read only to confirm points above.

Not reached:
- The truenas/apps library source (needed to settle ops-11; the repository is not attached to this session).
- Running the image, the smoke test or any gate; nothing was executed beyond syntax checks.
- website/ build configuration and the Astro site itself beyond the pages named above; docs/adr/.

### docs-user

#### Verified and not reported

The following were checked against the code and match (no finding): every key, default and range in
reference/configuration.md (server, soulseek incl. `soulseek.search.*`, search, import, backup, queue,
reference, acoustid, lastfm, update, feedback, media, lyrics, log) against the options classes and
validators; the `APP__` binding (`AppEnvironmentVariablesConfigurationProvider`: `APP__SERVER__URL_BASE`
→ `server:urlbase`, list items as `__0`); `WONDARR_MEDIA_ROOT` and the four defaults it moves
(`WondarrPaths.MediaRootDefaults`); the Dockerfile healthcheck (`/ping`, 30 s), ports 1077/50300,
`WONDARR_CONFIG_DIR`, `COMPILARR_CONFIG_DIR`; the Unraid template's every default and its TemplateURL;
the DockSTARTer `WONDARR__PORT_50300` and `WONDARR_MEDIA_ROOT=/storage`; the Docker Hub mirror (runs for
main pushes and tags, so `develop` is on Docker Hub); README version 0.1.16 against CHANGELOG (no local
git tags exist to compare the `v0.1.16` release link against; the workflow tags are `v*`); all
`/wondarr/...` links and `#anchors` across the site, every sidebar slug in astro.config.mjs (22 pages +
index, all listed, none extra), README/CONTRIBUTING/packaging relative links and screenshots; API routes
(`/api/v1/auth/user`, `system/download-token`, `song/editor` PUT+DELETE, `importlist/{id}/trustnextread`,
`referencelibrary/{id}/ownedsongs`, `release/push`, `update`, `update/check`, `update/settings`, `/ping`,
`/initialize.json`, `/login`, `/login/setup`, `/docs`, `/docs/v1/openapi.json`, `artist`,
`wanted/missing`, `queue/status`); UI labels on Settings → General (Security/Login, Metadata services,
Updates, Contribute with feedback, "Stored — type to replace", key URLs), Library (layouts incl.
"Artist → Album", policies, naming tokens, Plexamp preset, Add/Convert/Compact/Delete library…,
ReplayGain), Soulseek (all fields, Connection card, "(set)", "Saved — slskd restarts to apply it", kicked
and rejected-login texts), YouTube (all fields and the consent dialog), Plex (all buttons and the Test
text), Download clients and Indexers (all schema fields), Notifications (event labels, Webhook, Discord,
Apprise, Email fields, Clear/Undo, Discard dialog), Import lists, Reference libraries, Match queue, Add
songs (tabs, 1000-line cap, buttons), Library filters and mass editor, Wanted tabs, song page tabs,
Tasks/Backup/Logs/Updates pages and the Update badge; scheduled task names and intervals; health
message texts; login limits (64/8 characters, 10 attempts a minute); tag limits (64 chars, 50 per song);
email limits (587, 5 to 1440, 30, 500 held, 25 covers, 500 KB); fake-lossless window (60 s) and
compaction cap (250); Plex scan batching (10 s, 60 s, 25 folders); housekeeping retention (7 d, 30 d,
a quarter free); reference identification scores (0.95, 0.89, 0.90) and the missing-file rules
(24 h, 10 %, 200, 3); qBittorrent 4.5 and the category `/` rule; quality names and groups.

#### Coverage

Read in full: README.md, CONTRIBUTING.md, CHANGELOG.md (all 439 lines), docker/DOCKERHUB.md,
unraid/README.md, packaging/README.md, packaging/dockstarter/wondarr/README.md,
packaging/truenas/wondarr/README.md, website/astro.config.mjs and all 23 pages under
website/src/content/docs/.

Read for verification (relevant parts only): the options classes under src/Wondarr.Core (Backup,
Configuration, Feedback, Importing, Logging, Lyrics, Media, Metadata, Organizer, References, Searching,
Updates) and in Wondarr.Sources.Slskd/YouTube; WondarrPaths.cs and AppEnvironmentVariablesConfigurationSource.cs
in full; ScheduledTaskCatalog.cs; the controllers' route attributes; the health-check message strings;
frontend/src/components/AppLayout.tsx, the settings pages, the Activity/Add songs/Library/Match/Wanted/
Song pages and the system pages (label strings only); docker/Dockerfile (ENV, EXPOSE, HEALTHCHECK),
docker/docker-compose.yml, unraid/wondarr.xml, the DockSTARTer compose fragments; .github/workflows/
release.yml and mirror-dockerhub.yml (triggers and tag rules).

Not verified: the "who counts as local" host-name details beyond what the changelog states
(configuration.md:41-43); the indexer priority range "1 to 50" and the YouTube Music playlist "100 pages"
cap (no constant found by grep either way); the TrueNAS/Cosmos packaging folders' YAML (out of scope);
docs/architecture and docs/adr contents (another reviewer's area).

### docs-internal

#### Coverage

Read in full: all 14 files under `docs/adr/`, all 7 under `docs/architecture/`, all 3 under `docs/build/`, `NOTICE.md`, `tests/fixtures/README.md` and the six `tests/fixtures/*/README.md`, both `tests/gate/*.schema.md`, `tools/FakeSlskd/README.md`, `website/README.md`, `.env.example`, `scripts/check-notice.py` (whole file), and the prose headers of every `scripts/*.sh`, `scripts/*.py` and `scripts/tests/*.sh`. Also read for cross-checks: `CONTRIBUTING.md`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.gitignore`, `docker/Dockerfile` (pins, publish flags, s6 settings, healthcheck), `docker/docker-compose.yml`, the s6 `run`/`init-wondarr-user` scripts, `ScheduledTaskCatalog.cs`, `SeedData.cs`, `OutputPolicy.cs` (enum and defaults), `DomainEnums.cs` (AlbumPolicy), `tools/FakeSlskd/Scenario.cs` and `FakeSlskdOptions.cs`.

Verified and found correct (no finding): NOTICE.md (every "Ported from"/"adapted from"/"derived from" first-comment-block header in `src/`, `tools/`, `scripts/`, `docker/` has a row and every row's file exists; `ci.yml:50` runs `check-notice.py`; the bundled-program versions match the Dockerfile; the only unlisted mentions are body comments outside the first block, which the check does not count); `docs/api/openapi.json` (114 paths; every controller route is present, spot-checked `trustnextread`, `song/convert/preview`, `system/download-token`, `song/bulk`, `library/{id}/compact`, `referencelibrary/{id}/ownedsongs`, `feedback`, `youtube/video`, `plex/server`, `soulseek/test`); `docs/screenshots/` (the five images the README references all exist; the website references none under that path); `website/README.md` (Node `>=22.12`, the three scripts, the `/wondarr` base, the sidebar in `astro.config.mjs`, the links validator); DEPLOYMENT.md's s6, shutdown, port, healthcheck, slskd file paths, SHA-pinned actions and `promote` job claims; the `tests/fixtures/*/README.md` file lists (every named fixture exists); the gate file names the script headers cite (`phase1-songs.txt`, `phase1-reference.json`, `phase2-songs.txt`, `phase6-exportify.csv`); the FakeSlskd scenario property names, ports and `AssemblyName`.

Skimmed: `src/` and `tests/` (grep for names, keys, tables and routes only; no reading of logic), `scripts/*.py` bodies (argparse definitions only), `README.md` (screenshot references and the docs map), `CHANGELOG.md` (release dates and the OpenAPI entry).

Not reached: the `website/src/content/docs/**` pages (another reviewer's area), the data files under `tests/fixtures/**` and `tests/gate/replay/**` (excluded by the brief), the pixel content of the screenshots, `packaging/**` and `unraid/**` READMEs, and the numeric claims in MATCHING_ENGINE §6.3 scoring and §6.5 thresholds (not verified against the decision engine).


## Appendix: the reconstructed rules and what the real skill says

The documentation reviewers tagged style findings R1 to R14 against a rule set reconstructed before the real skill was available. This table is the key for reading those tags now that `.claude/skills/writing-style/SKILL.md` holds the original.

| Tag | Reconstructed rule | In the real skill (Style 1, Default, for repository prose) | Findings under it |
|---|---|---|---|
| R1 | Plain words, no filler ("simply", "just", "note that"), present tense | Partly. "Note that..." is forward-caveating and is banned; hedging ("may", "might", "could potentially", "in some cases") is banned; "simply"/"just" and tense are not mentioned | `Note:` stands; the rest rejected |
| R2 | British spelling | Not a rule. The skill and the repository write British, so it is a consistency preference | Rejected, optional |
| R3 | Numbers with a unit and a space; no "~" | Not a rule | Rejected |
| R4 | Bold UI labels, arrow navigation paths | Not a rule; repository practice | Rejected, optional |
| R5 | Name the actor; no agentless passives | Not a rule. The nearest is "State what things do" | Rejected |
| R6 | No marketing, exclamation marks or rhetorical questions | Not a rule; Style 1 is deliberately wry and the README is its model | Rejected |
| R7 | "for example", not "e.g.", "etc.", "and so on" | Not a rule | Rejected |
| R8 | Spelling, grammar and product names | Not written down, but an error in any style | Stands |
| R9 | No em dashes in prose | The opposite: "em dashes aren't banned at all" (Style 3), and Style 1's examples use them | Rejected |
| R10 | No arrows or "X → Y → Z" chains | Not a rule | Rejected |
| R11 | No clause-length parentheticals | Not a rule as such; "Short sentences. Often fragments." is the nearest | Paragraph-length findings stand; the parentheses count rejected |
| R12 | Expand abbreviations; no "§" references to absent documents | Not a rule for docs (only Style 2 level 5 spells out acronyms); the absent-document part is a fact finding (S2) | Rejected as style; S2 facts stand |
| R13 | Noun-phrase headings; short bullets; bold only for lead phrases | Not a rule, except that "Short sentences" and "no over-long intros" cover paragraph-length bullets | Paragraph-length findings stand; the rest rejected |
| R14 | No "it's not X, it's Y"; no triplets; no "Let's"; no "we" | "No 'not this, but this' framing" is a Style 1 rule (see `style-01`). Triplets are Style 3 (on request only); "we" is restricted only in casual outreach | `style-01` stands; the rest rejected |

Rules the real skill has that the reconstruction lacked, and which the later pass checked: no hedging (clean), no forward-caveating (clean except `Note:`), no "not this, but this" (`style-01`), short sentences (the paragraph-length findings), tables for technical lists (already the practice), and the code-comment rules (`style-02`, a decision for the owner).
