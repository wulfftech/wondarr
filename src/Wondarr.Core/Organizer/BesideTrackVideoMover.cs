using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The one rule for a song that gains a managed library file while its video is not beside any audio file
/// (ADR-0014): under <c>beside_track</c>, a video left behind by Remove file (in a library) or filed in the video
/// root because the song was held only by a reference library goes to the new file's folder and name plus the
/// current <c>beside_track_suffix</c>, with its <c>.nfo</c> and thumbnail. A taken name is reported, never replaced.
/// </summary>
internal static class BesideTrackVideoMover
{
    /// <summary>Moves the song's video beside <paramref name="newAudioPath"/> when the rule applies.</summary>
    /// <param name="database">The database the song's video row is read from (tracked).</param>
    /// <param name="disk">The file system.</param>
    /// <param name="options">The <c>music_videos</c> settings; none means the rule never applies.</param>
    /// <param name="songId">The song that gained the file.</param>
    /// <param name="newAudioPath">The managed library file now held.</param>
    /// <param name="leftBehind">Called with the file and the name it could not take.</param>
    /// <param name="notMoved">Called with the file and the reason it could not move.</param>
    /// <returns>The tracked video row (or <see langword="null"/>) and where its file is now when it moved.</returns>
    internal static async Task<(MusicVideo? Video, string? NewPath)> MoveAsync(
        WondarrDbContext database,
        IDiskOperations disk,
        MusicVideoOptions? options,
        long songId,
        string newAudioPath,
        Action<string, string>? leftBehind = null,
        Action<string, string>? notMoved = null)
    {
        var video = await database.MusicVideos
            .FirstOrDefaultAsync(candidate => candidate.SongId == songId, CancellationToken.None)
            .ConfigureAwait(false);

        if (options is not { Layout: MusicVideoOptions.BesideTrackLayout }
            || video?.Path is not { } videoPath
            || !Path.IsPathFullyQualified(videoPath)
            || !videoPath.EndsWith(MusicVideoPathResolver.VideoExtension, StringComparison.OrdinalIgnoreCase)

            // Already beside the new file: nothing to do.
            || SongFileRules.BesideRemainder(newAudioPath, videoPath) is not null)
        {
            return (video, null);
        }

        var libraryRoots = await database.Libraries
            .AsNoTracking()
            .Select(library => library.RootPath)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var referenceRoots = await database.ReferenceLibraries
            .AsNoTracking()
            .Select(reference => reference.RootPath)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

        // Wondarr never writes into a reference library, and only a video in a library (left by Remove file) or in
        // the video root (the reference-only fallback) was ever the song's own.
        var inLibrary = libraryRoots.Any(root => !string.IsNullOrWhiteSpace(root) && PathRules.IsStrictlyInside(root, videoPath));
        var inVideoRoot = !string.IsNullOrWhiteSpace(options.RootPath) && PathRules.IsStrictlyInside(options.RootPath, videoPath);

        if ((!inLibrary && !inVideoRoot) || referenceRoots.Any(root => !string.IsNullOrWhiteSpace(root) && PathRules.IsInside(root, videoPath)))
        {
            return (video, null);
        }

        var targetBase = Path.Combine(
            Path.GetDirectoryName(newAudioPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(newAudioPath) + options.BesideTrackSuffix);

        var moved = SongFileRules.ApplyVideoMoves(disk, SongFileRules.VideoMovesTo(disk, videoPath, targetBase), leftBehind, notMoved);

        return (video, moved);
    }
}
