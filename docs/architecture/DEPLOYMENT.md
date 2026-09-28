# Deployment and slskd management

> Working document derived from `docs/PLAN.md` (revision 3, 2026-09-28) — image conventions, bundled slskd, operational defaults. **This file evolves with the code; when it disagrees with PLAN.md, this file wins.** Section numbers (§) in the text refer to PLAN.md.

### 9.1 Containers

| Container | Role | Required? |
|---|---|---|
| **the app** | API + UI + scheduler + import pipeline (single process) | yes |
| **slskd** (bundled) | Soulseek client: searches, downloads, and shares the library back. **Ships inside the app image and runs as a child process supervised by the app** (§9.5), headless, configured entirely from the app's Soulseek settings page. An *external slskd* mode (URL + API key) is available for users who already run one | yes for the Soulseek source (bundled by default) |
| qBittorrent | torrent client (v1 scope) | optional |
| SABnzbd | usenet client | optional |
| Prowlarr | indexer aggregation; the app consumes its per-indexer Torznab/Newznab endpoints or `/api/v1/search` with an API key | optional |
| bgutil-ytdlp-pot-provider | PO-token provider for YouTube when bot checks bite | optional |
| Plex Media Server | library target; partial scans, empty-trash and playlist push via `X-Plex-Token` | optional |

Bundling is the default from Phase 0 (owner decision): one container, one settings page, no second service to wire up. slskd's pinned release build (linux-x64 / linux-arm64 self-contained archives from its GitHub releases, verified by checksum at image build) lives under `/opt/slskd`; each app release pins and tests one slskd version, and the release notes state it. slskd is redistributed unmodified with its AGPL-3.0 licence, additional terms and source link. Users who prefer the two-container layout run the same image with `APP__SOULSEEK__MODE=external` and their own slskd.

### 9.2 Image conventions (aligned with hotio/LinuxServer *arr images)

- One shared `/data` layout (`/data/downloads/{slskd,torrents,usenet}` and `/data/media/music`) so imports are hardlinks/atomic moves, plus `/config` for the SQLite DB, config, logs and backups ([Servarr docker guide](https://github.com/Servarr/Wiki/blob/master/docker-guide.md)). Legacy `/downloads` + `/music` mappings also work (copy instead of hardlink, with a health warning).
- `PUID`, `PGID`, `UMASK`, `TZ`; non-root by default; s6-overlay init like hotio/LinuxServer images.
- HTTP on **port 1077** (owner decision; no *arr-family app uses it); Soulseek listen port **50300/tcp** exposed for the bundled slskd (forward it on the router for best results); `URL base` setting; `/ping` (unauthenticated readiness) and `/api/v1/health`; container `HEALTHCHECK` on `/ping`.
- Remote path mappings per download client and a Plex path mapping.
- Multi-arch images (amd64, arm64) on GHCR on every tag: `:latest`, `:develop`, semver; Unraid Community Applications XML template in the repo.

#### Image contents (Phase 0)

One image, `linux/amd64` + `linux/arm64`, built framework-dependent on `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (the SDK and Node stages run on `$BUILDPLATFORM`). Everything that comes from outside the repository is pinned by version **and** sha256, and verified at build time:

| Component | Version | Where it lands |
|---|---|---|
| Wondarr (API + React UI in `wwwroot`) | `$VERSION` | `/app` |
| s6-overlay | 3.2.3.2 (arch + noarch) | `/` (`/init`, `/etc/s6-overlay`) |
| slskd (AGPL-3.0, unmodified, licence alongside) | 0.26.0 | `/opt/slskd` |
| ffmpeg / ffprobe (static, `mwader/static-ffmpeg`) | 9.0.2 | `/usr/local/bin` |
| fpcalc (static chromaprint) | 1.6.1 | `/usr/local/bin/fpcalc` |
| Deno | 2.9.7 | `/usr/local/bin/deno` |

s6-rc services: **`init-wondarr-user`** (oneshot) applies `PUID`/`PGID` to the `wondarr` account and creates `/config`, `/config/logs` and `/config/slskd`; **`svc-wondarr`** (longrun, depends on the oneshot) applies `UMASK` and runs `dotnet /app/Wondarr.Api.dll` as `wondarr`. PID 1 is s6-overlay's `/init`, so there is no shell-form `CMD` and signals reach the app; the image's `HEALTHCHECK` polls `/ping` on port 1077.

### 9.3 Compose example (Appendix A)

The reference `docker-compose.yml` is a single service: the app image with slskd bundled, `/config` (including `/config/slskd`) and `/data` volumes, ports 1077 and 50300. An alternative snippet shows external-slskd mode.

### 9.4 Operational rules baked into defaults

- **Soulseek**: global budget 30 searches per 4 minutes, at most 2 outstanding, ≥ 5 s between submissions; 8 s search timeout (from last response) with a 30 s wall-clock cancel; ≤ 3 concurrent downloads; every search deleted after use; sharing on by default; dedicated account; slskd ≥ 0.26.0 for per-batch destination folders.
- **YouTube**: user must enable the source; 1 download at a time; `-t sleep`-equivalent pacing; Deno in the image; optional cookies and bgutil sidecar; daily yt-dlp version check with in-place update by setting; health probe against a known Art Track.
- **Torrents (qBittorrent)**: partial single-file downloads on; never remove before the client's own seed goals are met; magnets resolved to metadata (`stopCondition=MetadataReceived`) before file selection.
- **Usenet (SABnzbd)**: whole post by default; opportunistic per-file trimming only when file subjects are clean.
- **Metadata APIs**: MusicBrainz 1 req/s with a descriptive `User-Agent`, AcoustID 3 req/s, LRCLIB 200–500 ms gaps, iTunes 20/min, honour `Retry-After`; every response cached with TTLs.

### 9.5 Managing the bundled slskd from the app

slskd exposes its configuration for remote management when `SLSKD_REMOTE_CONFIGURATION=true`: `GET/PUT /api/v0/options/yaml` and `POST /options/yaml/validate` (these need a JWT session from `POST /api/v0/session` with slskd's username/password, not an API key), and it **watches its YAML file and reloads changes**; speed limits and the listen port apply live, while shared directories, slot limits, credentials and distributed-network settings need a restart or reconnect ([slskd config docs](https://raw.githubusercontent.com/slskd/slskd/master/docs/config.md)). In bundled mode the app does not need remote configuration or a JWT session at all: a `SlskdHost` hosted service renders `/config/slskd/slskd.yml` from the app's settings, launches the slskd binary with `SLSKD_APP_DIR=/config/slskd`, `SLSKD_HEADLESS=true` and a generated API key, captures its logs into the app's log viewer, watches its health (`/api/v0/application`, login state), and restarts it when a change needs it (shares, slots, credentials); live-reloadable settings (speed limits, listen port) just take effect. The app's **Soulseek settings page** therefore owns everything: Soulseek username/password, listen port, shared folders (default: the managed library root, read-only, plus any reference library the user ticks), a **"Share my library" toggle** (default on; turning it off shows the leech-ban warning and a persistent health notice), upload slots and speed limits, download/incomplete directories, distributed-network participation, and "open slskd's own UI" for the curious (headless off). In external mode the same page writes through slskd's options API (`GET/PUT /api/v0/options/yaml`, which needs `SLSKD_REMOTE_CONFIGURATION=true` and a JWT session from the slskd web login) and shows a "restart slskd" notice where needed.
