using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>One release to ask the indexers for, with the wanted song's place on it.</summary>
/// <param name="Artist">The release's artist credit, as MusicBrainz writes it.</param>
/// <param name="Album">The release's title.</param>
/// <param name="Year">The release group's first year, when known.</param>
/// <param name="TrackNo">The song's track number on the release, when known.</param>
public sealed record ReleaseTarget(string Artist, string Album, int? Year, int? TrackNo);

/// <summary>
/// Lists the releases a recording appears on, in the order the indexers are asked for them
/// (DECISIONS build session 8 #2): the song's album context first, then official albums, singles and
/// EPs, then compilations, then anything else — at most <see cref="MaxTargets"/>, one per distinct
/// artist and title (the same album in ten countries is one search).
/// </summary>
public static class ReleaseTargets
{
    /// <summary>How many releases a song is searched for at most.</summary>
    public const int MaxTargets = 3;

    /// <summary>Finds the releases to search for. MusicBrainz failures leave the list shorter, never throw.</summary>
    /// <param name="musicBrainz">The MusicBrainz client (cached, rate limited).</param>
    /// <param name="request">The wanted song.</param>
    /// <param name="onError">Told about each MusicBrainz request that failed.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    public static async Task<IReadOnlyList<ReleaseTarget>> FindAsync(
        IMusicBrainzClient musicBrainz,
        SongSearchRequest request,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onError);

        var targets = new List<ReleaseTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(request.AlbumMbReleaseId))
        {
            var album = await TryAsync(
                () => musicBrainz.GetReleaseAsync(request.AlbumMbReleaseId, cancellationToken),
                onError,
                cancellationToken).ConfigureAwait(false);

            if (album is not null && seen.Add(Key(album)))
            {
                targets.Add(Target(album, request.AlbumTrackNo ?? TrackNoOf(album, request.MbRecordingId)));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.MbRecordingId))
        {
            var releases = await TryAsync<IReadOnlyList<MbRelease>>(
                async () => await musicBrainz.GetReleasesForRecordingAsync(request.MbRecordingId, 1, cancellationToken).ConfigureAwait(false),
                onError,
                cancellationToken).ConfigureAwait(false) ?? [];

            foreach (var release in releases.OrderBy(Rank).ThenBy(release => release.Date ?? "9999", StringComparer.Ordinal))
            {
                if (targets.Count >= MaxTargets)
                {
                    break;
                }

                if (!seen.Add(Key(release)))
                {
                    continue;
                }

                // A browse carries no tracklist; the lookup is cached, and the track number is what
                // tells two songs of the same title on one release apart.
                var full = await TryAsync(
                    () => musicBrainz.GetReleaseAsync(release.Id, cancellationToken),
                    onError,
                    cancellationToken).ConfigureAwait(false);

                targets.Add(Target(release, TrackNoOf(full, request.MbRecordingId)));
            }
        }

        // No MusicBrainz identity: the album context's title is still a release to ask for.
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(request.AlbumTitle))
        {
            var artist = request.MainArtists.Count > 0 ? request.MainArtists[0] : request.ArtistCredit;
            targets.Add(new ReleaseTarget(artist, request.AlbumTitle, null, request.AlbumTrackNo));
        }

        return targets;
    }

    /// <summary>Albums first, then singles and EPs, then compilations, then the rest (live, soundtracks…).</summary>
    private static int Rank(MbRelease release)
    {
        var group = release.ReleaseGroup;
        var secondary = group?.SecondaryTypes ?? [];

        if (secondary.Contains("Compilation", StringComparer.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (secondary.Count > 0)
        {
            return 3;
        }

        return group?.PrimaryType switch
        {
            "Album" => 0,
            "Single" or "EP" => 1,
            _ => 3,
        };
    }

    private static ReleaseTarget Target(MbRelease release, int? trackNo) =>
        new(MbArtistCredit.Format(release.ArtistCredit), release.Title, Year(release), trackNo);

    private static string Key(MbRelease release) =>
        string.Concat(
            TextMatching.NormalizeArtist(MbArtistCredit.Format(release.ArtistCredit)),
            "\u001f",
            TextMatching.Normalize(release.Title));

    private static int? Year(MbRelease release)
    {
        var date = release.ReleaseGroup?.FirstReleaseDate ?? release.Date;

        return date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) ? year : null;
    }

    /// <summary>The recording's track position on a release, when the release's tracklist is known.</summary>
    private static int? TrackNoOf(MbRelease? release, string? recordingId)
    {
        if (release is null || string.IsNullOrWhiteSpace(recordingId))
        {
            return null;
        }

        foreach (var medium in release.Media)
        {
            foreach (var track in medium.Tracks)
            {
                if (string.Equals(track.Recording?.Id, recordingId, StringComparison.OrdinalIgnoreCase))
                {
                    return track.Position;
                }
            }
        }

        return null;
    }

    private static async Task<T?> TryAsync<T>(
        Func<Task<T?>> lookup,
        Action<Exception> onError,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await lookup().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MetadataProviderException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            onError(exception);
            return null;
        }
    }
}
