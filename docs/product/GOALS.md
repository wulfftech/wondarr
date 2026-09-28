# Goals, non-goals, constraints

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the scope contract. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

### Goals

The name says the goal: **Wondarr** fetches an artist's one-hit wonder (or any single song) on its own — never the whole album of B-sides around it.

- **G1. Song-level everything.** Add, monitor, search, download, verify, tag, file, upgrade, and report at the level of one recording. No album is ever required to exist for a song to be managed.
- **G2. Source ladder: Soulseek → YouTube → Torrent/Usenet.** Each source is a plugin behind one interface; the order and per-source rules are configurable per song via source profiles.
- **G3. Verified imports.** A song is only marked "has file" after the file has been probed, duration-checked, and (where possible) fingerprint-matched to the intended recording.
- **G4. Four library layouts** — flat, per-artist, per-artist/album, and a Plex/Plexamp preset — driven by one naming-template engine, with complete tags and artwork regardless of layout.
- **G5. *arr ergonomics.** Wanted / Queue / History / Blocklist / Quality Profiles / Root Folders / Connect / Import Lists, an `X-Api-Key` REST API under `/api/v1`, and Docker conventions self-hosters already know.
- **G6. Runs on a NAS.** One container plus slskd; SQLite; modest CPU and memory; no Redis/Postgres requirement.

### Non-goals (v1)

- Managing albums, discographies, or artist-level completeness (that is Lidarr's job; this app should coexist with Lidarr, not replace it).
- Being a music *player* or streaming server (Plex/Plexamp, Navidrome, Jellyfin do that).
- Acting as a Prowlarr "application" (we consume Torznab/Newznab endpoints; Prowlarr does not need to know about us).
- Bulk ripping of streaming services (Tidal/Qobuz/Deezer rippers) in v1. The plugin interface allows a community source later; it is not on the core roadmap.
- Multi-user request workflows (Overseerr-style) in v1.

### Constraints

- Must respect Soulseek's social contract: share content, one login per username, do not flood searches.
- MusicBrainz's public API allows about one request per second per client and requires a descriptive `User-Agent`; every metadata call is cached.
- YouTube is a moving target (bot checks, PO tokens, JS challenges); the YouTube source must degrade gracefully and never block the rest of the app.
- Torrent/usenet music releases are album-sized; per-song cost must be minimised (selective file download, bundling of wanted songs) and private-tracker rules must be respected.
