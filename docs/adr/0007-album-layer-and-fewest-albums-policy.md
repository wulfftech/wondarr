# ADR-0007: Library layouts and the Plexamp preset keep an album layer with a sticky "fewest albums" policy

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
The owner prefers flat `Artist - Title` files, or artist folders, for Plexamp. Assessment: Plex groups by embedded tags but its guidance says a flat file list "can result in failures or a poor experience" even with perfect tags; loose files in one folder have been observed merging into the wrong album under default settings; and Plex never reconsiders a track's album membership on rescan (`docs/architecture/LIBRARY_OUTPUT.md` §7.2).

## Decision
Layout presets `flat`, `artist`, `artist_album`, `plexamp` share one template engine (Lidarr `{Token}` syntax). The `plexamp` preset keeps an album folder layer and applies the **album policy**, default `fewest_albums`: per artist, a greedy set cover of official releases holding ≥ 2 owned tracks, with strays in an `{Artist} – Singles` pseudo-album; alternatives `singles_only`, `original_album`, `single_release`. Assignments are **sticky**; re-planning happens only via the explicit "Compact library" task, which performs Plex's move-out/scan/empty-trash/move-back sequence. One release id and one identical date string per folder; real dates in `ORIGINALDATE`. Flat/artist layouts remain available with the documented Plex caveat ("Prefer local metadata").

## Consequences
- Plex libraries stay tidy without hundreds of one-track albums.
- Adding songs never moves existing files; compaction is opt-in and auditable.
- The album policy engine needs MusicBrainz release lists per recording (cached).

## References
`docs/architecture/LIBRARY_OUTPUT.md` §7.1–7.4 · `docs/DECISIONS.md` round 1 Q5, round 2 Q4
