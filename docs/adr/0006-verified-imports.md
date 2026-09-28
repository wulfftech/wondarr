# ADR-0006: Every import is verified (ffprobe, duration, AcoustID) and failures auto-retry the next candidate

**Status:** Accepted · **Date:** 2026-09-28 · **Deciders:** planning session (owner-approved plan)

## Context
Filename matching alone imports live versions, remixes, sped-up YouTube uploads and mislabelled files. AcoustID resolves a Chromaprint fingerprint to recording MBIDs with a 0–1 score at 3 req/s; remasters share a recording (pass), edits/remixes/live do not (fail) (`docs/research/research_metadata_plex.md` §2).

## Decision
After download: ffprobe must decode and measured quality replaces inferred quality (except app-made transcodes, which keep the source quality); duration must be within tolerance; `fpcalc` + AcoustID lookup must return the wanted recording with score ≥ 0.7 (0.5–0.7 = low-confidence badge; retry on a middle chunk first), learning the MBID when the song had none; if AcoustID has no coverage, fall back to probe + duration + score threshold and mark `fingerprint_verified = false`. A failed verification blocklists the candidate and grabs the next automatically, bounded per search; then the next source tier.

## Consequences
- Requires an AcoustID application key (owner-provided) and `fpcalc` in the image.
- Wrong files never reach the library silently; the interactive search shows why candidates were rejected.

## References
`docs/architecture/MATCHING_ENGINE.md` §6.5 · `docs/research/research_metadata_plex.md` §2
