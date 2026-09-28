# ADR-0001: The unit of work is a MusicBrainz recording, filed under an assigned album context

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner, planning session

## Context
Lidarr's unit is the MusicBrainz release group; its `Track` has no `Monitored` flag, search queries are `artist + album + year`, and automatic import rejects any download that does not contain a release's full tracklist. The maintainers document that "individual tracks aren't addable in isolation" and closed single-track requests in 2019 and 2025 (research: `docs/research/research_arr.md` §2). MusicBrainz's *recording* is the entity that means "this specific audio": remasters share a recording, while radio edits, remixes and live versions are separate recordings (`docs/research/research_metadata_plex.md` §1).

## Decision
A **Song** is one MusicBrainz recording (MBID when known; ISRC and Deezer/Spotify/YouTube Music ids as auxiliary identity; a title/artist/duration fallback while unresolved). Songs carry version flags parsed from titles and MB disambiguation. Each song has exactly one **album context** (a real release id or a synthetic pseudo-album id) assigned by the library's album policy (ADR-0007); the release, release-group and release-track MBIDs are stored alongside the recording MBID for tagging and Plex matching. Artists are grouping entities. Sonarr's per-episode `Monitored` + `wanted/missing` model is the template.

## Consequences
- Search, decision, import, upgrade and history all operate per song; albums are derived, never required.
- Identity resolution needs a cache and rate limiting (MusicBrainz 1 req/s) and a Deezer/iTunes fallback for songs MB lacks.
- The UI must show disambiguation, length and release types so users pick the right recording.

## References
`docs/PLAN.md` §3.1, §4.2, §5.4 · `docs/architecture/ARCHITECTURE.md` · `docs/research/research_arr.md` · `docs/research/research_metadata_plex.md`
