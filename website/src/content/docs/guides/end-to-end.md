---
title: End to end
description: From an empty host to a song from each source in a Plex library.
---

This walks through the whole path once. Each step links to the page with the detail.

## 1. Install Wondarr and qBittorrent on one `/data`

Put both services in one Compose file with the same `/data` mount, so imports from torrents are hard links. The full file is on the [qBittorrent](/wondarr/sources/qbittorrent/) page. Start it:

```bash
docker compose up -d
```

Open `http://192.168.1.10:1077`. Forward port `50300` on your router to the host for better Soulseek results. More in [Install](/wondarr/getting-started/install/) and [First run](/wondarr/getting-started/first-run/).

## 2. Sign in to Soulseek

Open **Settings → Soulseek**, enter the **Username** and **Password** of a dedicated Soulseek account, and press **Save**. The status card should show "Logged in as" your account. See [Soulseek](/wondarr/sources/soulseek/).

## 3. Connect Plex and map the library

1. **Settings → Plex**: **Sign in with Plex**, approve the code on plex.tv, press **Connect** next to your server, then **Test**.
2. **Settings → Library**: choose the **Music** library, pick the **Plex music section**, and fill in **Library folder as Plex sees it** if Plex mounts the music folder at a different path. **Save**.
3. In Plex, turn on **Prefer local metadata** for that music section before the first scan.

See [Plex](/wondarr/sources/plex/).

## 4. Add qBittorrent and an indexer

1. **Settings → Download clients → Add download client → qBittorrent**: host, port, username and password. Press **Test**, then **Save**. See [qBittorrent](/wondarr/sources/qbittorrent/).
2. **Settings → Indexers → Add indexer**: a Torznab row with your Prowlarr or Jackett URL and API key, or a Prowlarr row. Press **Test**, then **Save**. See [Indexers](/wondarr/sources/indexers/).

If you would rather have autobrr push releases, point its Lidarr action at `/api/v1/release/push` instead (see [Indexers](/wondarr/sources/indexers/)).

## 5. Add three songs

Open **Add songs**. On the **Paste a list** tab, enter one `Artist - Title` per line, choose a **Quality profile**, and press **Add all**. Or use the **Search** tab and press **Add** on the right recording.

Wondarr searches each song at once. It asks Soulseek first, then YouTube (if you turned it on, see [YouTube Music](/wondarr/sources/youtube/)), then your indexers.

## 6. Watch it work

- **Activity → Queue** shows what is downloading. **History** records every grab, import and failure with the reason, and **Blocklist** the candidates that were rejected.
- **Wanted** lists songs still without a file.
- When a file arrives, Wondarr checks that it is the recording you asked for, tags it, and files it in your library. Then it asks Plex to scan the folder, after a few seconds' quiet.

## 7. See them in Plex

Open your Plex music library (or Plexamp). The songs appear under the album each song was filed under, using the Plexamp layout and the **Fewest albums** policy. A song that has no album of its own appears under a per-artist Singles album.
