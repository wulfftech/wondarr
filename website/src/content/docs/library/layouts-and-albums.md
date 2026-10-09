---
title: Layouts and albums
description: How songs are named and filed, how a single song gets an album, and keeping Plex tidy.
---

Each library has its own layout, naming template and album policy, under **Settings → Library**. Press **Add library…** for more libraries.

## Layouts

The **Layout** is a preset for the **Naming template**. You can edit the template afterwards; the page shows a live **Preview** of the path, and **Reset to the … preset** puts the layout's template back.

| Layout | Default template |
|---|---|
| Flat | `{Artist Name} - {Track Title}` |
| Artist | `{Artist Name}/{Artist Name} - {Track Title}` |
| Artist → Album | `{Artist Name}/{Album Title} ({Release Year})/{track:00} - {Track Title}` |
| Plexamp | `{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}` |

The Plexamp layout keeps an album folder level and chooses each song's album deliberately, so Plex does not split or rename albums. See [Plex](/wondarr/sources/plex/) for the "Prefer local metadata" setting.

The **Tokens** button lists the tokens you can insert:

`{Artist Name}`, `{Album Artist Name}`, `{Album Title}`, `{Release Year}`, `{track:00}`, `{medium:0}`, `{Track Title}`, `{Track ArtistName}`, `{Quality Title}`, `{Artist NameThe}`, `{Artist CleanName}`, and the optional group `[ ({Release Year})]`, which disappears when the year is empty.

## Album policy

Every song is tagged with exactly one album. The **Album policy** decides which one for a song that is not part of an album you added:

| Policy | What it does |
|---|---|
| Fewest albums (default) | Uses the fewest real albums per artist that cover the songs you own, and puts the rest in a "Singles" album for that artist. A real album is only used when it holds at least **Minimum tracks per real album** owned tracks (default 2). |
| Singles only | One Singles album per artist: the fewest folders, no real album identity. |
| Original album | The earliest official album or EP each song appears on. |
| Single release | The song's own MusicBrainz single, one folder per song. |
| Compilation | Everything under one "Various Artists" compilation. |

### Sticky albums and your own choice

An album assignment is sticky: adding a song never moves the songs already filed. To choose a song's album yourself, open its actions menu on the **Library** page and pick **Change album…**. Your choice is pinned, so the Compact task keeps it.

## Compact library

**Compact library…** (on the library's settings tab) re-plans every song's album under the current policy, as if the library were built today. It first shows a dry run: each song, where it is now and where it would go. Songs you placed on an album yourself are kept. If you confirm, files move to their new album folders and Plex is told to forget the old albums first, because Plex never reconsiders an album on a rescan. This scans the linked Plex library, and folders it empties are cleaned up. Run it only when you want the change; nothing compacts on its own.

## Several libraries

Each library has its own root, layout, album policy, conversion rules and Plex section. A song belongs to exactly one library. You choose the library when you add a song, and a library picker appears wherever there is a choice.

To move a song, open its actions menu on the Library page and choose **Move to library…** (shown when you have more than one library). Its file is re-tagged and placed under the new library's root and template, and Plex is asked to scan both folders. A file in a [reference library](/wondarr/library/reference-libraries/) is never moved; only the song's library changes.

A library that still holds songs cannot be deleted, and neither can the default library. **Delete library…** never touches files on disk.

## Covers and lyrics

- Wondarr embeds the front cover in each file and writes `cover.jpg` in the album folder. The first file placed in a folder writes it and later files never replace it.
- Lyrics come from [LRCLIB](https://lrclib.net). Synced lyrics are written as a `.lrc` file next to the song, and plain lyrics as a `.txt`; an existing sidecar is never replaced. A missing or slow LRCLIB never fails an import. Turn lookups off with `lyrics.enabled: false` (see [Configuration](/wondarr/reference/configuration/)).
