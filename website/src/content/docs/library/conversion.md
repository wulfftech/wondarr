---
title: Conversion
description: Choose what each kind of download becomes, and convert files you already have.
---

Each library has **Conversion rules** under **Settings → Library**. They say what a download becomes before it is verified, tagged and filed. There is one rule for each kind of source:

| Rule | Applies to | Default |
|---|---|---|
| YouTube downloads | YouTube's Opus audio | AAC, 256 kbps constant bitrate |
| Lossy files (MP3, AAC, Opus, Vorbis…) | Lossy files from any other source | Keep as downloaded |
| Lossless files (FLAC, ALAC, WAV…) | Lossless files from any other source | Keep as downloaded |

## The options

Each rule has an **Action**: **Keep as downloaded**, **AAC (.m4a)**, **MP3** or **Opus**. The lossless rule also offers **FLAC** and **ALAC (.m4a)**. When the action converts, you also choose:

- **Mode**: **Constant bitrate**, or for MP3 only **LAME quality (VBR)** (V0 best to V9 smallest).
- **Bitrate**: 128, 160, 192, 256 or 320 kbps.
- **Sample rate**: keep the source's, 44100 Hz or 48000 Hz.
- **Container**: `.opus` or `.ogg`, for Opus targets and for a kept YouTube file. A YouTube Opus file kept as `.ogg` is only renamed.

## The rules that cannot be changed

- **Never lossless from lossy.** FLAC and ALAC are only offered for lossless sources, and a lossless target for a lossy file is refused.
- **Never `.webm`.**
- A file already in the target codec is kept as it is and not re-encoded.
- **A converted file keeps the quality it was downloaded at.** A FLAC converted to MP3 320 still counts as `FLAC`; a YouTube file turned into AAC still counts as `OPUS-160`. Otherwise a library that converts FLAC to MP3 would search for a FLAC upgrade forever. The song shows both what was downloaded and what is on disk.

## Convert files you already have

- **A whole library**: **Convert existing files…** on the library's settings tab. It plans the conversion first: how many files change, the current size and the estimated size, and what each file goes from and to, with the reason for any that are skipped. Press **Convert** to run it.
- **One song**: **Convert…** in the song's actions menu on the Library page. Choose a one-off rule, or use the library's rules.

Files are converted one at a time. The new file is checked (it decodes, and its length is within 1 second of the original), tagged and filed under the naming template with its new extension. **The original goes to the recycle bin**, so you can get it back. A file that fails to convert stays where it was. A file in a [reference library](/wondarr/library/reference-libraries/) is never converted. The History records a "Converted" event, and Plex is asked to scan the folder.
