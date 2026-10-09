---
title: SABnzbd
description: Add SABnzbd as the usenet download client.
---

SABnzbd is Wondarr's usenet client. It works with a [Newznab or Prowlarr indexer](/wondarr/sources/indexers/) set to the Usenet protocol.

## What Wondarr does with a post

1. The job is added to SABnzbd paused. If every file in the post is a plain audio file or a `.par2` file, Wondarr removes the audio files that no wanted song matches, so only the wanted track is downloaded. If anything else is in the post (a RAR set, obfuscated file names), the whole post is downloaded: a trimmed RAR set cannot be repaired.
2. SABnzbd verifies, repairs and unpacks it as it normally does.
3. Wondarr moves the wanted song's file out of SABnzbd's complete folder into its staging folder (`import.container_staging_path`, default `/data/downloads/containers`) and imports it.
4. Once every song taken from the job is imported or failed, Wondarr deletes the job from SABnzbd's history together with its files.

A post larger than `search.max_container_size_mb` (default 1500) is rejected.

## Create the category first

Wondarr files its jobs under a category (default `wondarr`). SABnzbd's API cannot create one, so make it yourself in SABnzbd under **Config → Categories** before you test the client.

## Settings → Download clients

Press **Add download client**, choose **SABnzbd**, then **Continue**.

| Field | Meaning |
|---|---|
| **Name**, **Enabled**, **Priority** | As for [qBittorrent](/wondarr/sources/qbittorrent/). |
| **Host** | Where SABnzbd listens, for example `sabnzbd` or `192.168.1.10`. A name or address only: no scheme, path or port. Required. |
| **Port** | Default `8080`. |
| **Use SSL** | Whether SABnzbd is served over HTTPS. |
| **URL base** | Under **Advanced**: SABnzbd's URL base, for example `/sabnzbd`. |
| **API key** | From SABnzbd's **Config → General**. Required. |
| **Category** | Default `wondarr`. Must exist in SABnzbd. |
| **Remote path mappings** | **Path in the client** and **Path Wondarr sees**, as for qBittorrent. |

### The complete folder

Wondarr reads the finished file from the folder SABnzbd finishes the category in: the category's own folder, or SABnzbd's completed-downloads folder (**Config → Folders**). That folder must be visible to Wondarr. The simplest way is to mount the same `/data` in both containers and keep SABnzbd's completed folder under it. If SABnzbd sees the folder at a different path, add a remote path mapping.

### Test

**Test** checks the connection and API key, that the category exists, and that the category's complete folder exists where Wondarr can see it. A failure says what to fix, for example "Create the category 'wondarr' in SABnzbd's settings (Config → Categories); SABnzbd's API cannot create one."
