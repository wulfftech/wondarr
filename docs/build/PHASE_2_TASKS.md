# Phase 2 tasks — the Soulseek source, slskd management and the import pipeline

Each task is sized for one cheap-worker run (≤ ~10 files, ≤ ~400 lines of diff; see `AGENT_WORKFLOW.md`). The full spec for each lives in `docs/build/tasks/<id>.md` and is committed on `main` before its worker starts. External API facts were verified on 2026-09-29 against the slskd 0.26.0 source, the AcoustID web service and chromaprint 1.6 (`docs/research/research_soulseek.md` §"Verified 2026-09-29", `docs/research/research_metadata_plex.md` §2.3), and real slskd responses were recorded from the bundled slskd on `ch01` into `tests/fixtures/slskd/live/`.

The shared contracts — `Candidate`, `ParsedName`, `ISourceProvider`, `SongSearchRequest`, `GrabHandle`, `DownloadStatus` (`src/Wondarr.Core/Sources/`) — are written by the orchestrator (P2-00) so the parser, the decision engine and the source can be built in parallel.

| ID | Title | Depends on | Size | Tier |
|---|---|---|---|---|
| P2-00 | Contracts, research, fixtures, account validation on `ch01` | — | S | orchestrator |
| P2-01 | slskd search API client, the global Soulseek search budget (30 / 4 min, ≤ 2 outstanding, ≥ 5 s apart), search runner (poll to completion, 30 s wall clock, stop, always delete) | P2-00 | M | worker |
| P2-02 | Candidate normalisation: Soulseek filename/path parser (§6.1), quality inference from attributes, slskd response → `Candidate`; golden `tests/fixtures/filenames.json` | P2-00 | M | worker |
| P2-03 | Decision engine v1 (§6.2–6.3): hard rejections, 0–1000 score with breakdown, ties, "good enough"; golden `tests/fixtures/decisions.json` | P2-00 | M | worker |
| P2-04 | Media tools: process runner, ffprobe probe → `MediaInfo` → measured quality, fpcalc fingerprint (window + middle-window retry via ffmpeg); fixtures recorded from the image | P2-00 | M | worker |
| P2-05 | AcoustID client (3 req/s, `Retry-After`, gzip POST) and the verification service (duration, score ≥ 0.7 / 0.5–0.7 review, MBID learning, no-fingerprint fallback) | P2-04 | M | worker |
| P2-06 | Tag writer (ATL 7.17): Picard mapping for MP3 (ID3v2.4), FLAC, M4A, Opus; embedded cover; write to a temp copy, re-read, atomic replace | P2-00 | M | worker |
| P2-07 | Naming template engine: Lidarr `{Token}` syntax, modifiers, optional groups, illegal-character replacement, max-path guard; preset templates; golden `tests/fixtures/naming.json` | P2-00 | M | worker |
| P2-08 | Domain + migration: `search_run`, `candidate` (with persisted score breakdown and rejections), `queue_item`, `soulseek_user` (reputation, ignore); services | P2-00 | M | worker |
| P2-09 | File placement: hardlink → move → copy fallback, permissions, recycle bin, remote path mappings, collision handling (`IFileSystem`-style abstraction) | P2-00 | M | worker |
| P2-10 | slskd transfers client: batch enqueue with per-grab `destination`, transfer state mapping, queue position, cancel/remove; `DownloadFileComplete` webhook (renderer + receiver endpoint) | P2-01 | M | worker |
| P2-11 | Soulseek source provider: multi-query strategy (§6.4), responses → candidates, ignore list and reputation skip, grab/status/cancel, stall / offline / remote-queue timeouts | P2-01, P2-02, P2-08, P2-10 | M | worker |
| P2-12 | Search and grab: `SongSearch` / `MissingSearch` commands, decision + persistence, grab the best, queue item + history "grabbed", scheduled missing search with per-song backoff | P2-03, P2-08, P2-11 | M | worker |
| P2-13a | Import service: completed file → verify → quality check → tag (with cover) → name → place → `song_file` + history; reject → blocklist (per song) + reputation + next candidate (≤ 4 attempts); deferred verification retried | P2-04 … P2-09, P2-12 | M | worker |
| P2-13b | Queue tracker (hosted service): poll active grabs (10 s / 60 s idle), webhook wake-up, start / remote-queue / stall timeouts, failures → next candidate, import on completion, crash recovery, queue actions (remove, blocklist-and-retry), SignalR queue events | P2-13a | M | worker |
| P2-14 | API: `GET/DELETE /api/v1/queue`, interactive search `GET /api/v1/release?songId=` + `POST /api/v1/release`, `POST /api/v1/library/{id}/preview` | P2-12, P2-13 | M | worker |
| P2-15 | slskd health: login errors, duplicate-login kick, sharing, download folder writable; no host crash on an unwritable `/data` | P2-00 | M | worker |
| P2-15b | Soulseek settings: `GET/PUT /api/v1/soulseek/settings` (writes the `soulseek` section of `config.yml`, env-locked fields read-only, restart via the existing supervisor), `GET /api/v1/soulseek/status` (login, shares from slskd, search budget) | P2-15, P2-01 | M | worker |
| P2-16 | Frontend: Queue page (live progress over SignalR, cancel, blocklist-and-retry) and the Interactive search modal (score breakdown, rejections, grab) | P2-14 | M | worker |
| P2-17 | Frontend: Settings → Soulseek (share toggle with the leech warning, restart notice) and the naming template editor with live preview in Settings → Library | P2-14, P2-15 | M | worker |
| P2-18 | Phase 2 gate: fake-slskd + AcoustID replay in `scripts/smoke-test.sh` (CI), 100-song live run and 30-file audit on `ch01` | all | M | orchestrator |

