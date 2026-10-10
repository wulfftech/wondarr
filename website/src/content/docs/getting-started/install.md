---
title: Install with Docker
description: Run Wondarr with Docker or Docker Compose.
---

Wondarr ships as one image with everything bundled: the API and web UI, slskd (the Soulseek client), ffmpeg, fpcalc and the tools the YouTube source needs. There is nothing else to wire up for Soulseek.

- Image: `ghcr.io/wulfftech/wondarr:latest` for releases, `ghcr.io/wulfftech/wondarr:develop` for the main branch.
- Architectures: `linux/amd64` and `linux/arm64`.

## Docker Compose

```yaml
services:
  wondarr:
    image: ghcr.io/wulfftech/wondarr:latest
    container_name: wondarr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Etc/UTC
    volumes:
      - ./config:/config
      - /srv/data:/data
    ports:
      - "1077:1077"
      - "50300:50300"
    restart: unless-stopped
```

Start it, then open `http://192.168.1.10:1077` (use your host's address).

```bash
docker compose up -d
```

## Docker run

```bash
docker run -d --name wondarr \
  -e PUID=1000 -e PGID=1000 -e UMASK=002 -e TZ=Etc/UTC \
  -v /srv/wondarr/config:/config \
  -v /srv/data:/data \
  -p 1077:1077 -p 50300:50300 \
  --restart unless-stopped \
  ghcr.io/wulfftech/wondarr:latest
```

## Volumes

| Path | What lives there |
|---|---|
| `/config` | The database (`wondarr.db`), `config.yml`, logs, backups and the bundled slskd's state (`/config/slskd`). |
| `/data` | Your music and every download. |

Use one `/data` mount for everything, in the usual \*arr layout. The defaults expect these folders under it:

| Default path | Used for |
|---|---|
| `/data/music` | The default library root (the library named "Music"). |
| `/data/downloads/slskd` | Where the bundled slskd saves completed Soulseek downloads. |
| `/data/downloads/youtube` | Where yt-dlp downloads to. |
| `/data/downloads/containers` | Where a song's file from a torrent or usenet post is staged before the import (`import.container_staging_path`). |

One mount matters because torrent imports are hard links: Wondarr links the finished file out of the torrent client's download folder so the torrent keeps seeding. A hard link cannot cross filesystems, so the torrent client's downloads and Wondarr's staging folder must be on the same mount. When a link is not possible Wondarr copies the file instead. See [qBittorrent](/wondarr/sources/qbittorrent/).

## Ports

| Port | Use |
|---|---|
| `1077` | The web UI and the API. |
| `50300` | The listen port of the bundled slskd (Soulseek). Forward it on your router to this host for better Soulseek results. |

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `PUID` / `PGID` | `1000` / `1000` | The user and group the app runs as. Files in `/config` and the files Wondarr places in `/data` belong to them. |
| `UMASK` | `002` | The umask the app runs with. |
| `TZ` | `Etc/UTC` | The time zone. |

Any setting in `config.yml` can also be set from the environment as `APP__<SECTION>__<KEY>`, with the key in upper case. For example `APP__SOULSEEK__USERNAME`, or `APP__SERVER__URL_BASE`. A setting taken from the environment wins over `config.yml`, and the settings page shows it as read-only.

## URL base behind a reverse proxy

To serve Wondarr under a path such as `https://wondarr.example/wondarr`, set the URL base. In `/config/config.yml`:

```yaml
server:
  url_base: /wondarr
```

or in the environment:

```yaml
environment:
  - APP__SERVER__URL_BASE=/wondarr
```

It must be empty or a path starting with `/` (for example `/wondarr`). Restart the container after changing it.

## The API key

On first start Wondarr writes `/config/config.yml` with a generated API key under `server.api_key` (32 lowercase hexadecimal characters). **Settings → General** shows it (**Show**, and a copy button); you can also read it from the file:

```bash
grep api_key /srv/wondarr/config/config.yml
```

Tools such as autobrr send it in the `X-Api-Key` header. The API reference is served at `/docs` on the same port; it describes the API, and every call still needs the key.

## Healthcheck

The image checks `http://localhost:1077/ping` every 30 seconds.

## Upgrading

### Finding out there is a new version

Wondarr never updates itself: it runs in a container, so you pull the new image. It does find out when there is one. Twice a day it asks GitHub's public releases list for the newest release and compares it with the version you run. When a newer one exists:

- An **Update available** badge appears in the header, next to the health badge. It links to **System → Updates** and can be dismissed for that version; a newer release shows it again.
- **System → Updates** shows the version you run, the latest version, when Wondarr last checked, the release notes and how to update. **Check now** asks GitHub straight away (a check made less than a minute ago is reused).
- **System → Status** has an **Update** row with the same answer.
- A notification is sent, once per new version, to every [notification](/wondarr/operations/notifications/) that listens to **On update available**.

A release candidate (`0.1.0-rc.1`) hears about newer candidates and the final release; a stable install only hears about stable releases. A development build (the `develop` or `sha-` images) is never told it is out of date, because its version cannot be compared with a release; the Updates page still shows the latest release.

Only the public releases list is asked for; nothing about your library, your settings or this install is sent. To turn it off, clear **Check for new versions on GitHub** under **Settings → General → Updates**, or set `update.check_enabled: false` in `config.yml` (environment: `APP__UPDATE__CHECK_ENABLED`; a value set there is read-only in the UI). With the check off Wondarr makes no request to GitHub at all.

### Applying an update

Pull the new image and recreate the container:

```bash
docker compose pull
docker compose up -d
```

If the image is pinned to a tag such as `:0.1`, change the tag to move to a new minor version; `:latest` follows every release. With `docker run`, pull the image, stop and remove the container, then create it again with the same command. On Unraid, open the **Docker** tab, choose **Check for updates**, then apply the update on the Wondarr container.

The database migrates itself when the new version starts. Wondarr does not take a backup first, so make one before a major upgrade: the **Backup** page in the web UI (in the left-hand navigation) has a **Back up now** button. Scheduled backups also run weekly into `/config/backups`.
