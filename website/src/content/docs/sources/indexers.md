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

## Settings → Indexers

Press **Add indexer** (the button on the page), choose a type, and fill in the form. Every type has:

- **Name**, **Enabled**.
- **Priority**: 1 to 50. A lower number is asked first.
- **Protocol**: **Torrent** or **Usenet**. Only shown for types that can be either (Prowlarr).
- **Download client**: which client takes this indexer's grabs. "Default" uses the first enabled client for the indexer's protocol.

Press **Test** to check the connection before saving. Keys and passwords are stored on the server and never shown again.

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

Choose **Torrent** or **Usenet** for the row; make a second Prowlarr row if you want both.

### Gazelle

A Gazelle tracker such as Redacted or Orpheus, searched directly by song title in its file lists.

- **URL**: the tracker's address.
- **API key**: from the tracker's settings; it needs the torrents scope.
- **Use freeleech tokens** (Advanced): spend a token on each download that is not freeleech already.

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

`title` is required; without it Wondarr answers `400`. A `magnetUrl` can be sent instead of `downloadUrl`. Wondarr matches the release against your wanted songs by its artist and album, reads its file list, and grabs it if a wanted song is in it. The answer has Lidarr's shape (`approved`, `rejected`, `rejections`), so autobrr shows why a release was turned down. A pushed release needs a download client for its protocol.
