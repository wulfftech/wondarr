using System.Globalization;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Albums;

/// <summary>
/// The album add. MusicBrainz is searched first, because its release groups are the canonical album
/// and its releases carry the tracklist the songs are filed under; Deezer answers when MusicBrainz
/// has nothing. An add is one batch of songs through <see cref="ISongService"/>, pinned to the
/// release the user picked — the unit stays the song (ADR-0001).
/// </summary>
public sealed partial class AlbumService : IAlbumService
{
    /// <summary>The media formats a release the songs are filed under should have.</summary>
    private static readonly string[] PreferredFormats = ["CD", "Digital Media"];

    private readonly WondarrDbContext _database;
    private readonly IMusicBrainzClient _musicBrainz;
    private readonly IDeezerClient _deezer;
    private readonly IIdentityResolver _resolver;
    private readonly ISongService _songs;
    private readonly ILogger<AlbumService> _logger;

    /// <summary>Initialises a new instance of the <see cref="AlbumService"/> class.</summary>
    public AlbumService(
        WondarrDbContext database,
        IMusicBrainzClient musicBrainz,
        IDeezerClient deezer,
        IIdentityResolver resolver,
        ISongService songs,
        ILogger<AlbumService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(deezer);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _musicBrainz = musicBrainz;
        _deezer = deezer;
        _resolver = resolver;
        _songs = songs;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlbumSearchResult>> SearchAsync(
        string term,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        (await SearchPartialAsync(term, limit, cancellationToken).ConfigureAwait(false)).Items;

    /// <inheritdoc />
    public async Task<PartialSearch<AlbumSearchResult>> SearchPartialAsync(
        string term,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var failed = new List<string>();
        Exception? firstFailure = null;
        var groups = new MbReleaseGroupSearchResult();

        try
        {
            groups = await _musicBrainz
                .SearchReleaseGroupsAsync(MusicBrainzQuery.ReleaseGroupByTerm(term), limit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (ProviderKeys.IsProviderFailure(exception, cancellationToken))
        {
            // Deezer can still answer; only when it fails too is the search unavailable.
            LogProviderFailed(_logger, ProviderKeys.MusicBrainz, exception.Message);
            failed.Add(ProviderKeys.MusicBrainz);
            firstFailure = exception;
        }

        var hits = new List<AlbumSearchResult>(groups.ReleaseGroups.Count);

        foreach (var group in groups.ReleaseGroups)
        {
            hits.Add(new AlbumSearchResult
            {
                Source = AlbumRef.MusicBrainzSource,
                ReleaseGroupId = group.Id,
                Title = group.Title,
                Artist = MbArtistCredit.Format(group.ArtistCredit),
                Year = YearOf(group.FirstReleaseDate),
                Type = TypeOf(group.PrimaryType, group.SecondaryTypes),
            });
        }

        if (hits.Count > 0)
        {
            LogSearched(_logger, hits.Count, AlbumRef.MusicBrainzSource);

            return new PartialSearch<AlbumSearchResult>(hits, failed);
        }

        DeezerAlbumSearchResult albums;

        try
        {
            albums = await _deezer.SearchAlbumsAsync(term, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ProviderKeys.IsProviderFailure(exception, cancellationToken))
        {
            LogProviderFailed(_logger, ProviderKeys.Deezer, exception.Message);
            failed.Add(ProviderKeys.Deezer);
            firstFailure ??= exception;

            if (failed.Contains(ProviderKeys.MusicBrainz))
            {
                throw new ProvidersUnavailableException(failed, firstFailure);
            }

            return new PartialSearch<AlbumSearchResult>(hits, failed);
        }

        foreach (var album in albums.Data)
        {
            hits.Add(new AlbumSearchResult
            {
                Source = AlbumRef.DeezerSource,
                DeezerAlbumId = album.Id,
                Title = album.Title,
                Artist = album.Artist?.Name ?? string.Empty,
                Year = YearOf(album.ReleaseDate),
                Type = TypeOf(album.RecordType, []),
                TrackCount = album.NbTracks,
                CoverUrl = album.CoverMedium,
            });
        }

        LogSearched(_logger, hits.Count, AlbumRef.DeezerSource);

        return new PartialSearch<AlbumSearchResult>(hits, failed);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlbumRelease>> GetReleasesAsync(
        string releaseGroupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseGroupId);

        var releases = await _musicBrainz
            .GetReleasesForReleaseGroupAsync(releaseGroupId, cancellationToken)
            .ConfigureAwait(false);

        var defaultRelease = DefaultRelease(releases);
        var listed = new List<AlbumRelease>(releases.Count);

        foreach (var release in releases)
        {
            listed.Add(new AlbumRelease
            {
                Id = release.Id,
                Title = release.Title,
                Date = release.Date,
                Country = release.Country,
                Formats = FormatsOf(release),
                TrackCount = release.Media.Sum(medium => medium.TrackCount),
                Disambiguation = release.Disambiguation,
                IsDefault = release.Id == defaultRelease?.Id,
            });
        }

        return listed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlbumTrack>?> GetTracklistAsync(
        AlbumRef album,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        if (!album.IsKnownSource)
        {
            throw new ArgumentException(
                $"Source '{album.Source}' is not one Wondarr reads; use musicbrainz or deezer.",
                nameof(album));
        }

        var tracks = album.Source == AlbumRef.MusicBrainzSource
            ? await MusicBrainzTracklistAsync(album.Id, cancellationToken).ConfigureAwait(false)
            : await DeezerTracklistAsync(album.Id, cancellationToken).ConfigureAwait(false);

        if (tracks is null)
        {
            return null;
        }

        await MatchOwnedAsync(tracks, cancellationToken).ConfigureAwait(false);

        return tracks;
    }

    /// <inheritdoc />
    public async Task<AlbumAddResult> AddAsync(
        AlbumAddRequest request,
        Func<string, Task>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ValidateAddAsync(request.LibraryId, request.QualityProfileId, cancellationToken).ConfigureAwait(false);

        var tracks = await GetTracklistAsync(request.Album, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"{request.Album.Source} does not know album {request.Album.Id}.");

        var selected = SelectTracks(tracks, request.TrackKeys);
        var pending = selected.Where(track => !track.Owned).ToList();
        var identities = new List<SongIdentity>(pending.Count);
        var failures = new List<AlbumAddFailure>();
        var resolved = 0;

        foreach (var track in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = await _resolver
                .GetIdentityAsync(track.MbRecordingId, track.DeezerTrackId, cancellationToken)
                .ConfigureAwait(false);

            if (identity is null)
            {
                failures.Add(new AlbumAddFailure(TrackKey(track), track.Title, UnresolvedReason));
            }
            else
            {
                identities.Add(identity);
            }

            resolved++;

            if (reportProgress is not null)
            {
                await reportProgress(
                    $"Resolved {resolved.ToString(CultureInfo.InvariantCulture)} of {pending.Count.ToString(CultureInfo.InvariantCulture)} tracks")
                    .ConfigureAwait(false);
            }
        }

        // One batch, so the album policy groups the album's songs instead of scattering singles. A
        // MusicBrainz album pins its songs to the release the tracklist was read from.
        var options = new SongAddOptions
        {
            LibraryId = request.LibraryId,
            QualityProfileId = request.QualityProfileId,
            Monitored = request.Monitored,
            AddedBy = "album",
            AlbumReleaseId = request.Album.Source == AlbumRef.MusicBrainzSource ? request.Album.Id : null,
        };

        var results = identities.Count > 0
            ? await _songs.AddIdentitiesAsync(identities, options, cancellationToken).ConfigureAwait(false)
            : [];

        var added = results.Count(result => result.Outcome == SongAddOutcome.Added);
        var alreadyInLibrary = selected.Count(track => track.Owned)
            + results.Count(result => result.Outcome == SongAddOutcome.AlreadyExists);
        var filedByPolicy = options.AlbumReleaseId is null
            ? 0
            : results.Count(result => result.Outcome == SongAddOutcome.Added
                && !result.Identity.ReleaseOptions.Any(option =>
                    string.Equals(option.Key, options.AlbumReleaseId, StringComparison.Ordinal)));

        var message = Summarize(selected.Count, added, alreadyInLibrary, failures.Count, filedByPolicy);

        LogAdded(_logger, added, alreadyInLibrary, failures.Count, request.Album.Id);

        return new AlbumAddResult
        {
            Album = request.Album,
            Added = added,
            AlreadyInLibrary = alreadyInLibrary,
            Failed = failures,
            FiledByPolicy = filedByPolicy,
            Message = message,
        };
    }

    /// <inheritdoc />
    public async Task ValidateAddAsync(
        long? libraryId,
        long? qualityProfileId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId is { } wantedLibrary
            && !await _database.Libraries
                .AnyAsync(candidate => candidate.Id == wantedLibrary, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Library {wantedLibrary.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(libraryId));
        }

        if (qualityProfileId is { } wantedProfile
            && !await _database.QualityProfiles
                .AnyAsync(candidate => candidate.Id == wantedProfile, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Quality profile {wantedProfile.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(qualityProfileId));
        }
    }

    /// <inheritdoc />
    public async Task<AlbumRef?> GetAlbumForSongAsync(long songId, CancellationToken cancellationToken = default)
    {
        var song = await _database.Songs
            .AsNoTracking()
            .Include(candidate => candidate.AlbumContext)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        var releaseId = song?.AlbumContext?.MbReleaseId;

        return string.IsNullOrWhiteSpace(releaseId)
            ? null
            : new AlbumRef(AlbumRef.MusicBrainzSource, releaseId);
    }

    /// <summary>Reads a MusicBrainz release's tracklist.</summary>
    private async Task<List<AlbumTrack>?> MusicBrainzTracklistAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        var release = await _musicBrainz.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false);

        if (release is null)
        {
            return null;
        }

        var tracks = new List<AlbumTrack>();

        foreach (var medium in release.Media.OrderBy(candidate => candidate.Position))
        {
            foreach (var track in medium.Tracks.OrderBy(candidate => candidate.Position))
            {
                var recording = track.Recording;

                tracks.Add(new AlbumTrack
                {
                    Disc = medium.Position,
                    Position = track.Position,
                    Title = track.Title ?? recording?.Title ?? string.Empty,
                    ArtistCredit = MbArtistCredit.Format(track.ArtistCredit.Count > 0
                        ? track.ArtistCredit
                        : recording?.ArtistCredit ?? []),
                    LengthMs = track.Length ?? recording?.Length,
                    MbRecordingId = recording?.Id,
                    Isrcs = recording?.Isrcs ?? [],
                });
            }
        }

        return tracks;
    }

    /// <summary>Reads a Deezer album's tracklist.</summary>
    private async Task<List<AlbumTrack>?> DeezerTracklistAsync(string albumId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(albumId, CultureInfo.InvariantCulture, out var id))
        {
            throw new ArgumentException(
                $"Album id '{albumId}' is not a Deezer album id.",
                nameof(albumId));
        }

        var album = await _deezer.GetAlbumAsync(id, cancellationToken).ConfigureAwait(false);

        if (album is null)
        {
            return null;
        }

        var tracks = new List<AlbumTrack>(album.Tracks?.Data.Count ?? 0);
        var position = 0;

        foreach (var track in album.Tracks?.Data ?? [])
        {
            position++;

            tracks.Add(new AlbumTrack
            {
                Disc = track.DiskNumber ?? 1,
                Position = track.TrackPosition ?? position,
                Title = track.Title,
                ArtistCredit = track.Artist?.Name ?? string.Empty,
                LengthMs = track.Duration > 0 ? track.Duration * 1000 : null,
                DeezerTrackId = track.Id,
            });
        }

        return tracks;
    }

    /// <summary>Fills in the song the library already holds for every track, when it holds one.</summary>
    private async Task MatchOwnedAsync(List<AlbumTrack> tracks, CancellationToken cancellationToken)
    {
        var recordingIds = tracks
            .Select(track => track.MbRecordingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (recordingIds.Count > 0)
        {
            var owned = await _database.Songs
                .AsNoTracking()
                .Where(song => song.MbRecordingId != null && recordingIds.Contains(song.MbRecordingId))
                .Select(song => new { song.Id, song.LibraryId, song.MbRecordingId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var byRecording = owned.ToDictionary(song => song.MbRecordingId!, StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < tracks.Count; index++)
            {
                if (tracks[index].MbRecordingId is { } id && byRecording.TryGetValue(id, out var song))
                {
                    tracks[index] = tracks[index] with { SongId = song.Id, LibraryId = song.LibraryId };
                }
            }
        }

        var deezerIds = tracks
            .Select(track => track.DeezerTrackId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (deezerIds.Count > 0)
        {
            var owned = await _database.Songs
                .AsNoTracking()
                .Where(song => song.DeezerId != null && deezerIds.Contains(song.DeezerId.Value))
                .Select(song => new { song.Id, song.LibraryId, song.DeezerId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var byDeezerId = owned.ToDictionary(song => song.DeezerId!.Value);

            for (var index = 0; index < tracks.Count; index++)
            {
                if (tracks[index].DeezerTrackId is { } id && byDeezerId.TryGetValue(id, out var song))
                {
                    tracks[index] = tracks[index] with { SongId = song.Id, LibraryId = song.LibraryId };
                }
            }
        }
    }

    /// <summary>The tracks the caller asked for, in album order.</summary>
    private static IReadOnlyList<AlbumTrack> SelectTracks(IReadOnlyList<AlbumTrack> tracks, IReadOnlyList<string>? trackKeys)
    {
        if (trackKeys is null || trackKeys.Count == 0)
        {
            return tracks;
        }

        var wanted = new HashSet<string>(trackKeys, StringComparer.OrdinalIgnoreCase);

        return tracks
            .Where(track => wanted.Contains(TrackKey(track)))
            .ToList();
    }

    /// <summary>The key a track is named by in a request: its recording MBID or its Deezer track id.</summary>
    private static string TrackKey(AlbumTrack track) =>
        track.MbRecordingId ?? track.DeezerTrackId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The release an album add files its tracks under by default.</summary>
    /// <remarks>
    /// The earliest official release whose media are all CD or Digital Media — the formats a download
    /// can actually be verified against — with the most tracks breaking a tie. When no release has
    /// those formats, the earliest-dated release; undated releases sort last.
    /// </remarks>
    internal static MbRelease? DefaultRelease(IReadOnlyList<MbRelease> releases)
    {
        var preferred = releases
            .Where(release => release.Media.Count > 0
                && release.Media.All(medium => PreferredFormats.Contains(medium.Format ?? string.Empty, StringComparer.Ordinal)))
            .ToList();

        var pool = preferred.Count > 0 ? preferred : releases;

        return pool
            .OrderBy(release => release.Date is { Length: > 0 } ? 0 : 1)
            .ThenBy(release => release.Date, StringComparer.Ordinal)
            .ThenByDescending(release => release.Media.Sum(medium => medium.TrackCount))
            .ThenBy(release => release.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The release's media formats, distinct, for example <c>CD + DVD-Video</c>.</summary>
    private static string? FormatsOf(MbRelease release)
    {
        var formats = release.Media
            .Select(medium => medium.Format)
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Distinct()
            .ToList();

        return formats.Count == 0 ? null : string.Join(" + ", formats);
    }

    /// <summary>The year of a date, when one is given.</summary>
    private static string? YearOf(string? date) =>
        string.IsNullOrWhiteSpace(date) ? null : date.Length >= 4 ? date[..4] : date;

    /// <summary>The album type a search hit shows: the primary type, or the secondary one.</summary>
    private static string TypeOf(string? primaryType, IReadOnlyList<string> secondaryTypes)
    {
        var primary = primaryType switch
        {
            "album" => "Album",
            "single" => "Single",
            "ep" => "EP",
            "compile" => "Compilation",
            null => null,
            _ => primaryType,
        };

        if (primary is null)
        {
            return secondaryTypes.Count > 0 ? secondaryTypes[0] : "Album";
        }

        return secondaryTypes.Count > 0 ? $"{primary} ({string.Join(", ", secondaryTypes)})" : primary;
    }

    /// <summary>The one-line summary an album add reports.</summary>
    private static string Summarize(int total, int added, int alreadyInLibrary, int failed, int filedByPolicy)
    {
        var message = $"Added {added.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} tracks"
            + $", {alreadyInLibrary.ToString(CultureInfo.InvariantCulture)} already in the library";

        if (failed > 0)
        {
            message += $", {failed.ToString(CultureInfo.InvariantCulture)} failed to resolve";
        }

        if (filedByPolicy > 0)
        {
            message += $", {filedByPolicy.ToString(CultureInfo.InvariantCulture)} filed by the library's album policy: not on that release";
        }

        return message;
    }

    /// <summary>Why a track could not be resolved.</summary>
    private const string UnresolvedReason = "Neither MusicBrainz nor Deezer knows this track";

    [LoggerMessage(Level = LogLevel.Information, Message = "Album search returned {Count} hits from {Source}")]
    private static partial void LogSearched(ILogger logger, int count, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} did not answer the album search, carrying on without it: {Reason}")]
    private static partial void LogProviderFailed(ILogger logger, string provider, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Album {AlbumId}: added {Added} songs, {Existing} already in the library, {Failed} failed to resolve")]
    private static partial void LogAdded(ILogger logger, int added, int existing, int failed, string albumId);
}
