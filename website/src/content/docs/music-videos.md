---
title: Music videos
description: Find a song's music video on YouTube, download it as an MKV, and file it where your media server expects it.
---

:::note[Not released yet]
This is in the `:develop` image now and arrives in `:latest` with the next release.
:::

Wondarr can fetch a song's music video and file it next to your music, or in a library of its own. It is off by default. Nothing about it shows in the app until you turn it on under **Settings → Music videos**.

Videos come from YouTube, so the [YouTube source](/wondarr/sources/youtube/) needs its yt-dlp setup (cookies, PO token, pacing). Music videos share it: YouTube sees one client.

## What it does

- A song has **one** video. A new pick replaces it once the new file has downloaded and been checked.
- The video is the best picture and best audio YouTube offers, up to your height cap, merged into **MKV**. Never `.webm`.
- Before it is filed, ffprobe checks the file: one video stream, one audio stream, and a length within 15 seconds of YouTube's.
- A video that turns out to be the wrong one can be removed and **blocklisted** for that song, so it is not picked again.
- Videos never touch your audio. They do not count towards quality, cutoffs or upgrades, and an audio search ignores them.

Once it is on, you get:

- A **Music video** column in the Library and Wanted tables, after Search.
- A **Music video** tab on each song's page.
- **Find music videos** in the Library's selection bar, for the songs you ticked.
- A **Music videos** tab on the Match page, for videos already in your folder that Wondarr could not place.

## The column

| Icon | Means | Click |
|---|---|---|
| Video with a magnifier | No video yet | Searches. A confident match downloads at once; otherwise you pick from the candidates. |
| Spinner | Queued or downloading | Opens the video. |
| Filled video | Has a video. Hover for its height. | Opens its path, resolution, codecs and size, with **Replace…** and **Remove**. |
| Red crossed-out video | The last download failed | Opens the candidates to pick another. The reason is shown there. |

**Remove** asks "Remove and don't pick this video again?". The box is ticked by default. The file, its `.nfo` and its thumbnail go to the recycle bin. Remove is refused while a download for the song is running.

## How a match is judged

A match is **confident** when either of these holds:

1. MusicBrainz links the song's recording to a video recording that has a YouTube link.
2. A YouTube Music search returns an **official video** from the song's artist's channel, titled as the song.

A confident match downloads as soon as you search. Anything less shows the ranked candidates: thumbnail, title, artists, length, what kind of upload it is, and a **Confident** or **From MusicBrainz** badge where it earned one. **Download** picks one; **Open on YouTube** lets you watch it first, in a new tab. Wondarr embeds no player.

A wrong video is worse than none, so Wondarr asks rather than guesses.

## Videos already in the folder

You probably have a few music videos already. Point the video folder at them and Wondarr finds them.

A scan reads every video file in the folder and works out which song it is. It runs daily while the feature is on, after you set or confirm the folder, and when you press **Scan the folder now** under **Settings → Music videos**. The same button shows how many files were matched, need a pick, found no match, were ignored or have gone missing.

For each file it tries:

1. The file name, in the naming template's shape or plain `Artist - Title`.
2. The `.nfo` beside it.
3. The file's own title and artist tags.

Length only breaks a tie. It never makes a match on its own.

A file that matches exactly one song, clearly, is **adopted where it is**. It shows on the song as **Found in your folder**, and counts as the song's video. Anything less goes to the Match page.

Wondarr never renames, moves or rewrites these files. Linking one to a song is a note in its own database, nothing more. Whatever you named them, however you filed them, they stay put. The one exception is **Remove**, which sends the file to the recycle bin because you asked it to. A removed file is not picked up again.

Any common video file works: `.mkv`, `.mp4`, `.m4v`, `.avi`, `.mov` and `.webm`. The MKV rule is about what Wondarr downloads, not what you already own.

A download never lands on top of a file the scan knows, and a song whose video is already in the folder shows as having one.

### The Match page

With music videos on, the Match page has two tabs: **Music**, the usual queue of reference files, and **Music videos**. The second carries a badge with the number of files that need you.

Its chips filter the list:

| Chip | Shows |
|---|---|
| **Needs a pick** | Files with more than one plausible song, or that look like a lyric video or visualiser. |
| **No match** | Files that look like nothing in your library. |
| **Ignored** | Files you marked as not belonging to the library. |

Each file shows its thumbnail if there is one beside it, its path under the video folder, the names Wondarr read from the file name, the `.nfo` and the tags (only where they differ), and its length and container. Below that are up to five candidate songs. Each has its artist, album, length, a score bar and why it was suggested. The score is how close the titles are, from 0 to 100%.

