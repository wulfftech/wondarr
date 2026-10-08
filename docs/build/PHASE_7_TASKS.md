# Phase 7 tasks — qBittorrent and SABnzbd sources

Each task is sized for one cheap-worker run (≤ ~10 files, ≤ ~400 lines of diff; see `AGENT_WORKFLOW.md`). The full spec for each lives in `docs/build/tasks/<id>.md` and is committed on `main` before its worker starts. Scope comes from ADR-0009 and `PHASES.md` Phase 7; the open questions are settled in `DECISIONS.md` build session 8 (#1–#11). External facts were checked on 2026-10-08 against qBittorrent's WebUI API v2 reference and Lidarr's `QBittorrentProxyV2` (the 2.11.0 threshold for `stopped`/`stop`/`start`), SABnzbd's API (`addurl`, `addfile`, `queue`, `history`, `get_files`, `delete_nzf`), and the Torznab/Newznab spec (`t=caps`, `t=music`, `t=search`, `torznab:attr`).

Gate (`PHASES.md` Phase 7): a song only available inside an album torrent is imported by downloading just that file; an NZB album downloads, unpacks, and only the wanted track(s) are imported; two wanted songs from the same album are satisfied by one grab.

| ID | Title | Depends on | Size | Tier |
|---|---|---|---|---|
| P7-00 | The owner's UI feedback (Wanted cover toggle, Init Caps badges, Match columns, no ids in reasons) and Plex "Connect" by server; this plan and the decisions | — | S | orchestrator |
| P7-01 | Indexers and download clients as rows (#1, #8): the two tables, CRUD + schema + test endpoints with masked secrets, in the notifications style; no searching yet | — | M | T2 worker |
| P7-02 | Release parsing: the release-name quality parser (Lidarr's `QualityParser` music part, ported) and artist/album from a release title; the bencode `.torrent` reader (#4); the NZB file-list reader | — | M | T2 worker |
| P7-03 | Indexer clients (#2): Torznab/Newznab `caps` (cached per indexer), the query generator (`t=music` vs `q=` fallback, categories), the RSS parser (ported); Prowlarr `/api/v1/search`; Gazelle `browse` with `fileList` | P7-01, P7-02 | M | T2 worker |
| P7-04 | Container matcher (#3): locate a wanted song in a file list by track number, title and size window; the indexer `SourceProvider.SearchAsync` (recording → releases → queries → containers → per-song candidates, tier 3, #7) | P7-03 | M | T3 (orchestrator) |
| P7-05 | qBittorrent client (#9): the proxy (login with `Referer`, re-login on 403, version detection), add stopped / `stopCondition`, `files`, `filePrio`, `start`/`resume`, `info`, remove; states for 4.x and 5.x; remote path mapping | P7-01 | M | T3 worker |
| P7-06 | SABnzbd client (#10): `addurl`/`addfile` paused, `get_files` + `delete_nzf` trimming, `queue`/`history` status, `storage` path, delete with files; remote path mapping | P7-01 | M | T3 worker |
| P7-07 | Grab, track, import (#5, #6): the provider's `GrabAsync`/`GetStatusAsync`/`CancelAsync` over the clients; completion per file; hard-link into staging (torrents), move + job cleanup (usenet); post-unpack matching; bundling; `release/push` grabs (#11) | P7-04…P7-06 | L | orchestrator |
| P7-08 | Frontend: Settings → Indexers and Settings → Download clients (add/edit per type from the schema, test, remote path mappings); the queue and history show the container and file | P7-01, P7-07 | M | T2 worker |
| P7-09 | Phase 7 gate: fakes in FakeSlskd (a Torznab/Newznab indexer from fixtures, a fake qBittorrent that "downloads" only the files with priority > 0, a fake SABnzbd that unpacks a fixture album), `scripts/phase7-gate.py` and the `SMOKE_PHASE7` stage; live on `ch01` against real qBittorrent and SABnzbd containers | all | M | orchestrator |

Waves: **A** P7-01 ∥ P7-02 → **B** P7-03 ∥ P7-05 ∥ P7-06 → **C** P7-04 → **D** P7-07 → **E** P7-08 → **F** P7-09. At most three workers at once; never a merge while a worker runs its tests (build session 7's lesson). Workers cannot regenerate `docs/api/openapi.json` and the frontend types; the orchestrator does at merge.

Migrations: P7-01 only (the two tables); P7-07 adds the queue item's container fields (infohash or job id, file index) if the handle JSON is not enough — decided when P7-07 is specified.

Ported files (P7-02, P7-03, P7-05, P7-06) carry the `Ported from` header and a `NOTICE.md` row at the pinned Lidarr commit; `scripts/check-notice.py` enforces it.

Budget: USD 8.36 left on the key (usage 21.64 of 30, 2026-10-08). Four T2 runs (≈ USD 0.15–0.3 each on `z-ai/glm-5.3-flash`), two T3 runs (≈ 0.6–1 on `z-ai/glm-5.3`) and T3 reviews for the T3 and orchestrator tasks (≈ 0.2 each) come to ≈ USD 3–4. The orchestrator writes P7-04, P7-07 and P7-09 itself (T3 workers ran out of turns on every M backend task in Phase 6).

## What the workers must not do

- Never delete or move torrent data in the client's folders (#5); never remove a torrent that still has a downloaded file or an item in flight.
- Never send a request to a real indexer, tracker or client in a test; fixtures and fakes only.
- Never log an API key, password, cookie (`SID`) or a URL carrying `apikey=`; register every secret with `ISecretRegistry`.
- Never write `.webm`; never produce lossless from lossy; never let a container's quality outrank what the probe measures after download.
- Never touch `AGENTS.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/adr/`, `.claude/`, `.github/workflows/`, `.env*`.
