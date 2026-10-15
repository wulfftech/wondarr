---
title: SABnzbd
description: Add SABnzbd as the usenet download client.
---

SABnzbd is Wondarr's usenet client. It works with a [Newznab or Prowlarr indexer](/wondarr/sources/indexers/) set to the Usenet protocol.

## What Wondarr does with a post

1. The job is added to SABnzbd paused. If every file in the post is a plain audio file, a `.par2` file or a small extra such as a `.nfo`, `.sfv` or cover image (in any letter case, so `.PAR2` counts too), Wondarr removes the audio files that no wanted song matches, so only the wanted track is downloaded. It removes the `.par2` files and the `.sfv` too: they describe the whole album, and with tracks missing SABnzbd would try to rebuild them and fail the job with "Repair failed, not enough repair blocks". A trimmed post therefore downloads without repair data, so a damaged article in the wanted file is caught when Wondarr checks the file at import instead of being repaired. If the post loses no track, its `.par2` files stay and repair works as usual. If anything else is in the post (a RAR set, obfuscated file names), the whole post is downloaded: a trimmed RAR set cannot be repaired. Wondarr then looks for the song among the unpacked files, by title, whether they are named `03 - Title.flac` or in the scene style, `03-artist_name-track_title-GRP.flac`.
2. SABnzbd downloads and unpacks it as it normally does. A post downloaded whole is also verified and repaired from its `.par2` files.
3. Wondarr moves the wanted song's file out of SABnzbd's complete folder into its staging folder (`import.container_staging_path`, default `/data/downloads/containers`) and imports it.
4. Once the wanted file is moved into staging and no other song still waiting on the job needs it, Wondarr deletes the job from SABnzbd's history together with its files. That happens before the import, not after it. A song that fails (SABnzbd reports the download as failed, or the file is not in the post) deletes its job the same way, and so does a job Wondarr added but could not resume.
5. SABnzbd deletes the files of a failed job. It never deletes the folder of a job that completed, so the post's `.nfo` and cover image stay behind, and a post downloaded whole leaves every unpacked track. With `import.clean_leftover_folders` on (the default) Wondarr deletes that folder itself, right after it has removed a job it added: when the wanted file has been moved out, when the finished post turns out not to hold the song, or when the song is taken off the queue after the job finished. See [Leftover folders](#leftover-folders).

A job that was trimmed for one song is not reused for another song whose file the trim removed: Wondarr adds a fresh job instead.

A post that is downloaded whole (a RAR set, obfuscated file names) and is larger than `search.max_container_size_mb` (default 1500) is rejected. A clean post is trimmed to the wanted tracks, so its size does not count against the limit. If the trim fails (SABnzbd would not list the job's files, or listed something else) and the post is larger than the limit, Wondarr removes the job before it downloads anything and the grab fails with "The post could not be trimmed and is larger than the 1500 MB limit." Under the limit it downloads whole.

If the unpacked post turns out not to hold the song, Wondarr blocklists the release for that song, deletes the job and its files, and tries the next candidate. It will not grab that release for that song again.

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

### Leftover folders

Wondarr deletes a finished job's folder, and everything in it, only when all of these hold:

- Wondarr added the job.
- No other song still waiting on the job needs it. With several songs from one post, the last one cleans up.
- SABnzbd has removed the job. A job it refuses to remove stays in SABnzbd with its folder: Wondarr logs a Warning and does not retry, so remove both by hand.
- The folder is a direct child of the folder SABnzbd finishes the category in (see above). That folder itself is never deleted, and neither is anything deeper: with a SABnzbd sorter, jobs land in `Artist/Album` folders that other jobs share.
- The folder is not a link, a filesystem root or Wondarr's staging folder.
- When the wanted file was moved out of the job, it is already in staging. A job removed because the post held no such song, or because the song was taken off the queue, has no wanted file to check.

A category whose folder in SABnzbd ends in `*` ("put the files straight into the category folder") has no folder per job, so Wondarr never cleans it up. Neither does it when SABnzbd names no usable completed folder: Wondarr reads the folder SABnzbd really uses from its status, falls back to **Config → Folders**, and gives up on a relative one. It says so once per client in the log (Warning).

Any other folder that fails a check stays, and the log says so (Warning, with the path). So does a folder Wondarr cannot delete, for example because it has no write access to SABnzbd's complete folder: that is a line in the log, not a failed import. Every folder removed is an Information line with its path.

The checks and the delete use the path as Wondarr sees it: what SABnzbd reports, mapped through the client's remote path mappings. Turn the cleanup off with the **Clean up leftover folders** switch on **Settings → Download clients**, or `import.clean_leftover_folders: false`. Then a job's folder stays, as it does with any other SABnzbd client.

### Test

**Test** checks the connection and API key, that the category exists, and that the category's complete folder exists where Wondarr can see it. A failure says what to fix, for example "Create the category 'wondarr' in SABnzbd's settings (Config → Categories); SABnzbd's API cannot create one."

Wondarr asks SABnzbd for the folder it really uses, so a stock install, whose **Config → Folders** shows the relative `Downloads/complete`, passes. Only if SABnzbd gives no full path and the setting is relative does **Test** fail, and it asks for an absolute completed-downloads folder.