- **Use this** links the file to that song. A song that already has a video is marked **has a video**. Picking it asks "Replace that song's video with this file?" and, on yes, sends the old video to the recycle bin.
- **Search…** looks through your library's songs, for when the right one is not listed.
- **Ignore** puts the file in the Ignored list, so the scan leaves it alone. **Unignore** brings it back.

Tick several files to **Accept top match** or **Ignore selected** in one go. Accept top match is deliberately fussy: it only takes a file whose best candidate scores 95% or more, leads the next by over 5 points and has no video yet. The rest are skipped, and the notice says how many.

When nothing is left, the tab says "Every video in the folder is matched" and offers **Scan now**.

## Layouts

**Layout** decides where a video goes. Either way you set a **Video folder** outside your libraries. You pick it with **Choose…**, which shows only the folders you have mounted. Like a library root, it needs the `.wondarr-library` marker file. If the folder is missing or unmarked, Settings asks you to confirm it before anything is written there, and music videos stay off until you do.

The folder defaults to `/data/music-videos` (under your media root, `WONDARR_MEDIA_ROOT`), and Settings marks it **(default)**. Music videos can be turned on only once the folder is usable. The default folder is Wondarr's own, so turning the feature on creates it for you.

### In Docker

A folder in the container's own file system is thrown away when the container is recreated, and so would your videos be. Wondarr therefore refuses a video folder that is not on a mounted volume, and offers only mounted folders when you choose one. If nothing is mounted at `/data/music-videos`, Settings says "No default here: choose a mounted folder". Mount a folder or a share there, or pick a folder under one you already mount, such as your music share.

```yaml
services:
  wondarr:
    volumes:
      - /srv/wondarr/config:/config
      - /mnt/nas/music:/data/music
      - /mnt/nas/music-videos:/data/music-videos   # a host folder or a share
```

Keep the video folder outside your music library folder, and apart from it in your media server: a Jellyfin, Kodi or Plex music video library points at this folder.

| Layout | Where videos go | Suits |
|---|---|---|
| **Separate folder** (default) | Under the video folder, named by the template. | Jellyfin, Kodi, and Plex's Other Videos. Point a music video library at the folder. |
| **Beside each track** | Next to the song's audio file, named after it plus a suffix (`-video` by default). | Plex, which links a video to a track by its file name. |

With **Beside each track**, a song held only by a [reference library](/wondarr/library/reference-libraries/) has its video in the separate folder instead. Wondarr never writes into a reference library.

A beside-track video moves with its track. When Compact, a conversion, a library move, a refile or an upgrade renames or moves the audio file, the video, its `.nfo` and its thumbnail follow under the new name. When a song held only by a reference library gets a file of its own (an adoption, or a download), its video moves from the separate folder to beside that file. A video whose new name is already taken stays where it is, and the health check lists any video whose file has gone missing.

Plex shows a video named `<track>-video.mkv` as the track's music video, and `<track>-lyrics.mkv` as its lyric video, so `-lyrics` is a suffix to use for a library of lyric videos. Rescan the album folder in Plex if a new video doesn't show.

The **Naming template** is the path of a video under the folder, without its extension. The default is `{Artist}/{Artist} - {Title}`. Tokens: `{Artist}`, `{AlbumArtist}`, `{Title}`, `{Album}` and `{Year}`.

## Settings

| Setting | What it does |
|---|---|
| Enable | Turns the feature on. Off by default. |
| Video folder | Where videos are written. Outside your libraries, and on a mounted volume in Docker. Pick it with **Choose…**; **Use default** goes back to `/data/music-videos`. |
| Layout | Separate folder, or beside each track. |
| Naming template | The path under the folder. |
| Beside-track suffix | Added to the audio file's name. Shown only for the beside-track layout. |
| Maximum height | Any, 2160, 1440, 1080 or 720. Above 1080, YouTube mostly serves VP9 or AV1. |
| Write `.nfo` | A Kodi and Jellyfin metadata file beside each video. |
| Save thumbnail | YouTube's thumbnail, saved beside each video. |
| Find missing videos on a schedule | Runs the scheduled task below. Off by default. |
| Run every (hours) | How often the task runs. |
| Songs per run | How many songs one run searches. |

A setting the environment sets (`APP__MUSIC_VIDEOS__…`) shows locked.

## The scheduled task

**Find missing music videos** is one of the scheduled tasks under **Tasks**. It is off until you turn on **Find missing videos on a schedule**.

It acts only on confident matches. Anything less is left for you to pick. Each run searches at most **Songs per run** songs, and it is slow on purpose: MusicBrainz answers one request a second, and each song costs two lookups.

## When a download fails

A failed video keeps its row and shows the reason. If the video folder's marker went missing, for example, the download is held as failed with that reason. Fix the cause and pick the video again from the candidates; the new pick replaces the failed row.
