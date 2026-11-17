using Wondarr.Core.MusicVideos;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The file rules every mover of a managed audio file shares (Compact, the library mover, the refiler, a
/// conversion, an import that replaces the file): which sidecars travel with an audio file — its lyrics
/// and, under <c>beside_track</c>, its music video with the video's own sidecars — and when a folder counts
/// as one a move-out emptied. Written fresh for Wondarr.
/// </summary>
internal static class SongFileRules
{
    /// <summary>The sidecar extensions that travel with their audio file.</summary>
    internal static readonly string[] SidecarExtensions = [".lrc", ".txt"];

    /// <summary>The sidecar suffixes of a music video, named after the video file (ADR-0014).</summary>
    internal static readonly string[] VideoSidecarSuffixes = [".nfo", "-thumb.jpg"];

    /// <summary>What counts as an audio file when deciding whether a folder is one a move emptied.</summary>
    internal static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".alac", ".wma", ".aac"];

    /// <summary>The album folder's art sidecar.</summary>
    internal const string CoverJpgName = "cover.jpg";

    /// <summary>The sidecars that sit beside <paramref name="audioPath"/>, as it is now.</summary>
    /// <param name="disk">The file system.</param>
    /// <param name="audioPath">The audio file whose neighbours are asked for.</param>
    /// <returns>Every existing sidecar path, in extension order.</returns>
    internal static IEnumerable<string> SidecarsOf(IDiskOperations disk, string audioPath)
    {
        var stem = Path.ChangeExtension(audioPath, null);

        if (stem is null)
        {
            yield break;
        }

        foreach (var extension in SidecarExtensions)
        {
            var candidate = string.Concat(stem, extension);

            if (disk.FileExists(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// The sidecar moves that would take <paramref name="audioPath"/>'s neighbours over to
    /// <paramref name="targetPath"/>'s: one per existing sidecar whose own target name is free.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="audioPath">The audio file the sidecars sit beside.</param>
    /// <param name="targetPath">The audio file they would sit beside.</param>
    /// <returns>
    /// (<c>From</c>, <c>To</c>, <c>TargetTaken</c>) triples: a taken target is reported rather than
    /// moved, so the caller can name it in its own log.
    /// </returns>
    internal static IEnumerable<(string From, string To, bool TargetTaken)> SidecarMoves(
        IDiskOperations disk,
        string audioPath,
        string targetPath)
    {
        var fromBase = Path.ChangeExtension(audioPath, null);
        var toBase = Path.ChangeExtension(targetPath, null);

        if (fromBase is null || toBase is null)
        {
            yield break;
        }

        foreach (var extension in SidecarExtensions)
        {
            var from = string.Concat(fromBase, extension);

            if (!disk.FileExists(from))
            {
                continue;
            }

            var to = string.Concat(toBase, extension);

            yield return (from, to, disk.FileExists(to));
        }
    }

    /// <summary>
    /// The part of a video's file name between the audio file's name and <c>.mkv</c>, or
    /// <see langword="null"/> when the video does not sit beside the audio file. A video is beside the track
    /// when it is in the audio file's folder and its name, without <c>.mkv</c>, starts with the audio file's
    /// name without its extension and goes on. The remainder is the video's own suffix, so a later change to
    /// <c>music_videos.beside_track_suffix</c> does not strand a video that already exists.
    /// </summary>
    /// <param name="audioPath">The audio file.</param>
    /// <param name="videoPath">The <c>music_video</c> row's path.</param>
    /// <returns>The remainder (never empty), or <see langword="null"/> when the video is not beside the audio file.</returns>
    internal static string? BesideRemainder(string audioPath, string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || string.IsNullOrWhiteSpace(videoPath))
        {
            return null;
        }

        var videoName = Path.GetFileName(videoPath);
        var audioStem = Path.GetFileNameWithoutExtension(audioPath);

        if (!videoName.EndsWith(MusicVideoPathResolver.VideoExtension, StringComparison.OrdinalIgnoreCase)
            || audioStem.Length == 0)
        {
            return null;
        }

        var videoStem = videoName[..^MusicVideoPathResolver.VideoExtension.Length];

        if (videoStem.Length <= audioStem.Length || !videoStem.StartsWith(audioStem, PathRules.Comparison))
        {
            return null;
        }

        var audioFolder = Path.GetDirectoryName(audioPath) ?? string.Empty;
        var videoFolder = Path.GetDirectoryName(videoPath) ?? string.Empty;

        return PathRules.AreEqual(audioFolder, videoFolder) ? videoStem[audioStem.Length..] : null;
    }

    /// <summary>One file of a video's group that moves with the audio file.</summary>
    /// <param name="From">Where the file is.</param>
    /// <param name="To">Where it goes.</param>
    /// <param name="TargetTaken">Whether <paramref name="To"/> is already a file: reported, never replaced.</param>
    /// <param name="IsVideo">Whether this is the <c>.mkv</c> itself rather than one of its sidecars.</param>
    internal readonly record struct VideoMove(string From, string To, bool TargetTaken, bool IsVideo);

    /// <summary>
    /// The music video of a song, and its <c>.nfo</c> and <c>-thumb.jpg</c> that exist, when it sits beside
    /// <paramref name="audioPath"/>. Empty when the video is not beside the audio file or its file is not there.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="audioPath">The audio file, as the video's row names it.</param>
    /// <param name="videoPath">The <c>music_video</c> row's path.</param>
    /// <returns>The video first, then its sidecars.</returns>
    internal static IReadOnlyList<string> VideoFilesOf(IDiskOperations disk, string audioPath, string? videoPath)
    {
        if (videoPath is null || BesideRemainder(audioPath, videoPath) is null || !disk.FileExists(videoPath))
        {
            return [];
        }

        var files = new List<string> { videoPath };
        var videoBase = VideoBase(videoPath);

        foreach (var suffix in VideoSidecarSuffixes)
        {
            var candidate = string.Concat(videoBase, suffix);

            if (disk.FileExists(candidate))
            {
                files.Add(candidate);
            }
        }

        return files;
    }

    /// <summary>
    /// The moves that would take a song's beside-track video over to <paramref name="targetPath"/>: the video
    /// as <c>&lt;new stem&gt;&lt;remainder&gt;.mkv</c> and its sidecars under the same base. A video whose own
    /// target is taken stays together with its sidecars and is reported; a sidecar whose target is taken is
    /// reported and the rest still go.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="options">The <c>music_videos</c> settings; nothing moves unless the layout is <c>beside_track</c>.</param>
    /// <param name="audioPath">The audio file the video sits beside, as the video's row names it.</param>
    /// <param name="targetPath">The audio file it would sit beside.</param>
    /// <param name="videoPath">The <c>music_video</c> row's path.</param>
    /// <param name="sourceOf">
    /// Where a file is now when it is not where the row implies (the Compact task's staging folder): maps the
    /// path the row implies to the real one, or returns <see langword="null"/> for the path itself.
    /// </param>
    /// <returns>The moves, the video first. Empty when nothing moves.</returns>
    internal static IReadOnlyList<VideoMove> VideoMoves(
        IDiskOperations disk,
        MusicVideoOptions? options,
        string audioPath,
        string targetPath,
        string? videoPath,
        Func<string, string?>? sourceOf = null)
    {
        // The folder layout's videos never move with audio. A file already taken into a staging folder
        // (sourceOf is set) is finished whatever the layout is now: staging is Wondarr's own.
        if ((!IsBesideTrack(options) && sourceOf is null)
            || videoPath is null
            || BesideRemainder(audioPath, videoPath) is not { } remainder)
        {
            return [];
        }

        var targetFolder = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var targetBase = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(targetPath) + remainder);

        return VideoMovesTo(disk, videoPath, targetBase, sourceOf);
    }

    /// <summary>
    /// The moves that take a video to <paramref name="targetBase"/>: the new audio file's folder and name plus
    /// the video's suffix, without <c>.mkv</c>. Used for a video no audio file sits beside any more (Remove file
    /// recycled it) and for putting a staged video back where its row names it.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="videoPath">The <c>music_video</c> row's path.</param>
    /// <param name="targetBase">Where the video goes, without its extension.</param>
    /// <param name="sourceOf">Where a file is now when it is not where the row implies, or <see langword="null"/>.</param>
    /// <returns>The moves, the video first. Empty when nothing moves.</returns>
    internal static IReadOnlyList<VideoMove> VideoMovesTo(
        IDiskOperations disk,
        string videoPath,
        string targetBase,
        Func<string, string?>? sourceOf = null)
    {
        if (!videoPath.EndsWith(MusicVideoPathResolver.VideoExtension, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var rowBase = VideoBase(videoPath);
        var moves = new List<VideoMove>();

        foreach (var (suffix, isVideo) in VideoGroup())
        {
            var named = string.Concat(rowBase, suffix);
            var from = sourceOf?.Invoke(named) ?? named;

            if (!disk.FileExists(from))
            {
                continue;
            }

            var to = string.Concat(targetBase, suffix);

            if (PathRules.AreEqual(from, to))
            {
                continue;
            }

            var taken = disk.FileExists(to);

            // A video that cannot take its new name keeps its sidecars with it: they would only be left
            // pointing at a name that something else holds.
            if (isVideo && taken)
            {
                return [new VideoMove(from, to, true, true)];
            }

            // Sidecars of a video that is not there are not moved: they would be stranded.
            if (isVideo || moves.Count > 0)
            {
                moves.Add(new VideoMove(from, to, taken, isVideo));
            }
        }

        return moves;
    }

    /// <summary>
    /// Where a beside-track video belongs when its audio file moves from <paramref name="audioPath"/> to
    /// <paramref name="targetPath"/>: the target's folder and name plus the video's own suffix.
    /// </summary>
    /// <param name="audioPath">The audio file the video sat beside.</param>
    /// <param name="targetPath">The audio file it sits beside now.</param>
    /// <param name="videoPath">The <c>music_video</c> row's path.</param>
    /// <returns>The video's new path, or <see langword="null"/> when the video was not beside the audio file.</returns>
    internal static string? VideoTargetOf(string audioPath, string targetPath, string? videoPath) =>
        BesideRemainder(audioPath, videoPath) is { } remainder
            ? Path.Combine(
                Path.GetDirectoryName(targetPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(targetPath) + remainder + MusicVideoPathResolver.VideoExtension)
            : null;

    /// <summary>Whether the layout is <c>beside_track</c>: the only layout whose videos move with audio.</summary>
    /// <param name="options">The <c>music_videos</c> settings, or <see langword="null"/> when a host has none.</param>
    internal static bool IsBesideTrack(MusicVideoOptions? options) =>
        options is { Layout: MusicVideoOptions.BesideTrackLayout };

    /// <summary>
    /// <see cref="VideoMoves"/> and <see cref="ApplyVideoMoves"/> in one step that never throws: whatever goes
    /// wrong is reported to <paramref name="failed"/> and the song's audio carries on as though it had no video.
    /// </summary>
    /// <returns>Where the video is now when it moved, or <see langword="null"/>.</returns>
    internal static string? TryMoveVideo(
        IDiskOperations disk,
        MusicVideoOptions? options,
        string audioPath,
        string targetPath,
        string? videoPath,
        Func<string, string?>? sourceOf,
        Action<string, string>? leftBehind,
        Action<string, string>? notMoved,
        Action<Exception>? failed)
    {
        try
        {
            return ApplyVideoMoves(disk, VideoMoves(disk, options, audioPath, targetPath, videoPath, sourceOf), leftBehind, notMoved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed?.Invoke(exception);

            return null;
        }
    }

    /// <summary>
    /// Carries out <paramref name="moves"/>. A move whose target is taken is reported and skipped; a move that
    /// throws is reported and skipped, and a failed <c>.mkv</c> stops the rest (its sidecars stay with it).
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="moves">What <see cref="VideoMoves"/> returned.</param>
    /// <param name="leftBehind">Called with the file and the name it could not take, for the caller's log.</param>
    /// <param name="notMoved">Called with the file and the reason, for the caller's log.</param>
    /// <returns>Where the <c>.mkv</c> is now when it moved, or <see langword="null"/> when it stayed where it was.</returns>
    internal static string? ApplyVideoMoves(
        IDiskOperations disk,
        IReadOnlyList<VideoMove> moves,
        Action<string, string>? leftBehind = null,
        Action<string, string>? notMoved = null)
    {
        string? videoNow = null;

        foreach (var move in moves)
        {
            if (move.TargetTaken)
            {
                leftBehind?.Invoke(move.From, move.To);

                continue;
            }

            try
            {
                var folder = Path.GetDirectoryName(move.To);

                if (!string.IsNullOrEmpty(folder) && !disk.DirectoryExists(folder))
                {
                    disk.CreateDirectory(folder);
                }

                disk.MoveFile(move.From, move.To);

                if (move.IsVideo)
                {
                    videoNow = move.To;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notMoved?.Invoke(move.From, exception.Message);

                if (move.IsVideo)
                {
                    break;
                }
            }
        }

        return videoNow;
    }

    /// <summary>The group's file suffixes in move order: the video itself first.</summary>
    private static IEnumerable<(string Suffix, bool IsVideo)> VideoGroup()
    {
        yield return (MusicVideoPathResolver.VideoExtension, true);

        foreach (var suffix in VideoSidecarSuffixes)
        {
            yield return (suffix, false);
        }
    }

    /// <summary>The video's path without its <c>.mkv</c>.</summary>
    private static string VideoBase(string videoPath) => videoPath[..^MusicVideoPathResolver.VideoExtension.Length];

    /// <summary>
    /// Whether <paramref name="folder"/> holds nothing but <c>cover.jpg</c> files — the shape a
    /// move-out leaves behind when it took every audio file with it. Anything else in the folder
    /// (an audio file, another file, a subfolder) makes it <see langword="false"/>.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="folder">The folder to inspect.</param>
    /// <param name="covers">The <c>cover.jpg</c> files it holds, empty when it is not covers-only.</param>
    /// <returns>Whether the folder is one a move-out emptied except for its art.</returns>
    internal static bool HoldsOnlyCovers(IDiskOperations disk, string folder, out IReadOnlyList<string> covers)
    {
        covers = [];

        var files = disk.EnumerateFiles(folder).ToList();

        // An audio file the move did not take with it means this is not a folder we emptied.
        if (files.Any(file => HasExtension(file, AudioExtensions)))
        {
            return false;
        }

        var found = files
            .Where(file => string.Equals(Path.GetFileName(file), CoverJpgName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count != found.Count)
        {
            return false;
        }

        covers = found;

        return true;
    }

    /// <summary>Whether <paramref name="path"/> ends with one of <paramref name="extensions"/>.</summary>
    private static bool HasExtension(string path, IReadOnlyList<string> extensions)
    {
        var name = Path.GetFileName(path);

        foreach (var extension in extensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
