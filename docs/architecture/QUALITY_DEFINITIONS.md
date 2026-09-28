# Quality definitions seed

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — the seed table for the qualities and default profiles. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

Ascending rank; the profile UI groups them as Lidarr does. Lossless ranks above every lossy tier; the profile's cutoff is one of these.

| Rank | Group | Qualities |
|---|---|---|
| 1 | Unknown | Unknown |
| 2 | Trash lossy | MP3-8 … MP3-80 (import-only, never grabbed) |
| 3 | Poor lossy | MP3-96, MP3-112, MP3-128, MP3-160, Vorbis Q5, **OPUS-96** |
| 4 | Low lossy | MP3-192, MP3-224, AAC-192, Vorbis Q6, WMA, **OPUS-128** |
| 5 | Mid lossy | MP3-256, MP3-VBR-V2, AAC-256, Vorbis Q7, Vorbis Q8, **OPUS-160** (YouTube's best) |
| 6 | High lossy | MP3-320, MP3-VBR-V0, AAC-320, AAC-VBR, Vorbis Q9, Vorbis Q10, **OPUS-192+** |
| 7 | Lossless | FLAC, ALAC, APE, WavPack |
| 8 | Hi-res lossless | FLAC 24-bit, ALAC 24-bit |
| 9 | Uncompressed | WAV, AIFF |

Default profile **"Standard 320"**: allowed = MP3-320, MP3-VBR-V0, AAC-256, AAC-320, FLAC, FLAC 24-bit, plus lower lossy tiers down to OPUS-128/MP3-192 as placeholders; cutoff = MP3-320 (AAC-256 counts as met); upgrades on. Second profile "Lossless": cutoff FLAC.

Additions over Lidarr: explicit Opus tiers (Lidarr folds Opus into Vorbis Q-levels), so YouTube results are ranked honestly. Detection order: measured (ffprobe) after download always wins; before download use Soulseek attributes (bitrate, sample rate, bit depth, VBR flag), YouTube format ids (251 → OPUS-160, 140 → AAC-128), and Lidarr-style release-name parsing for torrents/usenet (`FLAC`, `24BIT`, `MP3-320`, `V0`, `WEB`…). Size sanity windows per tier come from the MusicBrainz duration × bitrate with Lidarr-like min/max budgets.
