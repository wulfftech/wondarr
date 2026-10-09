---
title: Reference libraries
description: Tell Wondarr about the music you already have, so it never downloads it again.
---

A reference library is a folder of music you already own. Wondarr scans it, works out which recording each file is, and counts that song as owned. Add one under **Settings → Reference libraries** with **Add reference library**.

## Settings

- **Name**.
- **Root path**: "The folder to walk, as the container sees it." Mount it into the container, for example under `/data`.
- **Mode**: **Reference** or **Adopt** (below).
- **Target library**: with Reference, songs found here are filed under this library; with Adopt, "Adopted files are copied into this library."
- **Scan daily**: on by default.

The table shows each library's mode, when it was last scanned (hover for the scan's message) and badges for how many files are identified, adopted, unreadable, missing or still need review.

## Reference only, or adopt

- **Reference only**: Wondarr only reads the folder. An identified file becomes the song's file where it lies. The song counts as owned and is never searched for, upgraded or replaced automatically. If you later grab a file for it by hand, the new file goes into your managed library and the song points at it; your own file is left alone.
- **Adopt**: identified files are **copied** into the target library through the normal import path: tagged, filed by the library's layout and album policy, with cover and lyrics. The originals are never moved or changed. The copy is an ordinary library file from then on, so it can be upgraded. Use **Adopt now** in a library's menu to start it.

In both modes your files are only ever read. Deleting a reference library does not touch them; songs owned only through that folder become wanted again.

## The scan

The scheduled **Reference Library Scan** task runs every 24 hours, and **Scan now** in a library's menu runs it at once. It reads these file types: `.mp3`, `.flac`, `.m4a`, `.mp4`, `.aac`, `.ogg`, `.oga`, `.opus`, `.wav`, `.aif`, `.aiff`, `.wma`, `.ape` and `.wv`. A file whose size and modification time have not changed is not read again, so later scans are quick. Songs found in a reference library are added without being searched for, so scanning never starts a download. Scanning two libraries queues two scans; asking for the same library again while its scan is queued or running reuses that scan. A scan refuses to run (and changes nothing) if the folder is gone or looks like an unmounted share.

## How files are identified

For each file Wondarr tries, in order:

1. The MusicBrainz recording id in the file's tags.
2. The ISRC in its tags.
3. An AcoustID fingerprint. This needs an AcoustID client key (`acoustid.client_key`, see [Configuration](/wondarr/reference/configuration/)). Without a key Wondarr does not ask AcoustID.
4. A text search on the tagged artist and title.

Each result carries a confidence. A tagged recording id scores 1.0 and an ISRC 0.95. A fingerprint scores its AcoustID score, capped at 0.89 unless the file's tags back it up and the length agrees. A text match scores 0.90 only when it resolves cleanly and the lengths agree. A file is accepted on its own at or above `reference.auto_accept_threshold` (default `0.90`). The length must be within `reference.duration_tolerance_ms` (default 5000) of the recording's. Anything else goes to the Match queue.

If AcoustID is rate-limited or down, the file is left pending and tried again on the next scan.

## The Match queue

**Match** in the navigation lists the files Wondarr could not settle, with a count badge. Filter by **Reference library**. Each row shows the file, its format, length and bitrate, a **State** (**Needs review** or **No match**) and the top candidate.

- **Accept** takes the top candidate. **Select all on this page** and **Accept top candidate for selected** do it for many files.
- **Review** opens the file's drawer: what the file says about itself, the ranked candidates (with length difference, score and the reason for each), and a search box (**Artist – title**) to find the recording yourself. Accept a result, or **Skip** the file.
