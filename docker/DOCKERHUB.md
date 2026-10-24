# Wondarr

**The \*arr for one-hit wonders.** A self-hosted \*arr that thinks in songs, not albums. Tell it which songs you want; it finds the best copy on Soulseek, YouTube Music, torrents or usenet, checks it's really the right recording (length and AcoustID), tags it, files it into your library and keeps looking for a better copy until it's happy with the quality.

![The library](https://raw.githubusercontent.com/wulfftech/wondarr/main/docs/screenshots/library.png)

This image is a mirror of `ghcr.io/wulfftech/wondarr`: the same image, same digest, copied here once it has passed the release checks. Pull from whichever registry you like.

## Tags

| Tag | What it is |
|---|---|
| `latest` | The newest stable release |
| `0.1` | The newest 0.1.x release |
| `0.1.14`, … | One exact release |
| `develop` | The main branch. Expect rough edges. |

`linux/amd64` and `linux/arm64`. One container, with slskd (Soulseek), ffmpeg, fpcalc and yt-dlp bundled.

## Run it

```yaml
services:
  wondarr:
    image: wulfftech/wondarr:latest
    container_name: wondarr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Etc/UTC
    volumes:
      - ./config:/config
      - /srv/data:/data          # your music and the downloads, on one mount
    ports:
      - "1077:1077"              # the web UI
      - "50300:50300"            # Soulseek; forward this on your router for better results
    restart: unless-stopped
```

Then open `http://<your-host>:1077` and follow [First run](https://wulfftech.github.io/wondarr/getting-started/first-run/).

| Path | What lives there |
|---|---|
| `/config` | The database, `config.yml`, logs, backups and the bundled slskd's state |
| `/data` | Your music and every download. One mount for both, so torrent imports can hard-link. |

## More

- Docs: https://wulfftech.github.io/wondarr/
- Source, issues and release notes: https://github.com/wulfftech/wondarr
- Licence: GPL-3.0. slskd is redistributed unmodified under its own AGPL-3.0 licence; see [NOTICE.md](https://github.com/wulfftech/wondarr/blob/main/NOTICE.md).
