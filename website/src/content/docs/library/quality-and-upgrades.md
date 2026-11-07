---
title: Quality and upgrades
description: Quality profiles, cutoffs, automatic upgrades, and how a download is checked before it is imported.
---

## Qualities

Wondarr ranks every file by a quality, from worst to best. They are grouped roughly like Lidarr's:

| Group | Examples |
|---|---|
| Poor lossy | MP3-96 to MP3-160, Vorbis Q5, OPUS-96 |
| Low lossy | MP3-192, MP3-224, AAC-192, OPUS-128 |
| Mid lossy | MP3-256, MP3-VBR-V2, AAC-256, OPUS-160 |
| High lossy | MP3-320, MP3-VBR-V0, AAC-320, OPUS-192+ |
| Lossless | FLAC, ALAC, APE, WavPack |
| Hi-res lossless | FLAC 24-bit, ALAC 24-bit |
| Uncompressed | WAV, AIFF |

Opus has its own tiers so YouTube is ranked honestly. A YouTube grab is `OPUS-160` (YouTube's best), whatever you convert it to afterwards. See [Conversion](/wondarr/library/conversion/).

## Quality profiles

Under **Settings → Quality profiles** a profile has a **Name**, an **Upgrades allowed** switch, the qualities that are allowed, a **Cutoff** ("the quality that counts as done"), a **Minimum score** and a **Duration tolerance** (how far a candidate's length may differ, in seconds). Two profiles exist from the start:

- **Standard 320**: the cutoff is MP3-320. AAC-256 counts as meeting it. FLAC is allowed and upgrades are on.
- **Lossless**: the cutoff is FLAC. High lossy files are accepted as a stop-gap and Wondarr keeps looking for lossless.

A song is **wanted** until it has a file that meets its profile's cutoff.

## Wanted

The **Wanted** page has two tabs:

- **Missing**: monitored songs with no file.
- **Cutoff Unmet**: songs that have a file below the profile's cutoff.

Each row has a **Search** button (automatic search) and an **Interactive search** button.

## Upgrades

Wondarr looks for a better file for songs in **Cutoff Unmet** with the scheduled **Upgrade Search** task. It runs every `search.upgrade_interval_hours` (24) and takes at most `search.upgrade_batch_size` (50) songs, oldest first. It looks at monitored songs whose profile allows upgrades; it skips files from a [reference library](/wondarr/library/reference-libraries/), which are yours, unless that library has **Replace files below the cutoff** on or the file was settled with **Replace** in the Match queue ([Replacing files](/wondarr/library/reference-libraries/#replacing-files)). Such a song also shows under **Cutoff Unmet**.

An automatic upgrade is only taken when:

- the current file does not already meet the cutoff. An automatic grab stops at the cutoff: the scheduled searches, an import list's search and the song's **Search** button never replace a file that is at or above the cutoff, even with a better release on offer. (**Search** on such a song says "Already at the cutoff" and searches nothing.) The interactive search is your own and still lists every release, and you can grab any of them;
- the new file's quality is strictly higher than the current one;
- it does not look like a clearly worse match for the recording (a higher bitrate of a live take or different edit is not an upgrade);
- its AcoustID fingerprint confirms it. A replacement that AcoustID does not know is rejected and blocklisted for the song, because it would replace a file you already have with one nobody can confirm.

A manual grab from the interactive search is exempt from these rules. When an upgrade is imported, the old file goes to the recycle bin (see [Tasks, backups and logs](/wondarr/operations/tasks-backups-logs/)) and the History records "upgraded".

## Interactive search

The interactive search button on a song asks every enabled source and lists all candidates with a score, the file, the peer, quality, size, length and a **Rejections** column. Hover a rejection to see why it was refused, for example blocklisted, format not allowed, not an upgrade, duration out of tolerance, version mismatch (live, remix and so on), artist mismatch, size sanity, ignored user, no seeders or below the minimum score. You can still grab a rejected candidate after a confirmation.

## How a download is checked

Before a download is imported, Wondarr:

1. Probes it with ffprobe. It must decode, and the measured quality replaces the guessed one.
2. Checks the length against the song's, within the profile's duration tolerance.
3. Checks the AcoustID fingerprint, if you have set an AcoustID client key (`acoustid.client_key`). It passes when the wanted recording matches with a score of at least `acoustid.accept_score` (0.7). If AcoustID does not know the recording at all, or you have set no key, the length is not enough on its own: the file also has to name the song's artist, in its artist or album-artist tag or, failing those, in its file name. A file that names another artist is refused, because songs with the same title by other artists are everywhere. A file that names no artist at all is imported but marked for review, or refused when `acoustid.strict` is on.

A wrong file is refused: History shows a **Rejected** event with the reason, the candidate is blocklisted for that song (see **Activity → Blocklist**), and Wondarr tries the next candidate, up to `search.max_auto_attempts_per_search` (4) per search, and then the next source. When the reason is "Fingerprint belongs to a different recording" (AcoustID knows the file as another recording, not as a weak match for the wanted one) and the file came from Soulseek, Wondarr also blocklists the file's folder and file name (with the folder above it when the parent names no album, such as `CD1`, `FLAC` or `Music`), so the same file offered by another user is skipped for that song. That entry shows on **Activity → Blocklist** like any other and can be removed there. A file named only with the song's title is not remembered this way.

From any source, the same verdict teaches the song two more things, each an entry on **Activity → Blocklist** for that song:

- **The other recording's title**, when it is not the song's own. If AcoustID says a download for "Please Come Tonight" is "Please Come Out Tonight", no file titled "Please Come Out Tonight" is downloaded for that song again, whoever shares it. A different recording with the song's own title (a cover, another artist's song of that name) teaches no title, since a right file has that title too.
- **A compilation miss**, when the file was on a "Various Artists", "VA", "Compilation" or soundtrack path and only its title let it through (neither its name nor a whole folder named the artist). After two misses, such files need the artist like any other for that song.

Removing an entry undoes what it taught.

## Fake-lossless check

A lossless download (FLAC, ALAC, WAV, AIFF, WavPack, APE) can be a lossy file re-saved as lossless. Wondarr decodes a 60 second window of the file and looks for the sharp cut-off in the spectrum that a lossy encoder leaves. If the spectrum stops below 19.5 kHz, the import is refused with a reason such as "Fake lossless: the spectrum stops at 16.2 kHz", the file is blocklisted for the song and the next candidate is tried.

- This catches MP3 at 192 kbps or less. A 320 kbps MP3 or an AAC transcode cannot be told from a genuine master and is accepted.
- The setting is `import.fake_lossless_check`: `reject` (default) or `off`.
- Your own [reference library](/wondarr/library/reference-libraries/) files are never checked.
