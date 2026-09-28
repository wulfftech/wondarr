# ADR-0012: Inputs: reference libraries with a match queue, Exportify CSV, playlist and scrobble lists

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
The owner wants songs to come from individual adds, Deezer/YouTube Music playlists, Spotify playlists exported as CSV, Last.fm/ListenBrainz, and the contents of an existing music folder (flat or layered) with manual matching where AcoustID cannot resolve files; dedupe should check the user's main Music folder rather than Plex or Lidarr. Spotify's Web API is effectively owner-only for new apps; Exportify's CSV carries ISRC, durations and album data (`docs/research/research_metadata_plex.md` §3, `docs/DECISIONS.md` round 1 Q8/Q10).

## Decision
A **reference library** is any folder the user registers: scanned, identified (tags → AcoustID → title/artist/duration search), recorded as owned so it is never re-downloaded, with a **Match queue** for ambiguous files (auto-accept above a high confidence threshold, ask below) and an optional **adopt** action (re-tag and place under a managed layout). Import lists include CSV with the Exportify column set plus a generic mapper, Deezer and YouTube Music playlists, Last.fm and ListenBrainz, artist top-N, and reference libraries as list sources; the Spotify Web API importer is optional with the owner's own client id. This is Phase 3 (adoption) and Phase 6 (lists).

## Consequences
- "Do I already have it?" is answered from the user's own folder, not from Plex/Lidarr.
- The match queue UI is a core screen, not an afterthought.

## References
`docs/architecture/LIBRARY_OUTPUT.md` §7.6 · `docs/build/PHASES.md` Phases 3 and 6
