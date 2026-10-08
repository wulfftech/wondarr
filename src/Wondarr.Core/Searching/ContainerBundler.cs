using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Searching;

/// <summary>Another wanted song found in the container being grabbed, with its file.</summary>
/// <param name="Song">The song.</param>
/// <param name="Match">Its file in the container.</param>
public sealed record BundledSong(Song Song, ContainerMatch Match);

/// <summary>
/// Bundling (DECISIONS build session 8 #6): when a container is grabbed for one song, the other
/// monitored songs of the same main artist that are missing — or below their cutoff, when the
/// container's quality is an upgrade — and whose file the matcher finds in the container's known file
/// list ride along on the same grab, each with its own queue item.
/// </summary>
public static class ContainerBundler
{
    /// <summary>The other wanted songs in the candidate's container; empty when its file list is unknown.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="songId">The song the container is grabbed for.</param>
    /// <param name="candidate">The container candidate being grabbed.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    public static async Task<IReadOnlyList<BundledSong>> FindAsync(
        WondarrDbContext database,
        long songId,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Container != CandidateContainer.AlbumContainer
            || candidate.Release is not { Files: { Count: > 1 } files, FileIndex: { } grabbedIndex })
        {
            return [];
        }

        var artistId = await database.Songs
            .AsNoTracking()
            .Where(song => song.Id == songId)
            .Select(song => (long?)song.PrimaryArtistId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (artistId is null)
        {
            return [];
        }

        var songs = await database.Songs
            .AsNoTracking()
            .Include(song => song.File)
            .Where(song => song.PrimaryArtistId == artistId && song.Id != songId && song.Monitored)
            .OrderBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var profiles = await database.QualityProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Id, cancellationToken)
            .ConfigureAwait(false);

        var busy = await database.QueueItems
            .AsNoTracking()
            .Where(item => item.State == QueueItemState.Queued
                || item.State == QueueItemState.RemotelyQueued
                || item.State == QueueItemState.Downloading)
            .Select(item => item.SongId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var taken = new HashSet<int> { grabbedIndex };
        var bundle = new List<BundledSong>();

        foreach (var song in songs)
        {
            if (busy.Contains(song.Id) || !Wants(song, candidate.QualityId, profiles))
            {
                continue;
            }

            var match = ContainerMatcher.Find(
                files,
                new ContainerMatchRequest(song.Title, ParseFlags(song.VersionFlags), null, song.DurationMs, candidate.QualityId));

            // Two songs never share a file, and the grabbed song's file is its own.
            if (match is not null && taken.Add(match.File.Index))
            {
                bundle.Add(new BundledSong(song, match));
            }
        }

        return bundle;
    }

    /// <summary>
    /// The candidate for a bundled song: the same container and release, pointing at that song's
    /// file. Its blocklist key names the file, as the indexer source builds them.
    /// </summary>
    /// <param name="grabbed">The container candidate grabbed for the first song.</param>
    /// <param name="bundled">The other song and its file.</param>
    public static Candidate CandidateFor(Candidate grabbed, BundledSong bundled)
    {
        ArgumentNullException.ThrowIfNull(grabbed);
        ArgumentNullException.ThrowIfNull(bundled);

        var release = grabbed.Release ?? throw new ArgumentException("The candidate has no container release.", nameof(grabbed));
        var file = bundled.Match.File;
        var key = grabbed.SourceType == SourceTypes.Newznab
            ? BlocklistKeys.Usenet(release.ReleaseId, file.Path)
            : BlocklistKeys.Torrent(release.InfoHash ?? release.ReleaseId, file.Path);

        return grabbed with
        {
            BlocklistKey = key,
            DisplayName = Path.GetFileName(file.Path),
            RemotePath = file.Path,
            Parsed = bundled.Match.Parsed with
            {
                Artist = string.IsNullOrWhiteSpace(bundled.Match.Parsed.Artist) ? grabbed.Parsed.Artist : bundled.Match.Parsed.Artist,
                Album = string.IsNullOrWhiteSpace(bundled.Match.Parsed.Album) ? grabbed.Parsed.Album : bundled.Match.Parsed.Album,
            },
            Extension = bundled.Match.Extension,
            SizeBytes = file.Size,
            Release = release with
            {
                FileIndex = file.Index,
                AlsoWanted = null,
                Song = new ContainerMatchRequest(
                    bundled.Song.Title,
                    ParseFlags(bundled.Song.VersionFlags),
                    null,
                    bundled.Song.DurationMs,
                    grabbed.QualityId),
            },
        };
    }

    /// <summary>
    /// Missing, or held below its cutoff at a quality the container improves on. A file from a
    /// reference library is the user's own and is never replaced.
    /// </summary>
    private static bool Wants(Song song, long containerQualityId, Dictionary<long, QualityProfile> profiles)
    {
        if (song.File is not { } held)
        {
            return true;
        }

        if (held.SourceType == SourceTypes.Reference || !profiles.TryGetValue(song.QualityProfileId, out var profile))
        {
            return false;
        }

        return !profile.MeetsCutoff(held.QualityId) && profile.IsUpgrade(held.QualityId, containerQualityId);
    }

    private static VersionFlags ParseFlags(IEnumerable<string> names)
    {
        var flags = VersionFlags.None;

        foreach (var name in names)
        {
            if (VersionFlagNames.TryParse(name, out var single))
            {
                flags |= single;
            }
        }

        return flags;
    }
}
