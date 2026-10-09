---
title: Import lists
description: Keep Wondarr in step with a playlist, a CSV export or your own music folder.
---

An import list is a source Wondarr reads again and again: a playlist, a CSV file, a scrobble history. Each item is resolved to a recording and added as a song. Set them up under **Settings → Import lists**; **Add list** starts the form, and each list has **Sync now**, **Edit** and **Delete**.

## How an item is resolved

For each item Wondarr tries, in order: a MusicBrainz recording id, then an ISRC (MusicBrainz, then Deezer), then a text search on artist and title that also uses the item's length. An item that matches a song already in the library is linked to it ("already in the library"). Anything Wondarr is not sure about goes to the **Unresolved** page, where you pick a candidate or skip it. See [Adding songs](/wondarr/library/adding-songs/).

## Providers

| Type | What it reads | Settings |
|---|---|---|
| CSV file (Exportify or any mapped CSV) | An uploaded file | Column names for a generic file |
| Deezer playlist | A public Deezer playlist | A playlist link or id |
| Artist top tracks (Deezer) | An artist's top tracks | An artist name, link or id; how many (1 to 100, default 10) |
| YouTube Music playlist | A public or unlisted playlist | A YouTube Music or YouTube playlist link, or its id |
| Last.fm loved tracks | A user's loved tracks | User name; API key (optional when Settings → General has one) |
| Last.fm top tracks | A user's most played tracks | User name; API key (optional when Settings → General has one); period; how many (1 to 500, default 50) |
| ListenBrainz loved tracks | A user's loved recordings | User name |
| ListenBrainz playlist | A public playlist | A listenbrainz.org playlist link, or its MBID |
| Reference library | The identified files of a [reference library](/wondarr/library/reference-libraries/), in folder order | The reference library |

### CSV and Exportify

Choose **CSV file** and pick a file. Wondarr reads it in your browser and sends only its text, then shows a preview: the format, the number of rows and the first few rows.

- An **Exportify** export (a Spotify playlist exported with Exportify) is recognised by the `spotify:track:` URIs in its first column. Exportify writes its header row in your language, so Wondarr reads the base columns by position instead: a German or Japanese export imports as well as an English one. The title, artist, album, length and ISRC come from fixed columns.
- Any other CSV is **Mapped CSV**: name the columns by their header. **Title column** is required (or an ISRC or MusicBrainz id column). **Artist column**, **Album column**, **Length column** (milliseconds, seconds or `m:ss`) and **ISRC column** are optional, and **MusicBrainz recording id column** is under Advanced. An ISRC column is the most reliable way to match.

Choosing a new file on an existing list replaces the stored one.

### Last.fm

You need a Last.fm API key, from `last.fm/api/account/create`. Put it in **Settings → General → Metadata services** (`lastfm.api_key`) and every Last.fm list uses it; a list with a key of its own uses that one instead. With neither, the list reports that it needs an API key. The top-tracks **Period** is one of `overall`, `7day`, `1month`, `3month`, `6month` or `12month` (default `12month`).

## Sync interval

**Sync every (hours)** is 24 by default. It can be 0 to 720; **0 means only when I ask** (**Sync now**). A scheduled **Import List Sync** task runs hourly and syncs the lists that are due. A failed read still counts as a sync for the schedule, so a source that is down is not hammered.

## Policy

The **Policy** says what happens when an item leaves the source:

| Policy | Label in the form | Effect |
|---|---|---|
| `addOnly` (default) | Add only | New items are added. Nothing is undone. |
| `addAndUnmonitor` | Add, and unmonitor songs that leave the list | Songs added by this list that leave it are unmonitored. |
| `mirror` | Mirror: also delete songs that leave the list and have no file | As above, but a song with no file (and nothing downloading) is deleted. A song that has a file is unmonitored instead. |

**A list never deletes a file.** A song you added by hand, or one that another list still holds, is never touched.

## Quality profile and library

Each list has its own **Quality profile** and **Library**. Left empty, they are the defaults.

## Plex playlists and .m3u8

- **Keep as a Plex playlist**: Wondarr keeps one Plex playlist with the list's name, adding, removing and reordering items in place, so its poster and play history survive each sync. See [Plex](/wondarr/sources/plex/).
- **Write an .m3u8**: writes `<list name>.m3u8` into the `Playlists` folder under the library's root, with paths relative to it.
