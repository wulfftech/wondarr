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
- **Replace files below the cutoff**: off by default. See [Replacing files](#replacing-files) below.

The table shows each library's mode, when it was last scanned (hover for the scan's message) and badges for how many files are identified, adopted, unreadable, missing or still need review, how many are likely YouTube rips, and how many a library copy has replaced.

## Reference only, or adopt

- **Reference only**: Wondarr only reads the folder. An identified file becomes the song's file where it lies. The song counts as owned and is not searched for, upgraded or replaced automatically, unless you ask for that (see [Replacing files](#replacing-files)). If you grab a file for it by hand, the new file goes into your managed library and the song points at it; your own file is left alone.
- **Adopt**: identified files are **copied** into the target library through the normal import path: tagged, filed by the library's layout and album policy, with cover and lyrics. The originals are never moved or changed. The copy is an ordinary library file from then on, so it can be upgraded. Use **Adopt now** in a library's menu to start it.

In both modes your files are only ever read. Deleting a reference library does not touch them. The delete dialog tells you how many songs are owned through that folder and asks what should happen to them: keep them wanted (the default; the scheduled search picks them up in its own time), make them wanted and search every wanted song now (the search is not limited to these songs), or unmonitor them.

## Files that go missing, move or change

If a file disappears from the folder, its song keeps counting as owned for now. Only when a later scan finds it still gone, and at least 24 hours after it was first missed, does the song lose it and become wanted again, if it is monitored. So a rename or a move never loses the song: the scan finds the same song at its new path and the song follows it (and Adopt mode adopts the new file as usual).

If a scan finds more than 10 % of a library's owned files missing at once, or more than 200 of them (whichever is smaller, and never fewer than 3 files), Wondarr assumes the share is not mounted. It marks them missing but releases nothing, and Health shows a warning naming the library and the count. The warning clears by itself when the files are back or fewer are missing.

A file that is edited (a new size or modification time) is read and identified again, unless you skipped it or it was adopted: your choice stays, and only its size and measurements are refreshed.

## The scan

The scheduled **Reference Library Scan** task runs every 24 hours, and **Scan now** in a library's menu runs it at once. It reads these file types: `.mp3`, `.flac`, `.m4a`, `.mp4`, `.aac`, `.ogg`, `.oga`, `.opus`, `.wav`, `.aif`, `.aiff`, `.wma`, `.ape` and `.wv`. A file whose size and modification time have not changed is not read again, so later scans are quick. Songs found in a reference library are added without being searched for, so scanning never starts a download. Scanning two libraries queues two scans; asking for the same library again while its scan is queued or running reuses that scan. A scan refuses to run (and changes nothing) if the folder is gone or looks like an unmounted share.

A file with a few damaged audio frames, one that still plays through, is scanned and goes on to identification like any other: it only needs to decode with at most 20 lines of ffmpeg errors (a bad frame is two or three lines) and get through at least 98 % of its length. The scan notes it in the log, and the file's row says "Decodes with N errors" until it is identified. A file with more damage than that is counted as unreadable. Such a file is fingerprinted like any other: when the fingerprint tool gives up at a bad frame, Wondarr cuts the start of the file out with ffmpeg, which skips bad frames, and fingerprints that. Downloads are not given this slack: a damaged download is replaced.

## How files are identified

For each file Wondarr tries, in order:

1. The MusicBrainz recording id in the file's tags.
2. The ISRC in its tags.
3. An AcoustID fingerprint. This needs an AcoustID client key (`acoustid.client_key`, see [Configuration](/wondarr/reference/configuration/)). Without a key Wondarr does not ask AcoustID.
4. A text search on the tagged artist and title.

Each result carries a confidence. A tagged recording id scores 1.0 and an ISRC 0.95. A fingerprint scores its AcoustID score, capped at 0.89 unless the file's tags back it up and the length agrees. A text match scores 0.90 only when it resolves cleanly and the lengths agree. A file is accepted on its own at or above `reference.auto_accept_threshold` (default `0.90`). The length must be within `reference.duration_tolerance_ms` (default 5000) of the recording's. Anything else goes to the Match queue.

If AcoustID is rate-limited or down, the file is left pending and tried again on the next scan.

## Likely YouTube rips

YouTube serves its audio at 48 kHz; CDs and download stores use 44.1 kHz. So a lossy file (MP3, AAC, Opus, Vorbis) at 48 kHz is almost always a YouTube rip, even when it was re-encoded to a high-bitrate MP3 and retagged with MusicBrainz ids. The scan flags such a file as a **likely YouTube rip**. A lossless file at 48 kHz is never flagged, because 48 kHz is a real studio and store rate.

