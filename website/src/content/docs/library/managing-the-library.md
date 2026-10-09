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
