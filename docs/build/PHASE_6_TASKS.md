# Phase 6 tasks — import lists, playlist sync, albums, libraries and conversion

> **Outcome (2026-10-07):** all tasks merged (P6-01, P6-03 and P6-09 written by the orchestrator; P6-06, P6-07, P6-08 finished by it); the gate passes in CI against recorded metadata, FakeSlskd and a fake Plex, and was checked live on `ch01` (Plex excepted). Results, costs and lessons: `PROGRESS.md` "Phase 6".

Each task is sized for one cheap-worker run (≤ ~10 files, ≤ ~400 lines of diff; see `AGENT_WORKFLOW.md`). The full spec for each lives in `docs/build/tasks/<id>.md` and is committed on `main` before its worker starts. The external facts were verified on 2026-10-07 (researcher agent plus live calls; P6-00 below): Exportify's localised header row, Deezer's playlist/album/top-track shapes (ISRC only on playlist tracks and `/track/{id}`; pages of up to 100), ytmusicapi's playlist browse and continuation, Last.fm and ListenBrainz endpoints, python-plexapi's playlist calls, and a MusicBrainz release lookup returning recording ids, positions, lengths and ISRCs in one call.

Gate (`PHASES.md` Phase 6, extended by the owner on 2026-10-07): a 200-track Exportify CSV imports, resolves via ISRC ≥ 95 %, downloads, and appears as a Plex playlist after two search cycles; an album added by search arrives as its tracks under that album; a song moved to a second library lands in that library's folder and Plex section; a library set to convert lossless to MP3 imports a FLAC as an MP3 still ranked `FLAC`; an on-demand conversion replaces an existing file with the original in the recycle bin.

| ID | Title | Depends on | Size | Tier |
|---|---|---|---|---|
| P6-00 | Pre-flight, research, this plan, decisions (build session 7), ADR-0008 amendment; the identity rule's tolerance (#11) | — | S | orchestrator |
| P6-01 | Import-list framework + CSV: the provider interface, list CRUD and sync API, the item resolver (MBID → ISRC → text, #7), the scheduled `ImportListSync`, list membership with order; the CSV provider (Exportify by name or by position, a generic column mapping, #8) | — | L | T3 worker |
| P6-02 | Providers: Deezer playlist, YouTube Music playlist (InnerTube browse), artist top-N (Deezer) | P6-01 | M | T2 worker |
| P6-03 | Sync policies (#9) and playlist output: Plex playlist by rating keys reconciled in place (#10), `.m3u8` export | P6-01 | M | T3 worker |
| P6-04 | Providers: Last.fm loved/top, ListenBrainz loved/playlists, a reference library as a list source | P6-01 | M | T2 worker |
| P6-05 | Frontend: Import lists (list, add/edit per provider, CSV upload with the column mapper, sync now, items and the unresolved review, playlist output settings) | P6-01…P6-04 | M | T2 worker |
| P6-06 | Album add backend: album search (MB release groups, Deezer fallback), release options and tracklist with owned flags, `POST /api/v1/album/add` (pinned album context, one MissingSearch), "rest of the album" for a song (#1, #2) | — | M | T2 worker |
| P6-07 | Several libraries backend: create/delete (#3), a song's library change as a `MoveSongs` command (file move under the per-song lock, album re-plan, Plex scans of both folders) | — | M | T3 worker |
| P6-08 | Conversion policy v2 (#4, #5): the per-class rules, version-1 compatibility, the import applying it to every source with the source's quality kept, the transcoder's new targets | — | M | T3 worker |
| P6-09 | Conversion on demand (#6): the `ConvertFiles` command with a dry run, `POST /api/v1/song/convert`, history "converted" | P6-08 | M | T3 worker |
| P6-10a | Frontend: Settings → Library for several libraries (add/delete, per-library Plex section), the conversion rules editor, "convert existing files…" with a dry run, the library picker on add | P6-07…P6-09 | M | T2 worker |
| P6-10b | Frontend: the Album tab (search, release, tracklist, add), and per song "add the rest of this album", "move to library…", "convert…" | P6-06, P6-07, P6-09 | M | T2 worker |
| P6-11 | Phase 6 gate: `scripts/phase6-gate.py` + the `SMOKE_PHASE6` stage (FakeSlskd, the replayed metadata, a throwaway Plex), live on `ch01` | all | M | orchestrator |

Waves: **A** P6-01 ∥ P6-06 ∥ P6-08 → **B** P6-02 ∥ P6-07 ∥ P6-09 → **C** P6-03 ∥ P6-04 → **D** P6-05 ∥ P6-10a ∥ P6-10b → **E** P6-11. Workers cannot run a command with an environment-variable prefix, so the orchestrator regenerates `docs/api/openapi.json` and the frontend types when it merges a branch that changes the API. At most three workers at once; the orchestrator never runs three test suites at once.

Migrations: P6-01 (list item order, a list's last sync result, sync interval), P6-03 (the list's Plex playlist id and output settings), P6-08 none (the policy is JSON), P6-09 none. Two branches that both add a migration are merged one at a time and the second regenerates its migration on top of the first (Phase 3's lesson).

Budget: USD 7.29 left on the key (usage 17.71 of 25, 2026-10-07). Five T3 runs (≈ USD 0.6–1 each at the T3 pick `z-ai/glm-5.3`), five T2 runs (≈ 0.15–0.3 on `z-ai/glm-5.3-flash`) and T3 reviews for the five T3 tasks (≈ 0.2 each) come to ≈ USD 5–7. The orchestrator reviews the T2 tasks itself and takes over any task whose run would push spend past USD 6.5, so that about USD 0.75 stays in reserve.

## What the workers must not do

- Never delete a file from a list sync (#9); never replace or remove a file except through the recycle bin (conversion, a library move).
- Never write `.webm`; never produce a lossless file from a lossy source; never record a converted file's target as its quality (#5).
- Never move or convert a song's file outside `SongFileLock` (build session 6 #4); never let a library move or a conversion run while the song has an unfinished compaction move.
- Never exceed MusicBrainz's 1 req/s (go through the existing rate-limited client), Deezer's 50 requests per 5 s, or the Soulseek search budget (a list sync queues searches through the existing commands; it never searches directly).
- Never touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`.
