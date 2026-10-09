---
title: qBittorrent
description: Add qBittorrent as a download client, with a shared /data layout so imports are hard links.
---

Wondarr uses qBittorrent as its torrent client. It does not download whole torrents: it adds the torrent, selects only the file of the song it wants, and imports that file when it is done. qBittorrent 4.5 or newer is needed, and both the 4.x and 5.x series work.

## What Wondarr does with a torrent

1. A `.torrent` is added stopped; a magnet link is added running, but halted once its metadata arrives. Either way Wondarr then selects only the wanted file (other files get priority 0) and starts the torrent.
2. When the wanted file reaches 100 %, Wondarr hard-links it into its staging folder, and imports it from there. If a hard link is not possible (another filesystem) it copies the file instead.
3. The torrent keeps seeding until qBittorrent's own seeding goals are met. Wondarr never moves or deletes torrent data.

Other wanted songs by the same artist that Wondarr finds in the same torrent are selected in the same grab.

## One `/data` mount

The staging folder is `import.container_staging_path` in `config.yml`, default `/data/downloads/containers`. Hard links only work inside one filesystem, so qBittorrent's download folder must be on the same mount as that staging folder. The usual layout is a single `/data` mounted into both containers:

```yaml
services:
  wondarr:
    image: ghcr.io/wulfftech/wondarr:latest
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      - ./wondarr-config:/config
      - /srv/data:/data
    ports:
      - "1077:1077"
      - "50300:50300"

  qbittorrent:
    image: lscr.io/linuxserver/qbittorrent:latest
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
      - WEBUI_PORT=8080
    volumes:
      - ./qbittorrent-config:/config
      - /srv/data:/data
    ports:
      - "8080:8080"
```

Set qBittorrent's default save path to a folder under `/data`, for example `/data/downloads/torrents` (in qBittorrent: **Tools → Options → Downloads → Default Save Path**; the linuxserver image starts with `/downloads`). With both containers seeing `/data` at the same path, no remote path mapping is needed.

On its first start, qBittorrent 5 has no Web UI password: it prints a temporary one to its log (`docker compose logs qbittorrent`). Sign in at `http://192.168.1.10:8080` as `admin` with it, and set your own under **Tools → Options → Web UI**: Wondarr needs that username and password.

## Settings → Download clients

Press **Add download client**, choose **qBittorrent**, then **Continue**.

| Field | Meaning |
|---|---|
| **Name** | A label for this client. |
| **Enabled** | Turn the client off without deleting it. |
| **Priority** | 1 to 50. When several clients serve a protocol, the lowest number is the default. |
| **Host** | Where qBittorrent's Web UI listens, for example `qbittorrent` or `192.168.1.10`. Required. |
| **Port** | The Web UI port. Default `8080`. |
| **Use SSL** | Whether the Web UI is served over HTTPS. |
| **URL base** | Under **Advanced**: the Web UI's URL base, if it has one. |
| **Username** / **Password** | The Web UI login. Leave the username empty if qBittorrent needs none. |
| **Category** | The qBittorrent category Wondarr's torrents are filed under. Default `wondarr`; it must not contain `/`. |
| **Remote path mappings** | Pairs of **Path in the client** and **Path Wondarr sees**. |

### Remote path mappings

Add a mapping when qBittorrent reports a path that is different inside Wondarr's container. For example, if qBittorrent saves to `/downloads/torrents` and Wondarr sees the same folder at `/data/downloads/torrents`, map `/downloads/torrents` to `/data/downloads/torrents`. The longest matching prefix wins. Both paths must be absolute. If you can, mount the same folder at the same path in both containers and skip mappings.

### Test

Press **Test** before **Save**. It logs in, checks the version, creates the category if it is missing, and checks that the folder qBittorrent saves that category to exists where Wondarr can see it. If it does not, the message says "qBittorrent saves to … which Wondarr sees as … — that folder does not exist here; add a remote path mapping."

Torrents come from an indexer. Add one next: [Indexers](/wondarr/sources/indexers/).