Waves: **A** P2-01 ∥ P2-02 ∥ P2-03 ∥ P2-07 ∥ P2-08 → **B** P2-04 ∥ P2-06 ∥ P2-09 ∥ P2-10 ∥ P2-15 → **C** P2-05 ∥ P2-11 → P2-12 → **D** P2-13a → P2-13b → P2-14 ∥ P2-15b → **E** P2-16 ∥ P2-17 → **F** P2-18.

Tasks that add a migration (P2-08, and any later schema change) are merged one at a time; the second to merge gets its migration regenerated by the orchestrator so the model snapshot stays linear.

## Design decisions taken for Phase 2 (recorded in `docs/DECISIONS.md`, Build session 3)

- **Search results come from polling, not the hub, in v1.** Verified on `ch01` (2026-09-29): `GET /api/v0/searches/{id}/responses` returns `[]` while a search is in progress even when `responseCount` > 0, so REST polling cannot stream; but with `responseLimit` 100 real searches complete in ~3 s (`ResponseLimitReached`). The runner polls the search state every 500 ms until `isComplete`, then reads the responses once. `/hub/search` streaming (and the "grab at ≥ 850 while streaming" early stop) is deferred to Phase 5; the query-level early stop (§6.4) remains.
- **`searchTimeout` is sent in milliseconds** (8000): slskd passes it to Soulseek.NET unchanged, and its `[Range(5, …)]` minimum means a seconds value would be accepted but wrong.
- **Downloads go into a per-grab folder** via the batch endpoint's `options.destination` (`wondarr/{queueItemId}`), so the import pipeline never has to guess slskd's `${SOURCE_DIRECTORY}` layout and two grabs cannot collide.
- **Completion is detected by polling transfers** (10 s active); the `DownloadFileComplete` webhook only wakes the tracker early. A lost webhook costs latency, never correctness.
- **The webhook is authenticated with its own generated token** (a `X-Wondarr-Webhook` header, stored with the other slskd runtime secrets), not the app's API key.
- **Soulseek settings are written to `config.yml`**, the app's own file; environment variables still win, and the settings API reports env-set fields as read-only.
