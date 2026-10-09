---
title: Unraid
description: Installing Wondarr on Unraid from Community Applications or from its template URL.
---

Wondarr has an Unraid template with the paths and ids already set for Unraid. The image is the same one described on [Install with Docker](/wondarr/getting-started/install/).

## Install

**From Community Applications**, once Wondarr is listed there: open the **Apps** tab, search for "Wondarr" and press **Install**.

**From the template URL**, any time:

1. Open the **Docker** tab and press **Add Container**.
2. In the **Template** drop-down, choose to add a template by URL and enter:

   ```
   https://raw.githubusercontent.com/wulfftech/wondarr/main/unraid/wondarr.xml
   ```

3. Check the settings below and press **Apply**.

Then open the web UI from the container's menu, or at `http://<your-server>:1077`, and carry on with [First run](/wondarr/getting-started/first-run/).

## The template's defaults

| Setting | Default | Notes |
|---|---|---|
| WebUI port | `1077` | The web UI and the API. |
| Soulseek listen port | `50300` | Forward it on your router for better Soulseek results. |
| Config (`/config`) | `/mnt/user/appdata/wondarr` | The database, `config.yml`, logs and backups. |
| Data (`/data`) | `/mnt/user/data` | Your music and every download. |
| `PUID` | `99` | Unraid's `nobody` user. |
| `PGID` | `100` | Unraid's `users` group. |
| `UMASK` | `002` | |
| `TZ` (advanced) | `Etc/UTC` | Set your own time zone, for example `Australia/Sydney`. |
| `APP__SOULSEEK__USERNAME`, `APP__SOULSEEK__PASSWORD` (advanced) | empty | Optional. You can enter the Soulseek account in the web UI instead. A value set here locks that field in the UI. |

## Use one `/data` share

Mount a single `/data` for everything, as in the usual \*arr layout: your music in `/data/music` and downloads in `/data/downloads`. Point your torrent client's downloads at the same share. A torrent import is a hard link out of the client's folder, and a hard link cannot cross filesystems. If you give Wondarr and the client separate shares, imports fall back to copying. See [Install](/wondarr/getting-started/install/) for the folder layout.

In Plex, point the music library at the same folder. If Plex sees it under another path, set **Library folder as Plex sees it**; see [Plex](/wondarr/sources/plex/).
