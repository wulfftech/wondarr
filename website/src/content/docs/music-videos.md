---
title: Music videos
description: Find a song's music video on YouTube, download it as an MKV, and file it where your media server expects it.
---

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

## Layouts

**Layout** decides where a video goes. Either way you set a **Video folder** outside your libraries. Like a library root, it needs the `.wondarr-library` marker file. If the folder is missing or unmarked, Settings asks you to confirm it before anything is written there.

| Layout | Where videos go | Suits |
|---|---|---|
| **Separate folder** (default) | Under the video folder, named by the template. | Jellyfin, Kodi, and Plex's Other Videos. Point a music video library at the folder. |
| **Beside each track** | Next to the song's audio file, named after it plus a suffix (`-video` by default). | Plex, which links a video to a track by its file name. |

With **Beside each track**, a song held only by a [reference library](/wondarr/library/reference-libraries/) has its video in the separate folder instead. Wondarr never writes into a reference library.

A beside-track video moves with its track. When Compact, a conversion, a library move, a refile or an upgrade renames or moves the audio file, the video, its `.nfo` and its thumbnail follow under the new name. When a song held only by a reference library gets a file of its own (an adoption, or a download), its video moves from the separate folder to beside that file. A video whose new name is already taken stays where it is, and the health check lists any video whose file has gone missing.

Which suffix Plex itself picks up beside a track is not confirmed yet. `-video` is the default; check that Plex shows the video before relying on it.

The **Naming template** is the path of a video under the folder, without its extension. The default is `{Artist}/{Artist} - {Title}`. Tokens: `{Artist}`, `{AlbumArtist}`, `{Title}`, `{Album}` and `{Year}`.

## Settings

| Setting | What it does |
|---|---|
| Enable | Turns the feature on. Off by default. |
| Video folder | Where videos are written. Outside your libraries. |
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
