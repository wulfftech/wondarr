---
title: API and integrations
description: The Wondarr API, its Lidarr-compatible surface, and the tools that use it.
---

## The API

The API lives under `/api/v1` on the same port as the web UI. It follows the \*arr conventions: JSON, paging with `page`, `pageSize`, `sortKey` and `sortDirection`.

Send your API key with every call. Any of these works:

```bash
curl -H "X-Api-Key: your-api-key" http://192.168.1.10:1077/api/v1/system/status
curl "http://192.168.1.10:1077/api/v1/system/status?apikey=your-api-key"
curl -H "Authorization: Bearer your-api-key" http://192.168.1.10:1077/api/v1/system/status
```

The key is `server.api_key` in `/config/config.yml`; see [Install](/wondarr/getting-started/install/#the-api-key). The full reference, generated from the running app, is at `http://192.168.1.10:1077/docs`. The page and its OpenAPI document (`/docs/v1/openapi.json`) need no key, because a browser cannot send one; every endpoint they describe still does.

If you serve Wondarr under a URL base, put it in front of every path.

## The Lidarr-compatible surface

Wondarr mirrors the parts of Lidarr's API that dashboards and automation tools read, using the same field names. A tool written for Lidarr can usually be pointed at Wondarr unchanged. Songs take the place of Lidarr's tracks.

| Tool | What it reads from Wondarr |
|---|---|
| Homepage | The Lidarr widget calls `GET /api/v1/artist`, `GET /api/v1/wanted/missing` (`totalRecords`) and `GET /api/v1/queue/status` (`totalCount`), with the key as `?apikey=`. |
| Unpackerr | Pages `GET /api/v1/queue` and reads each record's `title`, `status`, `trackedDownloadStatus`, `protocol`, `size`, `sizeleft`, `downloadId`, `statusMessages` and `outputPath`. |
| autobrr | Tests the connection with `GET /api/v1/system/status` (a wrong key is `401`) and pushes releases to `POST /api/v1/release/push`. |

### Homepage

Use Homepage's Lidarr widget with Wondarr's address and API key.

### Unpackerr

A queue record's `protocol` is `torrent` or `usenet` for downloads from indexers, and the source type (for example `soulseek`) otherwise. Unpackerr only extracts torrent and usenet downloads, so it ignores the rest. A torrent's file is hard-linked out of the client's folder, so Wondarr does not need Unpackerr for it; see [qBittorrent](/wondarr/sources/qbittorrent/).

### autobrr

Use autobrr's Lidarr action, pointed at Wondarr's address and key. The details of the push, with an example request, are on the [Indexers](/wondarr/sources/indexers/#pushing-releases-from-autobrr) page. The answer is a single decision object with `approved`, `rejected`, `temporarilyRejected`, `songId` and `rejections`, so autobrr shows why a release was turned down. A push for a title that matches no wanted song is rejected with "No wanted song matches '…'", and a push without a `title` is a `400`.

### Prowlarr

Wondarr is not a Prowlarr "app", so Prowlarr does not push indexers to it. Instead, Wondarr uses Prowlarr as a source of indexers. Add a Prowlarr row under **Settings → Indexers**, or add Prowlarr's per-indexer Torznab and Newznab URLs. See [Indexers](/wondarr/sources/indexers/).
