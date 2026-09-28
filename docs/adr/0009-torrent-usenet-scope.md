# ADR-0009: Torrent/usenet v1 scope: qBittorrent selective single-file download and SABnzbd whole-post

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Track-level search does not exist in practice on Torznab/Newznab indexers; Gazelle's `filelist` is the only true per-track search. Every major torrent client supports selective file download; partial downloads never count as seeding on Gazelle trackers; usenet posts are commonly RAR-packed/obfuscated so tracks cannot be identified before download (`docs/research/research_torrent_usenet.md`). The owner wants qBittorrent only and no private-tracker rules for now, and keeps SABnzbd "as is tradition".

## Decision
v1 supports qBittorrent (add stopped, `stopCondition=MetadataReceived` for magnets, `files`, `filePrio`, `start`, import by file index; partial downloads on; never remove before the client's own seed goals) and SABnzbd (whole post, opportunistic per-file trimming only when file subjects are clean, post-unpack track selection). Releases are found by searching for albums that contain the recording (MusicBrainz release lists), via Prowlarr per-indexer endpoints or `/api/v1/search`, and Gazelle-direct where the user has a key. Other wanted songs in the same container are imported from one grab ("bundling"). Transmission/Deluge/rTorrent/NZBGet and per-indexer tracker policies are backlog.

## Consequences
- Per-song cost on torrents stays low; ratio effects on private trackers are the user's responsibility for now.
- Ported Lidarr/Prowlarr client code keeps Phase 7 short.

## References
`docs/architecture/MATCHING_ENGINE.md` §6.4 · `docs/build/PHASES.md` Phase 7 · `docs/research/research_torrent_usenet.md`
