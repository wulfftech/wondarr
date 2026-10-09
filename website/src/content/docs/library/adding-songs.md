---
title: Adding songs
description: Search for a song, add an album's tracks, paste a list, and review what Wondarr could not place.
---

Everything starts at **Add songs** in the left-hand navigation. It has three tabs: **Search**, **Album** and **Paste a list**. A song is always one MusicBrainz recording (or, when MusicBrainz does not know it, a Deezer track).

## Search

Type into the **Search** box. It accepts `Artist - Title`, a MusicBrainz or Deezer link, or an ISRC. Each candidate shows its cover, title, artist, length, year, release types and version flags (live, remix and so on) and which provider found it: **MusicBrainz** or **Deezer only**. Use the preview button to hear it, then press **Add** on the right recording.

If you have more than one library, a library picker appears next to the search box. A song that is already in the library shows an **In library** link instead of **Add**.

## Album

The **Album** tab adds a whole album as its tracks.

1. Search for `Artist - Album` or an album title and choose a result. Albums come from MusicBrainz, with Deezer as the fallback.
2. Pick a **Release** ("The release the tracks and tags come from"). The default is the earliest official release on CD or digital media; ties go to the release with the most tracks.
3. Tick the tracks you want. **Select all** and **Select none** are there to help. Tracks already in your library show **In the library** and cannot be ticked.
4. Choose the **Library** (only shown when you have more than one), the **Quality profile** and **Monitored**, then press **Add N songs**.

Each track becomes its own song. They are filed under that release as their album and the album choice is pinned, so the [Compact library](/wondarr/library/layouts-and-albums/) task will not scatter them.

### Add the rest of an album

On the **Library** page, the actions menu of a song that is filed under a real release has **Add the rest of this album**. It opens the Album tab for that release.

## Paste a list

Paste one `Artist - Title` per line, up to 1000 lines (the box shows a line count and refuses more). Choose the **Quality profile** and, with several libraries, the library, then press **Add all**.

Wondarr resolves the lines in the background, with a progress bar ("Resolved 12 of 50 lines"). When it finishes it reports how many were added (including ones the library already held), unresolved, skipped, and still to look at.

## Unresolved lines

A line Wondarr cannot place with enough confidence is **unresolved**. If any are left, press **Review N unresolved**. The **Unresolved** page lists each line with its reason and the closest candidates with a score:

- **Use this** adds that candidate for the line.
- **Search…** opens a search box to find the right recording yourself, then **Use this**.
- **Skip** drops the line.

The same review serves lines from [import lists](/wondarr/library/import-lists/).

## What "monitored" means

A **monitored** song is one Wondarr looks after: if it has no file it is searched for, and if its file is below the quality profile's cutoff it is searched for an upgrade. An unmonitored song stays in the library but is never searched for. You can add a song unmonitored (the **Monitored** switch on the Album tab) when you only want it on record.

## Search on add

Adding a monitored song queues a search for it straight away. This is `search.search_on_add`, on by default. With it off, the scheduled **Missing Search** task (every 6 hours by default) finds the song instead. See [Configuration](/wondarr/reference/configuration/).

Watch progress under **Activity → Queue**, and see songs still waiting under **Wanted**.
