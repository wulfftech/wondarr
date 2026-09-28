# ADR-0005: Source ladder Soulseek → YouTube Music → torrents/usenet behind one SourceProvider interface

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Soulseek is the only widespread network where single files in high quality are the norm; YouTube Music's auto-generated Art Tracks almost always exist but are capped at Opus ~160 kbps; torrent/usenet releases are album-sized and expose only file names and sizes (`docs/research/FINDINGS.md` §3.2–3.4).

## Decision
Sources implement `SourceProvider` (`search(song) → candidates`, `grab`, `status`, `cancel`, `completed_path`, `blocklist_key`) and are run **by tier** in the song's source profile: Soulseek first; YouTube only when Soulseek yields nothing acceptable; torrents/usenet last (interactive search fans out to all). Candidates are normalised to one shape so the decision engine is source-agnostic. YouTube discovery uses YouTube Music search (ISRC first, `songs` filter, Art Tracks preferred) and yt-dlp for the grab; the YouTube source must be enabled by the user (ToS disclaimer) and uses only user-supplied cookies/PO-token providers.

## Consequences
- Adding a source (Bandcamp, SoundCloud, embedded Soulseek) never touches the decision engine or import pipeline.
- Per-source rate limits and error taxonomies live inside each provider.

## References
`docs/architecture/ARCHITECTURE.md` §5.3 · `docs/architecture/MATCHING_ENGINE.md` §6.4 · `docs/research/research_youtube.md`
