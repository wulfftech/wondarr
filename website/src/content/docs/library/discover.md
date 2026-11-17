---
title: Discover
description: Hear suggested songs one by one, add the ones you like, and start a radio from any song.
---

**Discover** in the left-hand navigation suggests songs you do not have. Listen to a suggestion, add it, or turn it down.

## Mix and radio

The page opens on a **Mix from your library**: suggestions drawn from a random sample of your songs. **Refresh** draws a new sample. The mix seed sits in the address (`?mix=…`), so a reload shows the same mix.

A **radio** is seeded by one song. Start one from:

- the song picker at the top of the page (search by title or artist);
- **Discover from this** in a song's actions menu on the **Library** page;
- **Discover from this** on the song page;
- **Radio from this** on a suggestion, once you have added it.

**Back to mix** leaves the radio.

## Where suggestions come from

| Source | Needs |
|---|---|
| ListenBrainz | Nothing. Always on. |
| Last.fm | A Last.fm API key under **Settings → General → Metadata services**. |
| Deezer | A seed song that has a Deezer id. Deezer suggests by artist, not by track, so it ranks below the other two. |

Discover works with no keys at all. When a source is off or did not answer, a notice above the list says so. Suggestions from several sources are merged into one row, and each row shows which sources it came from.

Suggestions you already have are dropped: every song Wondarr knows (including reference-library files it has identified), the seed itself, and anything you turned down. A reference file still waiting in **Match** is not known yet. A radio shows at most three songs per artist. Results are kept for six hours per seed. **Show more** raises the list from 50 up to 200 rows.

## What you can do with a suggestion

Each row shows the cover, title, artist, album and length.

- **Preview** plays a short clip. Only one plays at a time. Wondarr looks the link up when you press the button and never stores it.
- **Add** adds the song with the **Quality profile** and **Library** chosen above the list. If the suggestion carries a MusicBrainz or Deezer id it is added at once. If it carries neither, Wondarr searches for `Artist - Title` and you pick the right recording. The row then reads **Added**, with a link to the song.
- **Not interested** removes the row and a toast offers **Undo**.
- **Radio from this** starts a radio from the song. It is available after you add the suggestion.

Wondarr asks MusicBrainz nothing until you press **Add**, so browsing costs it nothing.

## Not interested

**Not interested (n)** at the top of the page opens the list of songs you turned down. **Undo** on a row lets that song come up again.
