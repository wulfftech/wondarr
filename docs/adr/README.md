# Architecture Decision Records

One file per decision, numbered, never deleted; superseded ADRs get a "Superseded by" line. New ADRs use `0000-template.md`. Product-level (non-architectural) decisions live in `docs/DECISIONS.md`.

| ADR | Title | Status |
|---|---|---|
| 0001 | The unit of work is a MusicBrainz recording, filed under an assigned album context | Accepted |
| 0002 | Build new; port subsystems from Lidarr/Prowlarr (GPL-3.0) and logic from SoulSync/spotDL (MIT) | Accepted |
| 0003 | Stack: C#/.NET 10, ASP.NET Core, EF Core + SQLite, React 19 + Vite; GPL-3.0 | Accepted |
| 0004 | Soulseek via a bundled, app-supervised slskd process; never an embedded client library | Accepted |
| 0005 | Source ladder Soulseek → YouTube Music → torrents/usenet behind one SourceProvider interface | Accepted |
| 0006 | Every import is verified (ffprobe, duration, AcoustID) and failures auto-retry the next candidate | Accepted |
| 0007 | Library layouts and the Plexamp preset keep an album layer with a sticky "fewest albums" policy | Accepted |
| 0008 | Quality model derived from Lidarr with Opus tiers; default cutoff 320; transcodes keep source quality | Accepted (amended 2026-10-07) |
| 0009 | Torrent/usenet v1 scope: qBittorrent selective single-file download and SABnzbd whole-post | Accepted |
| 0010 | *arr-compatible API, Docker conventions, port 1077 | Accepted |
| 0011 | Agentic build workflow: Opus 5.5 orchestrator, cheap OpenRouter workers, review gates | Accepted |
| 0012 | Inputs: reference libraries with a match queue, Exportify CSV, playlist and scrobble lists | Accepted |
