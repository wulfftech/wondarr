# NOTICE — third-party code and attributions

Compilarr is licensed under GPL-3.0 (see `LICENSE`). This file lists code ported or adapted from other projects and software redistributed in the Docker image. Every ported file also carries a header comment naming its source, path and licence. Keep this list current in the same change that adds or removes ported code.

## Ported / adapted code

| Source project | Licence | What | Where in this repo |
|---|---|---|---|
| (none yet — Phase 0 adds the first entries) | | | |

Planned sources (see ADR-0002): Lidarr, Prowlarr, Sonarr (GPL-3.0); SoulSync, spotDL (MIT). Sockseek and slskd are AGPL-3.0 and are **not** copied.

## Redistributed software (Docker image)

| Software | Licence | Notes |
|---|---|---|
| slskd | AGPL-3.0 with additional terms | Redistributed unmodified as a separate program under `/opt/slskd`; licence, additional terms and a link to its source are shipped in the image |
| ffmpeg / ffprobe (static build) | LGPL/GPL (build-dependent) | Unmodified binaries |
| Chromaprint `fpcalc` | LGPL-2.1 | Unmodified binary |
| Deno | MIT | Unmodified binary, required by yt-dlp's JavaScript challenge solver |
| yt-dlp | Unlicense | Unmodified |
