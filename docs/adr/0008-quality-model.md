# ADR-0008: Quality model derived from Lidarr with Opus tiers; default cutoff 320; transcodes keep source quality

**Status:** Accepted, amended 2026-10-07 (conversion for every source; see Amendments) · **Date:** 2026-09-28 · **Deciders:** owner

## Context
Lidarr's quality definitions and weights are proven and familiar (`docs/architecture/QUALITY_DEFINITIONS.md`); Lidarr folds Opus into Vorbis tiers, which would misrank YouTube grabs. The owner wants FLAC supported, 320 as the default cutoff, and YouTube output transcoded to AAC or MP3 with customisable settings.

## Decision
Seed qualities from Lidarr plus explicit `OPUS-96/128/160/192+` tiers. Default profile "Standard 320": cutoff MP3-320 (AAC-256 counts as met), FLAC allowed, upgrades on; second profile "Lossless". Before download, quality is inferred (Soulseek attributes, YouTube format ids, release-name parsing); after download it is measured. A file the app transcoded from a lossy source keeps its **source** quality (`OPUS-160`), never the target bitrate, so it remains upgradeable. The YouTube output policy is per library with per-source and per-grab overrides (codec AAC/MP3/keep Opus, CBR or VBR, container, sample rate); lossless targets from lossy sources are refused; `.webm` is never written.

## Consequences
- Honest ranking: YouTube fills gaps now and gets replaced later.
- Quality definitions and size windows drive the size-sanity rejection.

## References
`docs/architecture/QUALITY_DEFINITIONS.md` · `docs/architecture/MATCHING_ENGINE.md` §6.3, §6.5 · `docs/architecture/LIBRARY_OUTPUT.md` §7.4

## Amendments
- **2026-10-07 (owner), conversion for every source:** the output policy is no longer YouTube-only. Each library's policy holds one rule per source class — YouTube (the Opus remux), lossy files from other sources, lossless files from other sources — and each rule keeps the file or converts it (AAC, MP3, Opus in `.opus` or `.ogg`, and for a lossless source FLAC or ALAC), with the bitrate, VBR quality and sample rate settings above; files can also be converted on demand after import. Unchanged: a converted file is ranked as its **source** (a FLAC converted to MP3 stays `FLAC` for upgrade decisions, as a YouTube file stays `OPUS-160`; the file's real codec and bitrate are stored beside it), lossless targets from lossy sources are refused, and `.webm` is never written (`docs/DECISIONS.md` build session 7).
