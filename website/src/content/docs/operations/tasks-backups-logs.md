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
| Missing Search | 6 h (`search.missing_interval_hours`) | Searches monitored songs that have no file, up to `search.missing_batch_size` (50) at a time. A song that finds nothing waits longer each time: 1 h, 6 h, 24 h, 72 h, then weekly (`search.backoff_hours`). |
| Upgrade Search | 24 h (`search.upgrade_interval_hours`) | Searches for better files for songs below their cutoff. See [Quality and upgrades](/wondarr/library/quality-and-upgrades/). |
| Reference Library Scan | 24 h | Scans [reference libraries](/wondarr/library/reference-libraries/). |
| Import List Sync | 1 h | Syncs the [import lists](/wondarr/library/import-lists/) that are due. |
| Backup | 7 days (`backup.interval_days`) | Makes a scheduled backup. |

The intervals in brackets can be changed in `config.yml`; see [Configuration](/wondarr/reference/configuration/). Longer jobs you start yourself, such as compacting or converting a library, also appear under Recent commands.

## Backups

**Backup** in the navigation lists every backup with its type, size and time.

- A **scheduled** backup is made weekly (`backup.interval_days`: 7) into `/config/backups/scheduled` and deleted after `backup.retention_days` (28).
- **Back up now** makes a **manual** backup in `/config/backups/manual`. Manual backups are never deleted automatically.
- Each backup is a zip named `wondarr_backup_v…_<time>.zip` holding a consistent copy of `wondarr.db` and `config.yml`. The bundled slskd's own state in `/config/slskd` is not included.
- The row's buttons **Download**, **Restore** and **Delete** do what they say.
- **Restore from file…** restores a zip you upload, for example one downloaded earlier or from another install.

A restore replaces the database and `config.yml`, so the API key and every setting become the backup's. Wondarr checks the zip, stages it, and restarts to apply it. The page shows "Restoring — Wondarr is restarting" and reloads when Wondarr is back. The files it replaced are kept next to the new ones as `*.pre-restore`.

Wondarr does not back up your music, only its own database and settings. Make a backup before upgrading to a major version.

## Logs

**Logs** shows Wondarr's own log, newest first, including the bundled slskd's output. Choose a **Level** (Debug, Information, Warning or Error) and type in **Filter** to search messages. Only the newest part of a very large log is searched; the page says so when that happens.

**Log files** at the bottom of the page lets you download the files themselves. They live in `/config/logs` as `wondarr-<date>.json`, one JSON object per line, one file per day, and the newest 7 are kept (`log.retained_files`). Set `log.level` to `Debug` for more detail.

## The recycle bin

When Wondarr replaces a file, whether by an upgrade or by converting it, the old file goes to the recycle bin instead of being deleted. By default that is `/config/recycle` (`import.recycle_bin_path`).

`import.recycle_bin_cleanup_days` (default `7`, from 0 to 365; `0` keeps files forever) sets how long a recycled file is meant to be kept. In this version no scheduled task runs that clean-up yet, so check the folder now and then and empty it yourself if it grows.
