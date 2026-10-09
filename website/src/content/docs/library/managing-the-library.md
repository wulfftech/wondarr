---
title: Managing the library
description: Filter the Library page, save views, tag songs, and change many songs at once.
---

The **Library** page lists every song Wondarr manages. Above the table is a filter bar; select rows to change many songs at once.

## Filters

| Filter | Shows |
|---|---|
| **Search** | Songs whose title or artist contains the text. |
| **Artist** | Songs an artist is credited on, as main artist or guest. |
| **All / Monitored / Unmonitored** | Songs by their monitored flag. |
| **Library** | Songs in one library. |
| **Quality profile** | Songs using one profile. |
| **File** | **Has a file** or **Missing its file**. |
| **Cutoff** | Songs with a file that has (**Cutoff met**) or has not (**Cutoff not met**) reached the profile's cutoff. A song without a file is in neither. |
| **Tag** | Songs carrying a tag. Clicking a tag in the **Tags** column filters by it too. |

Filters combine, and **Clear** resets them all. Changing a filter clears the selection.

## Saved views

**Views** lists the filters you saved. **Save view** stores the current filters under a name (each name once); choosing a view applies its filters, and **Delete view** removes the one in use.

## Tags

Tags are your own labels on songs, such as `wedding` or `car`. They are stored in lower case, at most 64 characters each with no commas, and at most 50 per song. Add them with the mass editor below; the **Tags** column shows them when any song on the page has one.

## Changing many songs at once

Tick the box on each row, or the box in the header for every song on the page; hold Shift to tick a range. The selection is kept while you page through the results. A bar then shows how many songs are selected and offers:

- **Monitor** and **Unmonitor**.
- **Quality profile** → **Apply profile**.
- **Library** → **Apply library** (with more than one library): the songs not already there are moved, files and all, as **Move to library…** does for one song. The bar follows the move.
- **Tags…**: add tags to the songs, remove them, or replace the songs' tags with the ones you pick.
- **Delete…**: removes the songs from Wondarr. Their files stay on disk. A song that is still downloading or importing is refused, and nothing is deleted: remove it from **Activity → Queue** first.
- **Clear selection**.

The same actions are in the API: `PUT /api/v1/song/editor` and `DELETE /api/v1/song/editor` (see [API and integrations](/wondarr/reference/api-and-integrations/)).

## The song page

Click a song's title anywhere it appears (the Library, Wanted, and Activity's Queue and History) to open its own page. A middle-click opens it in a new tab, and **Back** returns you to the same filtered Library.

The top of the page shows the album cover, the title with its version badges, the artist (a link to that artist's songs in the Library), the album with the track number and year, and the status: a **Monitored** switch, the quality profile (change it in place), the library, whether the file is **Downloaded** or **Missing**, whether it **meets its cutoff**, and its tags. The buttons there are:

- **Search** runs the automatic search; **Interactive search** shows every candidate right below the header, so you can pick one by hand.
- **Change album…**, **Convert…**, **Move…** (with more than one library) and **Delete** are the same dialogs as in the Library's row menu. A song on many releases gets a filter box in Change album.
- A **preview** button plays 30 seconds when Deezer has one.

Below that, the tabs are:

- **File:** the path, the quality Wondarr graded and what is on disk (codec, container, bitrate, sample rate, bit depth, channels, length, size), ReplayGain, the AcoustID and whether the fingerprint was verified, where the file came from (Soulseek, YouTube, a torrent, usenet, or your reference library), and when it was imported. A missing song shows the two search buttons instead.
- **About:** what MusicBrainz and Deezer know (first release date, ISRCs, BPM, a popularity figure, explicit flag, links out) and **Appears on**, the releases the song is on. It shows the current album and the first ten, originals before compilations; **Show all** expands the list and adds a filter box. A source that did not answer is simply left out.
- **Last.fm:** only when you have set a Last.fm key in Settings. Listeners, plays, tags, the wiki summary (with a link to read more on Last.fm), a short artist bio and similar tracks; one that you already own links to its own page.
- **Lyrics:** looked up only when you open the tab: a lyrics file next to the song, else LRCLIB. Timestamps are not shown.
- **History** and **Queue & blocklist:** only this song's entries; queue items and blocklist entries can be removed here just as on the Activity page.
