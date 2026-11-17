# ADR-0014: Music videos: opt-in, one per song, MKV from YouTube, folder or beside-the-track layout

**Status:** Accepted · **Date:** 2026-11-17 · **Deciders:** owner, orchestrator session

## Context
The owner wants an opt-in feature that finds and downloads a song's music video into a location the user chooses. It is driven from a column in the existing song tables. Until now Wondarr wrote audio only, and yt-dlp's download path is hard-coded to Opus.

Sources for a video, checked 2026-11-17:
- **MusicBrainz:** links an audio recording to its video recording ("music video" relationship), and that recording to YouTube ("free streaming" URL). Teardrop, `f3bba4cd…`, leads to `72f0aa36…` (`video: true`) and then to `youtube.com/watch?v=u7K72X4eo_s`. Coverage is partial.
- **YouTube Music:** its search already labels a result `OfficialVideo` (`MUSIC_VIDEO_TYPE_OMV`) and can filter to videos.
- **Layouts:** Jellyfin and Kodi want a separate music-video library. Plex links a video to a track when the video sits in the track's folder and its name starts with the track's file name. The exact suffix Plex accepts is not confirmed by Plex's own article.
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
- The Plex suffix is checked against a real Plex server before the `beside_track` default is fixed.

## References
`docs/adr/0006-verified-imports.md` · `docs/adr/0008-quality-model.md` · https://musicbrainz.org/relationships/recording-recording · https://jellyfin.org/docs/general/server/media/music-videos/ · https://support.plex.tv/articles/205568377-adding-local-artist-and-music-videos/
