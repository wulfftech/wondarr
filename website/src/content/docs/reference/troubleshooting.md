---
title: Troubleshooting
description: What the health messages mean, why a song stays wanted, and the usual permission and path problems.
---

## Health messages

**System** in the navigation lists every health check with its state and message, and the header shows a count of issues. The checks run every 15 minutes (**Tasks → Check Health → Run now** runs them at once). You can also get a [notification](/wondarr/operations/notifications/) when a new issue appears.

### Soulseek

| Message | What to do |
|---|---|
| `Soulseek rejected the login for <name>: invalid username or password` | Correct the account under **Settings → Soulseek**. |
| `Soulseek disconnected: another client logged in as <name>. Use a dedicated Soulseek account for Wondarr.` | Another client (a desktop app, say) signed in with the same account and kicked Wondarr off. slskd does not reconnect by itself. Stop the other client or give Wondarr its own account, then save the Soulseek settings to restart slskd. |
| `slskd is not running: …` / `slskd is running but not reachable` | Read the rest of the message and the Logs page, which include slskd's output. |
| `slskd binary not found at …; the Soulseek source is unavailable` | The image is damaged or the path was changed; pull the image again. |
| `slskd is not logged in to Soulseek` (your own slskd) | Check that slskd's own account is set up and connected. |
| `<folder> (the folder where Wondarr sees slskd's downloads) is not writable: …` or `… does not exist` | Wondarr must be able to read and write slskd's **Downloads directory** (default `/data/downloads/slskd`) to move finished files out. Fix the mount or its ownership; see Permissions below. |
| `Sharing is off: Soulseek users often ban peers who share nothing` | Turn **Share my library** back on. |
| `None of the shared folders exist: …` | Check **Shared folders** under Settings → Soulseek. |

### Other checks

| Message | What to do |
|---|---|
| `Media tools missing: … — downloads cannot be verified` | `ffprobe`, `ffmpeg` or `fpcalc` could not be run. They are bundled in the image; if you changed `media.ffprobe_path`, `media.ffmpeg_path` or `media.fpcalc_path`, put them back. |
| `yt-dlp could not be run: it is not installed or not on PATH …` | Only shown when the YouTube source is on. Pull the image again. A missing Deno runtime is reported as well. |
| `Plex partial scan failed: …` | Wondarr could not ask Plex to scan. Check that Plex is running and reachable, press **Test** under **Settings → Plex**, and check the path mapping. See [Plex](/wondarr/sources/plex/). |
| `<path> is not writable: …` for `/config` or `/config/logs` | See Permissions below. |
| `Database is not available: …` | The database could not be opened. Check `/config/wondarr.db` and the free space; restore a [backup](/wondarr/operations/tasks-backups-logs/) if it is damaged. |

## A song stays wanted

A monitored song with no file is searched for by the **Missing Search** task and, if `search.search_on_add` is on, straight away when it is added. If nothing happens:

- **No acceptable candidate.** Open **Wanted** and press the **Interactive search** button on the song. It lists every candidate with its score and a **Rejections** column; hover a rejection to read why. Typical reasons are a different version (live, remix), a length that is too far out, a quality the profile does not allow, or a candidate that was blocklisted earlier (see **Activity → Blocklist**). Raising the profile's duration tolerance or allowing more qualities, or grabbing a rejected candidate by hand, are the usual fixes.
- **The search budget.** Soulseek allows 30 searches per 4 minutes, 2 at a time, at least 5 seconds apart, and Wondarr stays inside that. A big list of new songs takes a while, and the interactive search says it "can take up to a minute when the search budget is busy".
- **Backoff.** A song that finds nothing is searched again after 1 hour, then 6, 24, 72 hours and then weekly. Press the song's **Search** button to try now.
- **Sources.** Soulseek needs an account (**Settings → Soulseek**). YouTube is off until you turn it on. Indexers need a download client. See [Soulseek](/wondarr/sources/soulseek/), [YouTube Music](/wondarr/sources/youtube/) and [Indexers](/wondarr/sources/indexers/).

## A file was rejected

**Activity → History** shows a **Rejected** event with the reason. The common ones:

- **Wrong recording**: the fingerprint belongs to a different recording, or does not match the wanted one.
- **Length**: "Duration … s is … s off the song's …". The file is a different edit, or a video with an intro.
- **Not decodable**: the file is damaged.
- **Fake lossless**: "Fake lossless: the spectrum stops at … kHz". See [Quality and upgrades](/wondarr/library/quality-and-upgrades/).
- **Not an upgrade**: the song already has a file that is as good or better, or the new file is a clearly worse match, or an upgrade that AcoustID could not confirm.

A rejected file is blocklisted for that song and the next candidate is tried.

## Permissions

Wondarr runs as `PUID`/`PGID` (default 1000/1000; 99/100 on Unraid). `/config` and the folders Wondarr writes in `/data` must be writable by that user:

```bash
chown -R 1000:1000 /srv/wondarr/config
chown -R 1000:1000 /srv/data/music /srv/data/downloads
```

Use the same ids as the other \*arr containers so they can all read each other's files. `UMASK=002` makes new files group-writable. If Plex cannot read the library, check the ownership and mode of the library folder, or turn on `import.set_permissions` (see [Configuration](/wondarr/reference/configuration/)).

## Hard links failing

A torrent's file is hard-linked into `import.container_staging_path` (default `/data/downloads/containers`). A hard link cannot cross filesystems, so the torrent client's download folder and the staging folder must be on the same mount, as in the one-`/data` layout in [Install](/wondarr/getting-started/install/). When a link is not possible Wondarr copies the file instead. That still works, but it uses extra disk space while the torrent seeds.

## Where the logs are

**Logs** in the web UI navigation, and the files in `/config/logs` (`wondarr-<date>.json`). Slskd's output is in the same log. Set `log.level` to `Debug` for more detail. See [Tasks, backups and logs](/wondarr/operations/tasks-backups-logs/).
