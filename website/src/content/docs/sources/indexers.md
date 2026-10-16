---
title: Indexers
description: Torznab, Newznab, Prowlarr and Gazelle indexers, and pushing releases from autobrr.
---

Indexers are Wondarr's last resort. For each song it asks Soulseek first, then YouTube, and only then the enabled indexers. Torrent and usenet indexers need a download client: [qBittorrent](/wondarr/sources/qbittorrent/) or [SABnzbd](/wondarr/sources/sabnzbd/).

## How a song is searched on an indexer

An indexer cannot be searched for a recording directly, so Wondarr works through albums:

1. From MusicBrainz it takes the releases the recording appears on: the album it is filed under first, then official albums, singles and compilations, at most three.
2. For each indexer and release it searches by artist and album (`t=music&artist=&album=` when the indexer's capabilities support it, otherwise a text search of "artist album"), in category 3000 (Audio) unless you list others.
3. When a release looks right, Wondarr reads its file list and looks for the wanted track inside it, by track number, title and a size window. Only then does the release become a candidate. For a torrent, only that file is downloaded.

A release whose file list does not contain the song is rejected and blocklisted.

File names don't have to be Wondarr's own. Scene names like `01-artist_name-track_title-GRP.flac` (underscores for spaces, hyphens between the fields) match too. Wondarr drops the track number, the release's artist and the release's group tag (the last part of the release name, `GRP` in `Artist-Album-CD-FLAC-2002-GRP`), then compares what's left. Same threshold, same version check (a live or remix file is never the studio song), same size window.

A release whose file list can't be read before the grab (a magnet, a RAR set, obfuscated names) stays a candidate. It scores lower, and its name has to carry the song: it starts with the song's artist, whole (a surname in common doesn't count), and it names the song's album or the song itself. Compilations, soundtracks and various-artists sets are turned away. Wondarr finds the song in it once the client has the file list or SABnzbd has unpacked the post. If it isn't there, the release is blocklisted for that song, what Wondarr added to the client is removed, and the next candidate is tried.

A release name can also say what every track is. `Live.at.Wembley`, `Unplugged`, `The.Remixes`, a `-LIVE` field or a bootleg make every file in it a live (or remix, or demo, or instrumental) take, so a studio song never matches it and a live song can.

### Usenet download limits

Reading a usenet post's file list means downloading its NZB, and most indexers cap NZB downloads per day (free plans allow 5 to 20). Wondarr reads at most 8 NZBs from each usenet indexer in one search, the posts that name a quality in their title first and then the most grabbed. It keeps each NZB it read for 6 hours, so grabbing that post, searching again, or a release pushed to Wondarr does not download it again, and a post that is already kept does not count towards the 8.

When an indexer says its limit is reached (a sentence such as "You already reached your daily restriction of 10 downloads.", or an error answer about your account or its limits), Wondarr stops reading from that indexer for the rest of the search, keeps using the others, and shows the indexer's own words in the search message and in the run's history. An error about a single post, such as a removed one, only skips that post. A grab whose download is not an NZB fails with the indexer's words, and nothing is added to SABnzbd.

An indexer that refused is also left alone after the search ends, for as long as its answer asked (`Retry-After`, 24 hours at most) or else for 1 hour: later searches still ask it for releases but read none of its NZBs, so its posts are left out unless their NZB is already kept. Wondarr logs when the cool-down ends (in the server's local time, with its UTC offset), and the search message says so too when nothing else was found. A restart of Wondarr forgets it. Behind a Prowlarr row each indexer counts on its own, with its own 8 reads and its own cool-down, so one that refuses does not stop the others.

## Settings → Indexers

Press **Add indexer** (the button on the page), choose a type, and fill in the form. Every type has:

- **Name**, **Enabled**.
- **Priority**: 1 to 50. A lower number is asked first.
- **Protocol**: **Torrent** or **Usenet**. Only shown for types that can be either (Prowlarr).
- **Download client**: which client takes this indexer's grabs. "Default" uses the first enabled client for the indexer's protocol.

Press **Test** to check the connection before saving. Keys and passwords are stored on the server and never shown again. If you change the address (the host, port or http/https), type the key again: a stored key is only ever sent to the address it was saved for. The same goes for download clients, notifications and your own slskd.

### Torznab

A torrent feed: Prowlarr's or Jackett's per-indexer URL.

- **URL**: the indexer's Torznab endpoint.
- **API key**: the key the indexer expects.
- **Categories**: comma-separated Newznab category IDs. `3000` is music.
- **Minimum seeders**: releases with fewer seeders are skipped.
- **API path** (Advanced): `/api` unless the indexer says otherwise.

### Newznab

A usenet feed (a usenet indexer, NZBHydra, or Prowlarr's per-indexer URL). The fields are the same as Torznab, without minimum seeders.

### Prowlarr

One row that searches through all of Prowlarr's indexers at once, using Prowlarr's own search.

- **URL**: Prowlarr's address, for example `http://prowlarr:9696`.
- **API key**: from Prowlarr's **Settings → General**.
- **Indexer ids**: comma-separated Prowlarr indexer IDs to ask. Empty asks all of them.
- **Categories**: as above.
- **Minimum seeders**: releases with fewer seeders are skipped, as for Torznab. Default 1. Usenet results are not filtered.

Choose **Torrent** or **Usenet** for the row; make a second Prowlarr row if you want both.

### Gazelle

A Gazelle tracker such as Redacted or Orpheus, searched directly by song title in its file lists.

- **URL**: the tracker's address.
- **API key**: from the tracker's settings; it needs the torrents scope.
- **Use freeleech tokens** (Advanced): spend a token on each grab that is not freeleech already. A search never spends one: Wondarr reads a release's files from the tracker's file lists, or downloads the `.torrent` without the token flag, and adds the flag only when it grabs.

## Pushing releases from autobrr

Instead of searching, you can have autobrr push new releases to Wondarr. Wondarr's endpoint is Lidarr-compatible, so use autobrr's Lidarr action, pointed at Wondarr:

```
POST http://192.168.1.10:1077/api/v1/release/push
X-Api-Key: your-api-key
Content-Type: application/json

{
  "title": "Artist - Album (2020) [FLAC]",
  "downloadUrl": "https://tracker.example/download/123.torrent",
  "protocol": "torrent",
  "indexer": "ExampleTracker",
  "size": 312000000
}
```

`title` is required; without it Wondarr answers `400`. A `magnetUrl` can be sent instead of `downloadUrl`. Wondarr matches the release against your wanted songs by its artist and album (or `Artist - Title` for a single), reads its file list, and grabs it if a wanted song is in it. A wanted song filed under the artist's Singles still matches an album release when its file is in the release's file list; a magnet has no file list until it is grabbed, so it must match by album or title. The answer has Lidarr's shape (`approved`, `rejected`, `rejections`), so autobrr shows why a release was turned down. A pushed release needs a download client for its protocol.
