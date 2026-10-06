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

## Output policy for YouTube-sourced files (ADR-0008)

A YouTube download is the lossless remux of itag 251 (`.opus`); most players want AAC or MP3, so each library carries an output policy that says what a YouTube file becomes before it is verified, tagged and placed. A Soulseek file is never transcoded — it is imported as the peer served it.

The policy is JSON in `library.output_policy`; a library without one uses the default. Keys (camelCase, every failure names the key it came from):

| Key | Values | Default |
|---|---|---|
| `codec` | `keepOpus`, `aac`, `mp3` — a lossless target (`flac`, `alac`, `wav`, `ape`, `wv`) is refused: never lossless from lossy (ADR-0006) | `aac` |
| `mode` | `cbr`, `vbr` — `vbr` is the LAME quality scale, so it is MP3-only | `cbr` |
| `bitrateKbps` | 64–320 | 256 |
| `vbrQuality` | 0 (best) – 9 (smallest) | 0 |
| `sampleRate` | `keep` or a rate in Hz | `keep` |

Containers: `keepOpus` → `.opus` (no transcode runs), `aac` → `.m4a`, `mp3` → `.mp3`. The transcode target is written next to the download under the video id, and the Opus original is deleted only after the transcode succeeded.

A transcoded file is ranked as the source it came from, not the file the transcode wrote: a YouTube grab is OPUS-160 whatever the policy turns it into, so the profile gate, the upgrade check and `song_file.quality_id` all see OPUS-160 while `song_file`'s bitrate, sample rate and container describe the file that was actually placed. A failed transcode fails the item without a blocklist entry and without grabbing the next candidate — the file passed nothing yet, and the Opus original stays on disk for a later attempt.

