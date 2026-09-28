# Reference compose and config

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — illustrative docker-compose.yml and config.yml. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

```yaml
services:
  wondarr:
    image: ghcr.io/wulfftech/wondarr:latest   # slskd is bundled inside this image (§9.1)
    container_name: wondarr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=Australia/Brisbane
      # everything else (Soulseek account, shares, sources, Plex, profiles) is set in the UI
    volumes:
      - ./config/wondarr:/config          # app DB/config/logs; bundled slskd state under /config/slskd
      - /data:/data                         # /data/downloads/{slskd,torrents,usenet}, /data/media/music
    ports:
      - "1077:1077"        # web UI / API
      - "50300:50300"      # Soulseek listen port for the bundled slskd (forward it for best results)
    healthcheck:
      test: ["CMD", "wget", "-q", "--spider", "http://localhost:1077/ping"]
      interval: 30s
    restart: unless-stopped

  # Optional companions (the app talks to them over their APIs):
  # qbittorrent: ...   sabnzbd: ...   prowlarr: ...   bgutil-provider: brainicism/bgutil-ytdlp-pot-provider

  # External-slskd mode instead of the bundled one: set APP__SOULSEEK__MODE=external,
  # APP__SLSKD__URL, APP__SLSKD__API_KEY (+ web username/password for the options API) and run
  # slskd/slskd:latest with SLSKD_REMOTE_CONFIGURATION=true.
```

`/config/<app>/config.yml` (subset):

```yaml
server: { port: 1077, url_base: "", auth: forms, api_key: "<generated>" }
metadata:
  musicbrainz: { user_agent: "<app>/0.1 (you@example.com)", rate_limit_rps: 1 }
  acoustid: { client_key: "<register at acoustid.org>", min_score: 0.7, review_score: 0.5 }
  lyrics: { provider: lrclib, write_lrc: true, embed_unsynced: true }
soulseek:                                  # rendered into /config/slskd/slskd.yml (§9.5)
  mode: bundled                            # bundled | external
  username: <dedicated-soulseek-user>
  password: <password>
  listen_port: 50300
  share_library: true                      # "Share my library" toggle
  shared_folders: [/data/media/music]
  upload_slots: 10
  upload_speed_limit_kib: 2000
sources:
  - type: slskd
    name: Soulseek
    # url/api_key only needed in external mode
    max_concurrent_downloads: 3
    search_timeout_ms: 8000
    preferred_formats: [flac, mp3, m4a]
    ignore_users: []
  - type: youtube
    name: YouTube Music
    prefer_topic_channel: true
    allow_videos: false
    # output policy (§7.4): fully configurable; also overridable per library and per grab
    output: { codec: aac, mode: cbr, bitrate_kbps: 256, container: m4a, sample_rate: keep }
    # e.g. { codec: mp3, mode: vbr, quality: 0 } or { codec: opus, keep: true }; ranked as OPUS-160 regardless
    cookies_file: null
    po_token_provider: null
  - type: torznab
    name: Prowlarr
    url: http://prowlarr:9696
    api_key: "<prowlarr key>"
    download_client: qbittorrent
    partial_download: true
libraries:
  - name: Plexamp
    root: /data/media/music
    layout: plexamp
    album_policy: fewest_albums              # fewest_albums | singles_only | original_album | single_release
    min_tracks_per_real_album: 2
    naming: "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}"
    sidecars: { cover_jpg: true, lrc: true, artist_jpg: false }
    plex: { url: http://plex:32400, token: "<token>", section: Music, path_map: { "/data/media/music": "/music" } }
reference_libraries:
  - name: My existing music
    root: /data/media/music-old
    mode: reference                          # reference | adopt
quality_profiles:
  default: Standard 320                     # cutoff MP3-320; AAC-256 and FLAC allowed; upgrades on
```
