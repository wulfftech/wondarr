# ADR-0002: Build new; port subsystems from Lidarr/Prowlarr (GPL-3.0) and logic from SoulSync/spotDL (MIT)

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Forking Lidarr would mean rewriting its domain, parser, decision engine, metadata source, import pipeline, API and UI (about 95k lines of Core C# and 103k lines of front end) while inheriting a cherry-pick maintenance model (`docs/research/research_stack.md` §1). SoulSync (MIT) and DroppedNeedle (AGPL + commercial) already implement much of the brief but are not *arr-shaped and lack the Plexamp policy and selective torrent download (`docs/research/FINDINGS.md` §3.6). The owner chose to build new and port useful code.

## Decision
Build a new code base. Port, with attribution headers and `NOTICE.md` entries: Lidarr/Prowlarr/Sonarr subsystems (qBittorrent and SABnzbd clients, Torznab/Newznab request/caps/RSS handling, `QualityParser`, `FileNameBuilder` token engine, notification providers, health-check framework, auth handlers, disk transfer/permissions/recycle bin, remote path mappings, decision-specification pattern, backup service, fingerprinting service); SoulSync's fake-lossless detector and matching heuristics; spotDL's YouTube Music matching rules. **Never copy AGPL code** (Sockseek, slskd); re-implement Sockseek's ranking from its documented behaviour.

## Consequences
- The app is GPL-3.0 (required by the ported code; the *arr norm).
- Ported code must be adapted to EF Core and our domain; keep its tests where they exist.
- `NOTICE.md` is a living attribution file checked in review.

## References
`docs/architecture/STACK.md` §8.4–8.5 · `docs/DECISIONS.md` round 1 Q1
