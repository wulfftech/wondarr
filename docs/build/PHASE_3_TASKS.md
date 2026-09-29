# Phase 3 tasks — reference libraries, adoption and the matching UI; the Plexamp preset

Each task is sized for one cheap-worker run (≤ ~10 files, ≤ ~400 lines of diff; see `AGENT_WORKFLOW.md`). The full spec for each lives in `docs/build/tasks/<id>.md` and is committed on `main` before its worker starts. External facts (Plex PIN flow, `refresh?path=`, `emptyTrash`, section listing, playlist upload, lyric sidecars; LRCLIB) are verified by the researcher agent before the specs that use them are written, and real responses are recorded under `tests/fixtures/plex/` and `tests/fixtures/lrclib/`.

Gate (`PHASES.md` Phase 3): an existing folder of 500 mixed files is identified ≥ 90 % automatically, the rest are resolved in the Match queue in one sitting, and adopted files appear in a fresh Plex Music library with correct grouping (no split albums, no one-track albums except deliberate singles); Plexamp shows local lyrics.

| ID | Title | Depends on | Size | Tier |
|---|---|---|---|---|
| P3-00 | Research (Plex, LRCLIB), fixtures, Phase 3 decisions, this plan | — | S | orchestrator |
| P3-01 | Reference-library scan: `reference_library` / `reference_file` / `match_candidate` tables + migration, `ITagReader` (ATL: MBIDs, ISRC, artist/title/album, track/disc, duration), scanner (walk flat or layered, probe, read tags, incremental by size + mtime, vanished files), `ReferenceLibraryScan` command, daily scheduled scan | P3-00 | M | worker |
| P3-02 | Identification pipeline: tag MBID → MusicBrainz; ISRC → the P1 bridge; fpcalc + AcoustID; title/artist/duration search; confidence tiers (auto-accept ≥ threshold); `match_candidate` rows; identified files become owned songs (`song_file` with source `reference`, never searched or replaced) | P3-01 | M | worker |
| P3-03 | API: `GET/POST/PUT/DELETE /api/v1/referencelibrary`, `POST …/{id}/scan`, `GET /api/v1/matchqueue` (paged, with ranked candidates), `POST /api/v1/matchqueue/{id}/resolve` (a candidate, an MBID/Deezer id from manual search, or skip), bulk resolve | P3-02 | M | worker |
| P3-04 | Adoption (`adopt` mode): identified reference files re-tagged (Picard mapping, cover) and placed under the target library's layout and album policy (move or copy, recycle bin, collisions), `song_file` repointed, state `adopted`; one save per file | P3-02 | M | worker |
| P3-05 | Plexamp preset specifics: cover resizing to the max edge (ffmpeg, no new imaging package), `cover.jpg` in each album folder (pseudo-album: the first track's art), folder consistency check (one album id / date / album artist / album title per folder, asserted before tagging), sidecar options on the library | P3-00 | M | worker |
| P3-06 | Plex client and connection: PIN flow (plex.tv v2), server discovery, section listing, connection test, token storage (never logged or returned), per-library section + path mapping (migration), API `GET/PUT /api/v1/plex/…` | P3-00 | M | worker |
| P3-07 | Plex partial scan after import and adoption: debounced per album folder, `refresh?path=` with the mapped path, failures logged as health, never failing an import | P3-06 | S | worker |
| P3-08 | LRCLIB client (`/api/get` with duration, `/api/search` fallback, User-Agent, spacing) and lyrics: `.lrc` (synced) or `.txt` sidecar with the track's base name, unsynced text in the tag; on import and on adoption, per-library sidecar option | P3-00, P3-05 | M | worker |
| P3-09a | Compact library planner: re-plan album assignments under the library's policy (the `AlbumPolicyEngine` over all songs, ignoring stickiness), diff against the current contexts → a list of moves (old path → new path, new album context); dry-run API | P3-05 | M | worker |
| P3-09b | Compact library executor: the Plex sequence (move out to a staging folder, scan, empty trash, re-tag + move to the new path, scan), per folder, resumable, recycle-bin safe; `CompactLibrary` command | P3-09a, P3-07 | M | worker |
| P3-10a | Notifications framework + Webhook: `notification` table, per-event toggles (grab, import, upgrade, failure, health), dispatcher on the event aggregator, test endpoint, API CRUD (ported from Lidarr, GPL-3.0, attributed) | P3-00 | M | worker |
| P3-10b | Notifications: Discord and Apprise (ported from Lidarr) | P3-10a | S | worker |
| P3-11a | Frontend: Settings → Reference libraries (list, add/edit, mode, target library, scan now, scan status) | P3-03 | M | worker |
| P3-11b | Frontend: Match queue page (file facts, ranked candidates with the reason and score, manual search, accept / skip, bulk accept of the top candidate) | P3-03 | M | worker |
| P3-12a | Frontend: Settings → Plex (PIN sign-in, server and section picker, path mapping, test) and the "Prefer local metadata" notice for flat/artist layouts in Settings → Library | P3-06 | M | worker |
| P3-12b | Frontend: Settings → Notifications (list, add/edit per type, event toggles, test) and a Compact library screen (dry-run list, run) | P3-09a, P3-10b | M | worker |
| P3-13 | Phase 3 gate: a synthetic reference library in the CI image (mixed tagged/untagged, flat/layered, AcoustID stub via FakeSlskd), `scripts/phase3-gate.py`, the Phase 3 stage of `scripts/smoke-test.sh`; live on `ch01` with a throwaway Plex Media Server and a 500-file folder | all | M | orchestrator |

Waves: **A** P3-01 ∥ P3-05 ∥ P3-06 ∥ P3-10a → **B** P3-02 ∥ P3-07 ∥ P3-08 ∥ P3-10b → **C** P3-03 ∥ P3-04 ∥ P3-09a → **D** P3-11a ∥ P3-11b ∥ P3-12a ∥ P3-09b → **E** P3-12b → **F** P3-13.

Tasks that add a migration (P3-01, P3-06, P3-10a) are merged one at a time; the second and third to merge get their migration regenerated by the orchestrator so the model snapshot stays linear.

Budget: the OpenRouter key showed USD 2.38 left at the start of the phase (limit USD 10, usage 7.62); Phase 2 cost USD 4.46 for 20 worker tasks. The gate-critical path (P3-01 … P3-08, P3-11b, P3-12a) goes first; notifications and compaction follow when the key allows.

## Design decisions taken for Phase 3 (recorded in `docs/DECISIONS.md`, Build session 4)

- **A reference-only file is an owned song's file.** An identified file in a `reference` library becomes a `song_file` row with `source_type = "reference"` pointing at the file where it is. Such a song is never searched (it has a file) and never upgraded or replaced automatically: the app does not control that file. A manual grab for it imports into the library and repoints the song, leaving the reference file untouched (never recycled). Adopting turns it into an ordinary library file.
- **Confidence tiers (owner round 2 #7).** A file is identified automatically at confidence ≥ `reference.auto_accept_threshold` (default 0.90); below that its ranked candidates go to the Match queue (`ambiguous`), and a file with no candidate at all is `unmatched` (manual search). Confidence per source: a tagged recording MBID MusicBrainz knows, within the duration tolerance → 1.0; an ISRC that bridges to one recording within tolerance → 0.95; AcoustID → the AcoustID score, capped at 0.89 unless the recording's title and artist agree with the file's tags (or the file has no tags and every recording in the result shares one title and artist); text search → 0.90 when the resolver resolves the tags' (or file name's) artist and title and the length agrees within 5 s, otherwise the candidate's score scaled to 0–0.89.
- **Covers are resized with ffmpeg**, which the image already ships, rather than an imaging package (ImageSharp's split licence is not an OSI licence; SkiaSharp needs native assets).
- **The Opus → AAC/MP3 transcode setting moves to Phase 4**, where the output policy (and the only source of Opus files that must be transcoded, YouTube) is built. **`artist.jpg`** (fanart.tv / TheAudioDB, needs a key) moves to the backlog.
