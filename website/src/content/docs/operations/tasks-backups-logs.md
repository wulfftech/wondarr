---
title: Tasks, backups and logs
description: The scheduled tasks, making and restoring backups, reading the logs, and the recycle bin.
---

## Tasks

**Tasks** in the navigation lists the scheduled tasks with their interval, last run, last duration, next run and last result. **Run now** starts one at once. Below it, **Recent commands** shows the latest runs, including the ones you started.

| Task | Interval | What it does |
|---|---|---|
| Heartbeat | 1 min | Proves the job queue is working. It does nothing else. |
| Check Health | 15 min | Runs the health checks shown under **System**. |
| Missing Search | 6 h (`search.missing_interval_hours`) | Searches monitored songs that have no file, up to `search.missing_batch_size` (50) at a time. A song that finds nothing waits longer each time: 1 h, 6 h, 24 h, 72 h, then weekly (`search.backoff_hours`); the count runs back to the song's last grab, so a song that is never found settles at weekly. Releases pushed from autobrr do not count. |
| Upgrade Search | 24 h (`search.upgrade_interval_hours`) | Searches for better files for songs below their cutoff. See [Quality and upgrades](/wondarr/library/quality-and-upgrades/). |
| Reference Library Scan | 24 h | Scans [reference libraries](/wondarr/library/reference-libraries/). |
| Import List Sync | 1 h | Syncs the [import lists](/wondarr/library/import-lists/) that are due. |
| Housekeeping | 24 h | Keeps the database small: deletes finished commands older than 7 days (the newest run of each task is kept, for the Last run column), search results older than 30 days that no download or file still refers to, expired blocklist entries and expired cached answers from the metadata providers; empties the recycle bin of files older than `import.recycle_bin_cleanup_days`; then compacts the database when a quarter of it is free space. The history is never pruned. |
| Backup | 7 days (`backup.interval_days`) | Makes a scheduled backup. |
| Check For Updates | 12 h | Asks GitHub whether a newer Wondarr release exists (the first run is about two minutes after start-up). Does nothing when `update.check_enabled` is off. See [Upgrading](/wondarr/getting-started/install/#upgrading). |

**Next run** is the time the scheduler will start the task. **Run now** does not move it. A search task that waits for a download slot, and a reference scan that waits for another scan of the same library, do not keep other commands from starting meanwhile.

The intervals in brackets can be changed in `config.yml`; see [Configuration](/wondarr/reference/configuration/). Longer jobs you start yourself, such as compacting or converting a library, also appear under Recent commands.

## Backups

**Backup** in the navigation lists every backup with its type, size and time.

- A **scheduled** backup is made weekly (`backup.interval_days`: 7) into `/config/backups/scheduled` and deleted after `backup.retention_days` (28).
- **Back up now** makes a **manual** backup in `/config/backups/manual`. Manual backups are never deleted automatically.
- Each backup is a zip named `wondarr_backup_v…_<time>.zip` holding a consistent copy of `wondarr.db` and `config.yml`. The bundled slskd's own state in `/config/slskd` is not included.
- The row's buttons **Download**, **Restore** and **Delete** do what they say.
- **Restore from file…** restores a zip you upload, for example one downloaded earlier or from another install.

A restore replaces the database and `config.yml`, so the API key and every setting become the backup's. Wondarr checks the zip, stages it, and restarts to apply it. The page shows "Restoring — Wondarr is restarting" and reloads when Wondarr is back. The files it replaced are kept next to the new ones as `*.pre-restore`, and the last three restores are kept: `*.pre-restore` is the newest, then `*.pre-restore.1` and `*.pre-restore.2`. A restore that is interrupted part-way is finished at the next start. A backup made by a newer Wondarr than the one you run is refused.

### A backup before every upgrade

When a new version has database changes to apply, Wondarr first makes a backup named `wondarr_pre-<previous version>_<time>.zip` in `/config/backups/scheduled` (`unknown` as the version the first time). It is kept under the scheduled retention (`backup.retention_days`) like any scheduled backup. **If that backup fails, the upgrade stops**: nothing is changed and Wondarr does not start until the cause is fixed (the log says why and how much space the backups volume has). Keep about twice your database's size free in `/config`.

### Undoing a restore

Stop the container, then in `/config` put the files Wondarr set aside back:

```
mv wondarr.db wondarr.db.restored && mv config.yml config.yml.restored
mv wondarr.db.pre-restore wondarr.db && mv config.yml.pre-restore config.yml
```

Use `*.pre-restore.1` or `.2` to go back two or three restores, delete a stray `wondarr.db-wal` or `wondarr.db-shm` left from the restored database, then start the container. Do this before restoring again: a fourth restore deletes the oldest set.

Wondarr does not back up your music, only its own database and settings. Make a backup before upgrading to a major version.

## Logs

**Logs** shows Wondarr's own log, newest first, including the bundled slskd's output. Choose a **Level** (Debug, Information, Warning or Error) and type in **Filter** to search messages. Only the newest part of a very large log is searched; the page says so when that happens.

**Log files** at the bottom of the page lets you download the files themselves. They live in `/config/logs` as `wondarr-<date>.json`, one JSON object per line, one file per day, and the newest 7 are kept (`log.retained_files`). Set `log.level` to `Debug` for more detail.

## The recycle bin

When Wondarr replaces a file, whether by an upgrade or by converting it, the old file goes to the recycle bin instead of being deleted. By default that is `/config/recycle` (`import.recycle_bin_path`).

`import.recycle_bin_cleanup_days` (default `7`, from 0 to 365; `0` keeps files forever) sets how long a recycled file is kept; the daily **Housekeeping** task removes older ones.
