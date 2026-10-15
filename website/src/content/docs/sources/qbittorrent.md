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

## Torrents Wondarr did not add

Wondarr changes only torrents it added itself, or that a song it grabbed at the same time added for it. If the release is already in qBittorrent (you added it, or an earlier grab left it), Wondarr only raises the wanted file to priority 1. It never switches another file off, never changes a higher priority you set, and never removes the torrent, not even when you cancel the download in Wondarr. If that torrent is stopped and the wanted file still needs data, Wondarr starts it and says so in the log.

When Wondarr cancels a download of a torrent it added, it removes the torrent with its (empty) data only if no other song uses it, every file is at 0 %, and qBittorrent is not checking, moving, fetching metadata or reporting an error. Otherwise it only switches that song's file off.

## "Keep incomplete torrents in"

With this qBittorrent option on, a file that has reached 100 % stays in the incomplete folder until the whole torrent is done, then qBittorrent moves everything to the save path. Wondarr looks for the finished file in the incomplete folder while the torrent is incomplete, and in the save path once it is complete. A file that is not found yet while the torrent is incomplete or moving is a wait, not a failure.

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

### Clean up leftover folders

The switch at the top of **Settings → Download clients** is `import.clean_leftover_folders` in `config.yml` (default on). After an import, Wondarr removes what the download left behind: [a usenet job's folder](/wondarr/sources/sabnzbd/#leftover-folders), and its own download folders (the Soulseek, YouTube and staging folders, `wondarr/<guid>`) even when a partial or a cover is still in them. With the switch off, those folders go only while they are empty.

It never touches a torrent. The data stays for as long as qBittorrent has the torrent, seeding included: only the hard link or copy in Wondarr's staging folder goes. When Wondarr removes a torrent it added itself and nothing was downloaded, it deletes that torrent's (empty) data, as before. If the environment sets `APP__IMPORT__CLEAN_LEFTOVER_FOLDERS`, the switch is read-only.

Torrents come from an indexer. Add one next: [Indexers](/wondarr/sources/indexers/).
