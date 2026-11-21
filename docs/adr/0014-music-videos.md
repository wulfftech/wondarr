# ADR-0014: Music videos: opt-in, one per song, MKV from YouTube, folder or beside-the-track layout

**Status:** Accepted · **Date:** 2026-11-17 · **Deciders:** owner, orchestrator session

## Context
The owner wants an opt-in feature that finds and downloads a song's music video into a location the user chooses. It is driven from a column in the existing song tables. Until now Wondarr wrote audio only, and yt-dlp's download path is hard-coded to Opus.

Sources for a video, checked 2026-11-17:
- **MusicBrainz:** links an audio recording to its video recording ("music video" relationship), and that recording to YouTube ("free streaming" URL). Teardrop, `f3bba4cd…`, leads to `72f0aa36…` (`video: true`) and then to `youtube.com/watch?v=u7K72X4eo_s`. Coverage is partial.
- **YouTube Music:** its search already labels a result `OfficialVideo` (`MUSIC_VIDEO_TYPE_OMV`) and can filter to videos.
- **Layouts:** Jellyfin and Kodi want a separate music-video library. Plex links a video to a track when the video sits in the track's folder and its name starts with the track's file name. Checked on Plex Media Server 1.43.4 on 2026-11-18 (MKV videos beside MP3 tracks in a temporary music library): `<track>-video.mkv` and `<track>.mkv` become the track's music video extra, `<track>-lyrics.mkv` its lyric music video.
- **Formats:** YouTube serves 1440p and above mostly as VP9 or AV1, which MP4 cannot hold everywhere.

## Decision
- **Off by default.** `music_videos.enabled`, with all settings on one Settings page. The video column, actions and task appear only while the feature is on.
- **One video per song**, stored in a `music_video` row: song, YouTube id, how it was found, path, resolution, codecs, size and status. A rejected video is blocklisted per song.
- **Layout is a setting.**
  - `folder` (the default) writes to a video root that carries the `.wondarr-library` marker under the same rules as a library (DECISIONS build session 14 #40). It uses a naming template, default `{Artist}/{Artist} - {Title}`, with an optional Kodi/Jellyfin `.nfo` and thumbnail.
  - `beside_track` writes next to the song's managed audio file, with that file's name plus a suffix that is a setting. The folder root is still required: a song held only by a reference library falls back to it, because Wondarr never writes into a reference library. Organizer moves (Compact, adoption, an upgrade that renames the file, Remove file) carry the video with the audio.
- **Format.** Best video and best audio up to a height cap (setting; default no cap), merged into **MKV**. `.webm` is never written (ADR-0006, ADR-0008). After download, ffprobe must find one video and one audio stream, and a length within 15 s of the YouTube length.
- **Finding a video.** A match is **confident** if either of these holds:
  1. MusicBrainz links the song's recording to a video recording with a YouTube URL.
  2. A YouTube Music video search returns an `OfficialVideo` from the song's artist's channel, titled as the song (title comparison at or above the same floor the decision engine uses).

  A confident match downloads straight away when the user clicks search. Anything less shows the ranked candidates for the user to pick. A wrong video is worse than none.
- **Scheduled task.** "Find missing music videos" is disabled by default. It acts only on confident matches and is limited per run.
- **Shared limits.** Downloads use the same yt-dlp gate, cookies and PO token as audio, so YouTube sees one client.

## Consequences
- Videos never affect audio searches, imports, quality or upgrades.
- Every organizer move of a managed audio file needs a matching video step under `beside_track`. A missed path leaves an orphan video, which the library scan reports.
- The MusicBrainz finder costs two lookups per song at 1 request/s. The scheduled task stays slow on purpose.
- The `beside_track` default suffix `-video` is the one Plex reads as a music video (checked 2026-11-18, Plex 1.43.4).

## References
`docs/adr/0006-verified-imports.md` · `docs/adr/0008-quality-model.md` · https://musicbrainz.org/relationships/recording-recording · https://jellyfin.org/docs/general/server/media/music-videos/ · https://support.plex.tv/articles/205568377-adding-local-artist-and-music-videos/

## Amendments
- **2026-11-19 (owner): videos already in the folder are recognised.**
  - A scan of the video root (on demand, after the root is set or confirmed, and daily while the feature is on) identifies each video file against the library's songs. It uses the file name (the naming template and the common `Artist - Title` form), the `.nfo` beside it, and the file's own title and artist tags, with length as a tiebreaker only.
  - A confident match to exactly one song is **adopted where it is**: the file is never renamed or moved, it is linked to its song as `FoundBy = Existing`, and its row is `Present`.
  - Anything less goes to a **Music videos** tab on the Match page, where the user picks a song, searches for one, or marks the file as not in the library.
  - Existing files may be any video container Wondarr can probe (`.mkv`, `.mp4`, `.m4v`, `.avi`, `.mov`, `.webm`). The MKV rule and "never write `.webm`" govern what Wondarr downloads and writes, not what it finds.
  - A download never replaces or collides with a file the scan knows. A song whose video is already in the folder shows as having one.
- **2026-11-21 (owner): a confident search result is checked against its real title and channel (M-68).**
  - For an official-video result YouTube Music shows the song's catalogue title and artist, not the upload's own, so an "Official Audio" upload or a fan's lyric video came back confident. A search candidate that would be confident, and the five best-ranked official videos, are looked up through YouTube's keyless oEmbed (at most six lookups a search, two at once, kept in memory for 24 hours).
  - **Confident now means all three:** an official video credited to the song's artist; its real title is the song's (the same matcher and decorations); and its channel is the artist's (normalised name, a trailing "vevo", "official", "music", "tv" or "channel" dropped, containment either way) and it is not audio only.
  - A real title that says audio or lyrics ("(Official Audio)", "// Lyrics", a visualizer, a static video) makes the candidate `AudioOnly`: never confident, ranked like an art track. A failed lookup leaves the candidate listed but not confident (fail closed). The MusicBrainz path is unchanged.
  - Ranking: the full artist weight needs a title at the decision engine's title floor (at most 50 points below it), and a title that is the song's outweighs an official video of another song. The "Best match" badge needs a candidate titled as the song.
  - For music videos only, "edit", "radio edit" and the single, 7" and album version markers are soft: they are dropped from the search text and the match, and a video's own marker is not another version. "Cover" is another version whatever the video's kind.
- **2026-11-21 (owner): a person's pick replaces a video found in the folder (M-68).**
  - Replacing a found video (search, pick or grab from the dialog) moves the found file and its sidecars to the recycle bin, first when the new video takes its name (and puts them back if filing fails), otherwise once the new one is in place. The scheduled task and automatic searches still never replace a found file.
  - Without a usable recycle bin (one inside the video folder) nothing is downloaded: "Set up a recycle bin to replace a video that was in your folder".
  - A failed replacement leaves the filed video as it is and is stored on its row (`replace_failure_reason`, `replace_failed_at`) with a "Music video failed" history event, until the next successful replacement or a dismissal.
  - The same video may be downloaded again when the filed copy is under 720 pixels high, through the same replacement path.