A likely rip is rated no better than **OPUS-160**, the quality Wondarr gives its own YouTube downloads. By bitrate alone a rip re-encoded to MP3 V0 would rate as V0, but it holds no more than the Opus stream it came from. A rip that measures worse (say MP3-128) keeps its own, lower, rating. Files already scanned are flagged and re-rated on the upgrade, from what the scan stored; nothing is read again.

Some rips land in the Match queue, because a music-video edit is a different length from the album recording its tags name. Most are identified like any other file, which is why the setting below matters.

## Replacing files

A song owned through a reference file can be replaced by a better copy, in two ways:

- **Replace** in the Match queue accepts the recording, like **Accept**, and asks for a better copy of the file. If the file is below the song's quality profile cutoff, an **Upgrade Search** for the song starts at once, even if the song was searched for recently; if it already meets the cutoff, or the profile allows no upgrades, nothing is replaced and Wondarr says so.
- **Replace files below the cutoff**, a setting on the reference library, does the same for every song held by one of the library's files, Match queue or not: such a song is upgraded like a library song, by the scheduled **Upgrade Search**, and shows under **Wanted → Cutoff Unmet**. Likely rips count as OPUS-160, so with the usual profiles they are below the cutoff.

Either way the better copy goes into your managed library, filed, tagged and verified like any download (the fingerprint checks it is the recording the song names, so a video edit is replaced by the album version), and the song points at it. Your file is never written, moved or deleted.

Retagging the file, or anything else that makes the scan read it again, keeps the **Replace** answer. **Skip** forgets it.

## Replaced files

**Replaced by a library copy**, below the table, lists the reference files whose song now uses a library file: a download replaced them, or the song had a library copy first. Nothing uses these files any more, so you can delete them; Wondarr never does. If Plex also scans the reference folder, it shows both copies until you do. Each row shows your file and its rating, the song, and the library copy's quality. When the library copy rates lower than your file (the song had a worse copy first), the row says **Your file rates higher**: keep that file. **Copy the paths on this page** copies the other files' paths, one per line, as the container sees them.

A replaced file you delete never counts towards the "share not mounted" check described above, so deleting many of them at once raises no warning. Its row goes missing like any other and is dropped once it has stayed missing for 24 hours, on a scan that does not see a mount problem.

## The Match queue

**Match** in the navigation lists the files Wondarr could not settle, with a count badge. Filter by **Reference library**, or turn on **Only likely YouTube rips**. Each row shows the file, its format (with a **Likely YouTube rip** badge when it is one), length, bitrate and sample rate, a **State** (**Needs review** or **No match**) and the top candidate.

- **Accept** takes the top candidate. **Select all on this page** and **Accept top candidate for selected** do it for many files.
- **Replace** takes the top candidate and asks for a better copy (see [Replacing files](#replacing-files)). **Replace selected** does it for many files.
- **Review** opens the file's drawer: what the file says about itself, the ranked candidates (with length difference, score and the reason for each), and a search box (**Artist – title**) to find the recording yourself. **Accept** or **Replace** a candidate or a search result, or **Skip** the file.

### Songs MusicBrainz doesn't have

Some songs are not on MusicBrainz at all, often ones a group only ever put on YouTube. Wondarr can't match a file to a song MusicBrainz doesn't know, but it can help you add the song there:

1. In the file's drawer, choose **Add to MusicBrainz…**. The form starts from the file's tags (or its name): **Title** and **Artist**.
2. Optionally paste the song's **YouTube link**. With the YouTube source on, **Read video** fills in the **Release date** from the day it was uploaded and shows the channel, so you can check it is the right video. With the source off, Wondarr doesn't contact YouTube, and the link is sent as you typed it.
3. **Open MusicBrainz** opens MusicBrainz' release editor in a new tab with the song filled in as a digital single, with one track of the file's length. You need a MusicBrainz account; if you are signed out, MusicBrainz keeps the form while you sign in.
4. In the editor, link the artist (search for them, or add a new artist), check the rest and submit. Wondarr never submits anything itself.
5. MusicBrainz sends you back to Wondarr's Match page, which settles the file with the new release. If MusicBrainz doesn't show the release yet, wait a minute and choose **Try again**. Until the file is settled or you dismiss the notice, Wondarr also tries again each time you open Match in this browser, so closing the tab loses nothing.

A release with several tracks settles the file with the track titled like it. If none is, search for the recording in the drawer instead. A way back from MusicBrainz that this browser didn't start (a link someone sent you, for example) is shown, and nothing is settled until you choose **Settle**.

MusicBrainz learns the address you reach Wondarr at, because that is where it sends you back to. Nothing else about your library goes to MusicBrainz, and the edit note it publishes holds only the song's link and where its details came from.
