using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The one "align the video" rule (ADR-0014), and the guarded read every audio mover makes of a song's video row.
/// A video is never allowed to fail or skew an audio move: a read or a move that goes wrong is reported and the
/// audio carries on as though the song had no video.
/// </summary>
internal static class BesideTrackVideoMover
{
    /// <summary>
    /// Reads the song's video row (tracked), or <see langword="null"/> when it has none or the read failed. Callers
    /// read it <em>before</em> the audio file moves, so a failure here costs nothing.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="songId">The song.</param>
    /// <param name="failed">Called with what went wrong.</param>
    internal static async Task<MusicVideo?> TryLoadAsync(WondarrDbContext database, long songId, Action<Exception>? failed = null)
    {
        try
        {
            return await database.MusicVideos
                .FirstOrDefaultAsync(candidate => candidate.SongId == songId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed?.Invoke(exception);

            return null;
        }
    }

    /// <summary>
    /// Moves the song's video beside <paramref name="newAudioPath"/> when this rule applies: the layout is
    /// <c>beside_track</c>, the song holds that managed library file, the video's path is <em>not</em> beside it, and
    /// the video sits in the video root, in the same folder as the file, or inside a managed library root (the video
    /// root may not overlap a library, so there it can only be a beside-track video the song's own row names). The video goes to the file's folder and
    /// name plus the <em>current</em> suffix, with its <c>.nfo</c> and thumbnail. A video anywhere else is left
    /// alone, a video inside a reference library is never touched, and a taken name is reported, never replaced.
    /// Never throws (except for cancellation).
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="disk">The file system.</param>
    /// <param name="options">The <c>music_videos</c> settings; none means the rule never applies.</param>
    /// <param name="video">The song's video row, read before the audio moved.</param>
    /// <param name="newAudioPath">The managed library file the song now holds.</param>
    /// <param name="leftBehind">Called with the file and the name it could not take.</param>
    /// <param name="notMoved">Called with the file and the reason it could not move.</param>
    /// <param name="failed">Called with what went wrong, when anything did.</param>
    /// <returns>Where the video's file is now when it moved, otherwise <see langword="null"/>.</returns>
    internal static async Task<string?> TryAlignAsync(
        WondarrDbContext database,
        IDiskOperations disk,
        MusicVideoOptions? options,
        MusicVideo? video,
        string newAudioPath,
        Action<string, string>? leftBehind = null,
        Action<string, string>? notMoved = null,
        Action<Exception>? failed = null)
    {
        try
        {
            if (!SongFileRules.IsBesideTrack(options)
                || video?.Path is not { } videoPath
                || !Path.IsPathFullyQualified(videoPath)
                || !videoPath.EndsWith(MusicVideoPathResolver.VideoExtension, StringComparison.OrdinalIgnoreCase)

                // Already beside the file: nothing to do.
                || SongFileRules.BesideRemainder(newAudioPath, videoPath) is not null)
            {
                return null;
            }

            var inVideoRoot = options!.ActiveRoot is { } activeRoot && PathRules.IsStrictlyInside(activeRoot, videoPath);
            var inFileFolder = PathRules.AreEqual(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetDirectoryName(newAudioPath) ?? string.Empty);

            var inManagedLibrary = false;

            if (!inVideoRoot && !inFileFolder)
            {
                var libraryRoots = await database.Libraries
                    .AsNoTracking()
                    .Select(library => library.RootPath)
                    .ToListAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                inManagedLibrary = libraryRoots.Any(root => !string.IsNullOrWhiteSpace(root) && PathRules.IsStrictlyInside(root, videoPath));
            }

            if (!inVideoRoot && !inFileFolder && !inManagedLibrary)
            {
                return null;
            }

            var referenceRoots = await database.ReferenceLibraries
                .AsNoTracking()
                .Select(reference => reference.RootPath)
                .ToListAsync(CancellationToken.None)
                .ConfigureAwait(false);

            // Wondarr never writes into a reference library.
            if (referenceRoots.Any(root => !string.IsNullOrWhiteSpace(root) && PathRules.IsInside(root, videoPath)))
            {
                return null;
            }

            var targetBase = Path.Combine(
                Path.GetDirectoryName(newAudioPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(newAudioPath) + options.BesideTrackSuffix);

            return SongFileRules.ApplyVideoMoves(disk, SongFileRules.VideoMovesTo(disk, videoPath, targetBase), leftBehind, notMoved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed?.Invoke(exception);

            return null;
        }
    }
}
