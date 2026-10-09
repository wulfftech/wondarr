---
title: Plex
description: Connect Wondarr to a Plex server, link a library to a music section and map its path.
---

Plex is where your songs end up. Wondarr signs in to plex.tv, picks your server, and tells Plex to scan the folder of each song it imports. There are two places to set it up: **Settings → Plex** for the account and server, and **Settings → Library** for which Plex music section each library feeds.

## 1. Sign in and pick the server

1. Open **Settings → Plex** and press **Sign in with Plex**. A plex.tv tab opens and Wondarr shows a code ("Enter this code on plex.tv to approve Wondarr"). Approve it there. If the code expires, press **Get a new code**.
2. If you cannot open a browser tab on the machine, expand **Paste a token instead**, paste your Plex token and press **Save token**.
3. Once signed in, the **Server** card lists the servers your account can see, each with its connections marked Local, Remote or Relay. Press **Connect** next to your server: Wondarr tries its connections and uses one that works. You can also pick a connection by hand, or type a **Server URL** (for a server behind a reverse proxy, or one plex.tv does not list, for example `http://plex.lan:32400`) and press **Use this server**.
4. Press **Test**. A good result reads like "Connected to your server (Plex 1.x), 1 music libraries".

**Sign out** forgets the Plex sign-in and stops refreshing the linked libraries. Nothing already in the library is touched.

## 2. Link a library to a Plex music section

Open **Settings → Library**, choose the library, and find the **Plex** block.

- **Plex music section**: the music section in Plex that this library feeds. Each option shows the section's folder in Plex. Leave it as "Not linked" to skip Plex for this library.
- **Library folder as Plex sees it**: the path Plex uses for this library's root folder. This is the Plex path mapping. If Wondarr has the library at `/data/music` and your Plex container mounts the same folder at `/music`, enter `/music`. Leave it empty when Plex sees the same path as Wondarr. The box shows the section's own folder as a hint.

Press **Save**. Without the right path, a scan request names a folder Plex does not have and silently scans nothing.

## What Wondarr does in Plex

After a song is placed, Wondarr asks Plex for a partial scan of that song's folder. The scans are batched: Wondarr waits for 10 seconds of quiet (and at most 60 seconds from the first request), asks once per folder, and scans the library root instead when more than 25 folders are waiting. A failed scan is retried, and a persistent failure becomes a health warning.

## Prefer local metadata

Plex groups and names albums by their tags only when **Prefer local metadata** is on. Turn it on (and **Use local assets**) in the Plex library's advanced settings before the first scan, or Plex may rename or split albums. Wondarr files songs from different real releases into pseudo-albums such as "Singles", and Plex would otherwise replace those with its own idea of the album. Settings → Library shows a dismissable reminder about this. A Flat or Artist layout depends on the setting completely.

## The Plexamp layout

The Plexamp layout keeps an `Album Artist/Album/` folder level, so Plex and Plexamp see tidy albums. See [First run](/wondarr/getting-started/first-run/) for the library's other settings.

## Playlists

An import list can keep a Plex playlist in step with it. In **Settings → Import lists**, tick **Keep as a Plex playlist** on the list: Wondarr creates a Plex playlist with the list's name and updates it in place.
