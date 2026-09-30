using System.Globalization;
using System.Linq.Expressions;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Songs;

/// <summary>The write side of the song lifecycle: adding resolved identities, and the CRUD around them.</summary>
public interface ISongService
{
    /// <summary>Adds a batch of resolved identities as songs, artists, credits and album contexts.</summary>
    /// <param name="identities">The identities to add, in the caller's order.</param>
    /// <param name="options">Where and how the songs are added.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One result per identity, in input order.</returns>
    Task<IReadOnlyList<SongAddResult>> AddIdentitiesAsync(
        IReadOnlyList<SongIdentity> identities,
        SongAddOptions options,
        CancellationToken cancellationToken);

    /// <summary>Resolves one song by MBID or Deezer id and adds it.</summary>
    /// <param name="mbRecordingId">The recording MBID, when known.</param>
    /// <param name="deezerId">The Deezer track id, when known.</param>
    /// <param name="options">Where and how the song is added.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The add result.</returns>
    /// <exception cref="SongNotFoundException">Neither provider knows the id.</exception>
    Task<SongAddResult> AddAsync(
        string? mbRecordingId,
        long? deezerId,
        SongAddOptions options,
        CancellationToken cancellationToken);

    /// <summary>Reads one song with everything a song resource needs.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The song, or <see langword="null"/> when the id is unknown.</returns>
    Task<Song?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists songs, filtered and paged.</summary>
    /// <param name="paging">The page, size, sort key and direction.</param>
    /// <param name="artistId">Only songs an artist is credited on, in any role.</param>
    /// <param name="monitored">Only songs with this monitored flag.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of songs and the total the filter matched.</returns>
    Task<PagedResult<Song>> GetPageAsync(
        PagingSpec paging,
        long? artistId,
        bool? monitored,
        CancellationToken cancellationToken);

    /// <summary>Changes a song's monitored flag and quality profile.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="monitored">The new flag, or <see langword="null"/> to leave it.</param>
    /// <param name="qualityProfileId">The new profile, or <see langword="null"/> to leave it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The updated song, or <see langword="null"/> when the id is unknown.</returns>
    /// <exception cref="ArgumentException">The quality profile does not exist.</exception>
    Task<Song?> UpdateAsync(long id, bool? monitored, long? qualityProfileId, CancellationToken cancellationToken);

    /// <summary>Deletes a song; its credits, album context and file row go with it, the artists stay.</summary>
    /// <param name="id">The song id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a song was deleted.</returns>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Reads the releases a song could be filed under, for the album picker.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The release options, empty when the song is unknown or has none.</returns>
    Task<IReadOnlyList<ReleaseOption>> GetAlbumOptionsAsync(long songId, CancellationToken cancellationToken);

    /// <summary>Moves one song to another album context, explicitly overriding its policy assignment.</summary>
    /// <param name="songId">The song id.</param>
    /// <param name="albumKey">One of the song's release option keys, or the literal <c>singles</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The updated song, or <see langword="null"/> when the id is unknown.</returns>
    /// <exception cref="ArgumentException">The key is not one of the song's release options.</exception>
    Task<Song?> SetAlbumContextAsync(long songId, string albumKey, CancellationToken cancellationToken);

    /// <summary>
    /// Re-plans every song of a library under its album policy as if none were placed yet, and returns
    /// what each one would become. Nothing is written: the plan is a proposal for the Compact task.
    /// </summary>
    /// <param name="libraryId">The library to re-plan.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One proposal per re-planned song, in song id order.</returns>
    /// <exception cref="KeyNotFoundException">The library does not exist.</exception>
    Task<IReadOnlyList<ReplannedSong>> ReplanLibraryAsync(long libraryId, CancellationToken cancellationToken);
}

/// <summary>The album context a re-plan would give one song. The row is new and untracked: nothing is saved.</summary>
/// <param name="SongId">The song the proposal is for.</param>
/// <param name="Proposed">The album context that would replace the song's stored one.</param>
public sealed record ReplannedSong(long SongId, AlbumContext Proposed);

/// <summary>
/// Turns resolved identities into songs (ARCHITECTURE §5.4). One batch is planned as a whole: the album
/// policy sees every new song at once, so a pasted list of one artist's songs can fill a real album
/// instead of scattering into one pseudо-album per song. Everything a batch writes goes in one
/// <c>SaveChangesAsync</c>.
/// </summary>
public sealed partial class SongService : ISongService
{
    /// <summary>The album key that means "the artist's Singles pseudo-album".</summary>
    public const string SinglesAlbumKey = "singles";

    /// <summary>An identity without a credited artist cannot be persisted: the primary artist is a foreign key.</summary>
    private const string NoArtistMessage = "A song identity must credit at least one artist.";

    /// <summary>Told apart from a database id, which is always numeric.</summary>
    private const string NewArtistPrefix = "new-artist-";

    private readonly WondarrDbContext _database;
    private readonly IIdentityResolver _resolver;
    private readonly IAlbumPolicyEngine _albumPolicy;
    private readonly IMusicBrainzClient _musicBrainz;
    private readonly ICoverArtResolver _coverArt;
    private readonly ILogger<SongService> _logger;

    /// <summary>Initialises a new instance of the <see cref="SongService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="resolver">The identity resolver, for the id-based add and the album picker.</param>
    /// <param name="albumPolicy">The album policy engine.</param>
    /// <param name="musicBrainz">The MusicBrainz client, for the chosen releases' tracklists.</param>
    /// <param name="coverArt">The cover-art resolver.</param>
    /// <param name="logger">The logger.</param>
    public SongService(
        WondarrDbContext database,
        IIdentityResolver resolver,
        IAlbumPolicyEngine albumPolicy,
        IMusicBrainzClient musicBrainz,
        ICoverArtResolver coverArt,
        ILogger<SongService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(albumPolicy);
        ArgumentNullException.ThrowIfNull(musicBrainz);
        ArgumentNullException.ThrowIfNull(coverArt);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _resolver = resolver;
        _albumPolicy = albumPolicy;
        _musicBrainz = musicBrainz;
        _coverArt = coverArt;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SongAddResult>> AddIdentitiesAsync(
        IReadOnlyList<SongIdentity> identities,
        SongAddOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(options);

        if (identities.Count == 0)
        {
            return [];
        }

        var results = new SongAddResult?[identities.Count];
        var library = await GetLibraryAsync(options.LibraryId, cancellationToken).ConfigureAwait(false);
        var qualityProfileId = await GetQualityProfileIdAsync(options.QualityProfileId, cancellationToken)
            .ConfigureAwait(false);

        // 1. Duplicates: a song the library already holds, or an identity this batch already named.
        var stored = await LoadStoredSongsAsync(identities, cancellationToken).ConfigureAwait(false);
        var pending = new List<int>();
        var duplicateOf = new Dictionary<int, int>();
        var firstByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < identities.Count; index++)
        {
            var key = DuplicateKey(identities[index]);

            if (key is null)
            {
                pending.Add(index);
                continue;
            }

            if (stored.TryGetValue(key, out var song))
            {
                results[index] = new SongAddResult(identities[index], SongAddOutcome.AlreadyExists, song);
                continue;
            }

            if (firstByKey.TryGetValue(key, out var first))
            {
                duplicateOf[index] = first;
                continue;
            }

            firstByKey.Add(key, index);
            pending.Add(index);
        }

        if (pending.Count > 0)
        {
            await CreateAsync(
                identities,
                pending,
                duplicateOf,
                results,
                library,
                qualityProfileId,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        var added = 0;
        foreach (var result in results)
        {
            added += result is { Outcome: SongAddOutcome.Added } ? 1 : 0;
        }

        LogBatch(_logger, added, identities.Count - added);

        return [.. results.Select(result => result!)];
    }

    /// <inheritdoc />
    public async Task<SongAddResult> AddAsync(
        string? mbRecordingId,
        long? deezerId,
        SongAddOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var identity = await _resolver
            .GetIdentityAsync(mbRecordingId, deezerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SongNotFoundException("Neither MusicBrainz nor Deezer knows the requested identity.");

        var results = await AddIdentitiesAsync([identity], options, cancellationToken).ConfigureAwait(false);

        return results[0];
    }

    /// <inheritdoc />
    public async Task<Song?> GetAsync(long id, CancellationToken cancellationToken) =>
        await Songs()
            .FirstOrDefaultAsync(song => song.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetPageAsync(
        PagingSpec paging,
        long? artistId,
        bool? monitored,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = Songs();

        if (artistId is { } id)
        {
            // Any credited artist, not just the primary one: a featured guest still owns the song.
            query = query.Where(song => song.Artists.Any(credit => credit.ArtistId == id));
        }

        if (monitored is { } wanted)
        {
            query = query.Where(song => song.Monitored == wanted);
        }

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Song>(records, totalRecords);
    }

    /// <inheritdoc />
    public async Task<Song?> UpdateAsync(
        long id,
        bool? monitored,
        long? qualityProfileId,
        CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        if (qualityProfileId is { } profileId)
        {
            await RequireQualityProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            song.QualityProfileId = profileId;
        }

        if (monitored is { } value)
        {
            song.Monitored = value;
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return song;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return false;
        }

        // The credits, the album context and the file row cascade in the database; the artists stay.
        _database.Songs.Remove(song);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReleaseOption>> GetAlbumOptionsAsync(
        long songId,
        CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null || (song.MbRecordingId is null && song.DeezerId is null))
        {
            return [];
        }

        // Cached metadata makes this cheap: the identity was read when the song was added.
        var identity = await _resolver
            .GetIdentityAsync(song.MbRecordingId, song.DeezerId, cancellationToken)
            .ConfigureAwait(false);

        return identity?.ReleaseOptions ?? [];
    }

    /// <inheritdoc />
    public async Task<Song?> SetAlbumContextAsync(long songId, string albumKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumKey);

        var song = await _database.Songs
            .Include(candidate => candidate.PrimaryArtist)
            .Include(candidate => candidate.AlbumContext)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        var identity = await _resolver
            .GetIdentityAsync(song.MbRecordingId, song.DeezerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"Song {songId} has no identity to choose an album from.", nameof(songId));

        var options = OptionsFor(identity, albumKey);
        var libraryAlbums = await LoadLibraryAlbumsAsync(song.LibraryId, cancellationToken).ConfigureAwait(false);
        var place = new SongToPlace
        {
            Ref = songId.ToString(CultureInfo.InvariantCulture),
            ArtistKey = song.PrimaryArtistId.ToString(CultureInfo.InvariantCulture),
            ArtistName = song.PrimaryArtist.Name,
            OriginalDate = identity.OriginalDate,
            Flags = identity.Flags,
            Options = options,
        };

        // An explicit override ignores the policy and its album-size threshold: the caller named a
        // release, so a one-song album is exactly what was asked for. Everything else is the add path.
        var assignment = _albumPolicy.Assign(new AlbumPolicyInput
        {
            Policy = AlbumPolicy.FewestAlbums,
            MinTracksPerRealAlbum = 1,
            LibraryName = string.Empty,
            Songs = [place],
            ExistingAlbums = libraryAlbums.Albums,
        })[0];

        assignment = await WithTrackNumbersAsync(assignment, identity, cancellationToken).ConfigureAwait(false);

        var planned = new PlannedSong(place.Ref, 0, identity, song.PrimaryArtist);
        var cover = libraryAlbums.Covers.TryGetValue(assignment.AlbumKey, out var existing)
            ? existing
            : await ResolveCoverAsync(planned, assignment, cancellationToken).ConfigureAwait(false);

        var context = song.AlbumContext;
        if (context is null)
        {
            context = new AlbumContext { SongId = song.Id };
            _database.AlbumContexts.Add(context);
        }

        ApplyAssignment(context, assignment, cover);

        // The user named this album, so the Compact task leaves it alone — and keeps the album itself,
        // so the other songs of the library can still be planned into it.
        context.Pinned = true;

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return song;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReplannedSong>> ReplanLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken)
    {
        var library = await _database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == libraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Library {libraryId.ToString(CultureInfo.InvariantCulture)} does not exist.");

        // A pinned song keeps the album the user chose, and a song with no album context was never
        // placed at all: neither is re-planned.
        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.PrimaryArtist)
            .Include(song => song.AlbumContext)
            .Where(song => song.LibraryId == libraryId && song.AlbumContext != null && !song.AlbumContext.Pinned)
            .OrderBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var identities = new Dictionary<long, SongIdentity>();
        var places = new List<SongToPlace>(songs.Count);
        var planned = new List<PlannedSong>(songs.Count);

        foreach (var song in songs)
        {
            if (song.MbRecordingId is null && song.DeezerId is null)
            {
                continue;
            }

            var identity = await _resolver
                .GetIdentityAsync(song.MbRecordingId, song.DeezerId, cancellationToken)
                .ConfigureAwait(false);

            if (identity is null)
            {
                continue;
            }

            var reference = song.Id.ToString(CultureInfo.InvariantCulture);

            identities[song.Id] = identity;
            places.Add(new SongToPlace
            {
                Ref = reference,
                ArtistKey = song.PrimaryArtistId.ToString(CultureInfo.InvariantCulture),
                ArtistName = song.PrimaryArtist.Name,
                OriginalDate = identity.OriginalDate,
                Flags = identity.Flags,
                Options = identity.ReleaseOptions,
            });
            planned.Add(new PlannedSong(reference, 0, identity, song.PrimaryArtist));
        }

        if (places.Count == 0)
        {
            return [];
        }

        // Ignoring stickiness means handing the engine only the albums whose key must survive as it is:
        // the ones a pinned song holds, plus the library's pseudo-album and compilation album, so those
        // keep their synthetic keys instead of being given new ones. Every real album of a non-pinned
        // song is deliberately left out — that is what re-planning from scratch looks like.
        var libraryAlbums = await LoadLibraryAlbumsAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var pinnedKeys = await LoadPinnedAlbumKeysAsync(libraryId, cancellationToken).ConfigureAwait(false);

        var kept = libraryAlbums.Albums
            .Where(album => pinnedKeys.Contains(album.AlbumKey)
                || album.Kind == AlbumContextKind.PseudoSingles
                || (album.Kind == AlbumContextKind.Compilation && album.IsVariousArtists))
            .ToList();

        var assignments = _albumPolicy.Assign(new AlbumPolicyInput
        {
            Policy = library.AlbumPolicy,
            MinTracksPerRealAlbum = library.MinTracksPerRealAlbum,
            LibraryName = library.Name,
            Songs = places,
            ExistingAlbums = kept,
        });

        var assignmentByRef = new Dictionary<string, AlbumAssignment>(StringComparer.Ordinal);
        var releases = new Dictionary<string, MbRelease?>(StringComparer.Ordinal);

        foreach (var assignment in assignments)
        {
            var identity = identities[long.Parse(assignment.SongRef, CultureInfo.InvariantCulture)];

            assignmentByRef[assignment.SongRef] = await WithTrackNumbersAsync(
                assignment,
                identity,
                cancellationToken,
                releases).ConfigureAwait(false);
        }

        var covers = await ResolveCoversAsync(planned, assignmentByRef, libraryAlbums, cancellationToken)
            .ConfigureAwait(false);

        var replanned = new List<ReplannedSong>(places.Count);

        foreach (var place in places)
        {
            var assignment = assignmentByRef[place.Ref];

            // A new row that is never attached to the context: the caller reads the proposal, and the
            // change tracker is left exactly as it was.
            var context = new AlbumContext { SongId = long.Parse(place.Ref, CultureInfo.InvariantCulture) };

            ApplyAssignment(context, assignment, covers[assignment.AlbumKey]);
            context.Pinned = false;

            replanned.Add(new ReplannedSong(context.SongId, context));
        }

        LogReplanned(_logger, replanned.Count, libraryId);

        return replanned;
    }

    /// <summary>The album keys a pinned song holds: the albums a re-plan must not plan away.</summary>
    private async Task<HashSet<string>> LoadPinnedAlbumKeysAsync(long libraryId, CancellationToken cancellationToken)
    {
        var keys = await _database.AlbumContexts
            .AsNoTracking()
            .Where(context => context.Song.LibraryId == libraryId && context.Pinned)
            .Select(context => context.AlbumKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(keys, StringComparer.Ordinal);
    }

    /// <summary>The release options an explicit album choice is planned against.</summary>
    private static IReadOnlyList<ReleaseOption> OptionsFor(SongIdentity identity, string albumKey)
    {
        if (string.Equals(albumKey, SinglesAlbumKey, StringComparison.OrdinalIgnoreCase))
        {
            // No option left to pick: the engine's set cover has nothing to work with, so the song
            // lands in its artist's pseudo-album.
            return [];
        }

        foreach (var option in identity.ReleaseOptions)
        {
            if (string.Equals(option.Key, albumKey, StringComparison.Ordinal))
            {
                return [option];
            }
        }

        throw new ArgumentException($"'{albumKey}' is not one of the song's release options.", nameof(albumKey));
    }

    /// <summary>Everything a song resource needs, so no endpoint has to fall back to lazy loading.</summary>
    private IQueryable<Song> Songs() => _database.Songs
        .AsNoTracking()
        .Include(song => song.PrimaryArtist)
        .Include(song => song.Artists).ThenInclude(credit => credit.Artist)
        .Include(song => song.AlbumContext)
        .Include(song => song.File);

    /// <summary>
    /// Sort keys are case-insensitive. Anything unknown — and no key at all — is <c>added</c>, newest
    /// first. The id breaks ties so that paging the same query twice cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<Song> ApplySort(IQueryable<Song> query, PagingSpec paging) =>
        paging.SortKey?.ToLowerInvariant() switch
        {
            "title" => By(query, song => song.Title, paging.Descending),
            "artist" => By(query, song => song.ArtistCredit, paging.Descending),
            "album" => By(query, song => song.AlbumContext!.AlbumTitle, paging.Descending),
            "added" => By(query, song => song.CreatedAt, paging.Descending),
            _ => By(query, song => song.CreatedAt, descending: true),
        };

    private static IQueryable<Song> By<TKey>(IQueryable<Song> query, Expression<Func<Song, TKey>> key, bool descending)
    {
        var ordered = descending ? query.OrderByDescending(key) : query.OrderBy(key);

        return ordered.ThenBy(song => song.Id);
    }

    /// <summary>Creates the songs of one batch: artists, planning, track numbers, covers and the write.</summary>
    private async Task CreateAsync(
        IReadOnlyList<SongIdentity> identities,
        List<int> pending,
        Dictionary<int, int> duplicateOf,
        SongAddResult?[] results,
        Library library,
        long qualityProfileId,
        SongAddOptions options,
        CancellationToken cancellationToken)
    {
        var libraryAlbums = await LoadLibraryAlbumsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        var knownArtists = await LoadArtistsAsync(identities, pending, cancellationToken).ConfigureAwait(false);
        var artistKeys = new Dictionary<Artist, string>();
        var creditsByIndex = new Dictionary<int, List<(Artist Artist, IdentityArtist Credit)>>();
        var plannedByRef = new Dictionary<string, PlannedSong>(StringComparer.Ordinal);
        var places = new List<SongToPlace>(pending.Count);

        // 2. Artists: reused when an id or a name matches, created otherwise; the primary artist is
        // the credit at position 0.
        foreach (var index in pending)
        {
            var identity = identities[index];

            if (identity.Artists.Count == 0)
            {
                throw new ArgumentException(NoArtistMessage, nameof(identities));
            }

            var credited = new List<(Artist Artist, IdentityArtist Credit)>(identity.Artists.Count);
            foreach (var credit in identity.Artists)
            {
                credited.Add((ResolveArtist(credit, knownArtists), credit));
            }

            var primary = credited.MinBy(entry => entry.Credit.Position).Artist;
            var reference = index.ToString(CultureInfo.InvariantCulture);

            creditsByIndex[index] = credited;
            plannedByRef[reference] = new PlannedSong(reference, index, identity, primary);
            places.Add(new SongToPlace
            {
                Ref = reference,
                ArtistKey = ArtistKey(primary, artistKeys),
                ArtistName = primary.Name,
                OriginalDate = identity.OriginalDate,
                Flags = identity.Flags,
                Options = identity.ReleaseOptions,
            });
        }

        // 3./4. One planning run for the whole batch, against the library's existing albums.
        var assignments = _albumPolicy.Assign(new AlbumPolicyInput
        {
            Policy = library.AlbumPolicy,
            MinTracksPerRealAlbum = library.MinTracksPerRealAlbum,
            LibraryName = library.Name,
            Songs = places,
            ExistingAlbums = libraryAlbums.Albums,
        });

        var assignmentByRef = new Dictionary<string, AlbumAssignment>(StringComparer.Ordinal);
        var releases = new Dictionary<string, MbRelease?>(StringComparer.Ordinal);

        foreach (var assignment in assignments)
        {
            // 5. Track numbers come from the chosen release's own tracklist, read once per release.
            assignmentByRef[assignment.SongRef] = await WithTrackNumbersAsync(
                assignment,
                identities[plannedByRef[assignment.SongRef].Index],
                cancellationToken,
                releases).ConfigureAwait(false);
        }

        // 6. One cover per album key.
        var covers = await ResolveCoversAsync(plannedByRef.Values, assignmentByRef, libraryAlbums, cancellationToken)
            .ConfigureAwait(false);

        // 7. Everything in one transaction.
        foreach (var index in pending)
        {
            var identity = identities[index];
            var reference = index.ToString(CultureInfo.InvariantCulture);
            var assignment = assignmentByRef[reference];

            var song = new Song
            {
                Title = identity.Title,
                ArtistCredit = identity.ArtistCredit,
                PrimaryArtist = plannedByRef[reference].PrimaryArtist,
                MbRecordingId = Lower(identity.MbRecordingId),
                DeezerId = identity.DeezerId,
                Isrcs = [.. identity.Isrcs],
                DurationMs = identity.DurationMs,
                VersionFlags = [.. VersionFlagNames.ToWireNames(identity.Flags)],
                Monitored = options.Monitored,
                QualityProfileId = qualityProfileId,
                LibraryId = library.Id,
                AddedBy = options.AddedBy,
            };

            foreach (var (artist, credit) in creditsByIndex[index])
            {
                // The domain's positions start at 1 for the main artist; the identity's start at 0.
                song.Artists.Add(new SongArtist
                {
                    Artist = artist,
                    Role = credit.Role,
                    Position = credit.Position + 1,
                });
            }

            song.AlbumContext = new AlbumContext();
            ApplyAssignment(song.AlbumContext, assignment, covers[assignment.AlbumKey]);

            _database.Songs.Add(song);
            results[index] = new SongAddResult(identity, SongAddOutcome.Added, song);
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A repeated identity inside one batch shares the song the first occurrence created.
        foreach (var (duplicate, first) in duplicateOf)
        {
            results[duplicate] = new SongAddResult(
                identities[duplicate],
                SongAddOutcome.AlreadyExists,
                results[first]!.Song);
        }
    }

    /// <summary>Replaces the engine's track numbers with the chosen release's own, when MusicBrainz has it.</summary>
    private async Task<AlbumAssignment> WithTrackNumbersAsync(
        AlbumAssignment assignment,
        SongIdentity identity,
        CancellationToken cancellationToken,
        Dictionary<string, MbRelease?>? releases = null)
    {
        var recordingId = identity.MbRecordingId;
        var releaseId = assignment.MbReleaseId;

        if (recordingId is null ||
            releaseId is null ||
            assignment.Kind is not (AlbumContextKind.Album or AlbumContextKind.Ep or AlbumContextKind.Single))
        {
            return assignment;
        }

        var cache = releases ?? new Dictionary<string, MbRelease?>(StringComparer.Ordinal);
        if (!cache.TryGetValue(releaseId, out var release))
        {
            release = await _musicBrainz
                .GetReleaseAsync(releaseId, cancellationToken)
                .ConfigureAwait(false);

            cache[releaseId] = release;
        }

        var position = release is null ? null : FindTrack(release, recordingId);
        if (position is null)
        {
            // MusicBrainz does not know the release, or the recording is not on it: the engine's
            // numbers stand.
            return assignment;
        }

        return assignment with
        {
            TrackNo = position.Value.TrackNo,
            DiscNo = position.Value.DiscNo,
            TotalTracks = position.Value.TotalTracks,
        };
    }

    /// <summary>The track, medium and total of the recording on a release, or <see langword="null"/> when it is not there.</summary>
    private static (int TrackNo, int DiscNo, int TotalTracks)? FindTrack(MbRelease release, string recordingId)
    {
        foreach (var medium in release.Media)
        {
            foreach (var track in medium.Tracks)
            {
                if (string.Equals(track.Recording?.Id, recordingId, StringComparison.OrdinalIgnoreCase))
                {
                    return (track.Position, medium.Position, medium.TrackCount);
                }
            }
        }

        return null;
    }

    /// <summary>One cover URL per album key: the stored album's, or the album's own best source.</summary>
    private async Task<Dictionary<string, string?>> ResolveCoversAsync(
        IEnumerable<PlannedSong> planned,
        Dictionary<string, AlbumAssignment> assignments,
        LibraryAlbums libraryAlbums,
        CancellationToken cancellationToken)
    {
        var covers = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var song in planned)
        {
            var assignment = assignments[song.Ref];

            if (covers.ContainsKey(assignment.AlbumKey))
            {
                continue;
            }

            covers[assignment.AlbumKey] = libraryAlbums.Covers.TryGetValue(assignment.AlbumKey, out var existing)
                ? existing
                : await ResolveCoverAsync(song, assignment, cancellationToken).ConfigureAwait(false);
        }

        return covers;
    }

    /// <summary>
    /// The cover of a new album: a real MusicBrainz release is resolved by its ids, a Deezer-only album
    /// uses the identity's own cover, and a pseudo-album or compilation borrows the cover of its first
    /// song's own best release. A miss is a normal outcome, not an error.
    /// </summary>
    private async Task<string?> ResolveCoverAsync(
        PlannedSong song,
        AlbumAssignment assignment,
        CancellationToken cancellationToken)
    {
        if (assignment.Kind is AlbumContextKind.PseudoSingles or AlbumContextKind.Compilation)
        {
            if (!string.IsNullOrWhiteSpace(song.Identity.CoverUrl))
            {
                return song.Identity.CoverUrl;
            }

            var option = FirstEligibleOption(song.Identity);

            var borrowed = await _coverArt.ResolveAsync(
                new CoverArtRequest
                {
                    MbReleaseGroupId = option?.MbReleaseGroupId,
                    MbReleaseId = option?.MbReleaseId,
                    Artist = song.PrimaryArtist.Name,
                    Title = song.Identity.Title,
                },
                cancellationToken).ConfigureAwait(false);

            return borrowed?.Url;
        }

        if (assignment.MbReleaseId is null)
        {
            return song.Identity.CoverUrl;
        }

        var cover = await _coverArt.ResolveAsync(
            new CoverArtRequest
            {
                MbReleaseGroupId = assignment.MbReleaseGroupId,
                MbReleaseId = assignment.MbReleaseId,
                Artist = assignment.AlbumArtist,
                Album = assignment.AlbumTitle,
                Title = song.Identity.Title,
            },
            cancellationToken).ConfigureAwait(false);

        return cover?.Url;
    }

    /// <summary>The first official Album/EP/Single a song appears on.</summary>
    private static ReleaseOption? FirstEligibleOption(SongIdentity identity)
    {
        foreach (var option in identity.ReleaseOptions)
        {
            if (string.Equals(option.Status ?? "Official", "Official", StringComparison.OrdinalIgnoreCase) &&
                option.PrimaryType?.ToLowerInvariant() is "album" or "ep" or "single")
            {
                return option;
            }
        }

        return null;
    }

    /// <summary>Copies an assignment onto the row the organizer reads.</summary>
    private static void ApplyAssignment(AlbumContext context, AlbumAssignment assignment, string? coverUrl)
    {
        context.Kind = assignment.Kind;
        context.AlbumTitle = assignment.AlbumTitle;
        context.AlbumArtist = assignment.AlbumArtist;
        context.AlbumKey = assignment.AlbumKey;
        context.MbReleaseId = assignment.MbReleaseId;
        context.MbReleaseGroupId = assignment.MbReleaseGroupId;
        context.TrackNo = assignment.TrackNo;
        context.DiscNo = assignment.DiscNo;
        context.TotalTracks = assignment.TotalTracks;
        context.Date = assignment.Date;
        context.OriginalDate = assignment.OriginalDate;
        context.CoverUrl = coverUrl;
        context.IsVariousArtists = assignment.IsVariousArtists;

        // Adding a song never moves another: only the Compact task re-plans assignments.
        context.Sticky = true;
    }

    /// <summary>The songs the library already holds for the identities' MBIDs and Deezer ids.</summary>
    private async Task<Dictionary<string, Song>> LoadStoredSongsAsync(
        IReadOnlyList<SongIdentity> identities,
        CancellationToken cancellationToken)
    {
        var mbIds = new List<string>();
        var deezerIds = new List<long?>();

        foreach (var identity in identities)
        {
            if (!string.IsNullOrWhiteSpace(identity.MbRecordingId))
            {
                var id = identity.MbRecordingId!.ToLowerInvariant();
                if (!mbIds.Contains(id, StringComparer.Ordinal))
                {
                    mbIds.Add(id);
                }
            }
            else if (identity.DeezerId is { } deezerId && !deezerIds.Contains(deezerId))
            {
                deezerIds.Add(deezerId);
            }
        }

        if (mbIds.Count == 0 && deezerIds.Count == 0)
        {
            return new Dictionary<string, Song>(StringComparer.Ordinal);
        }

        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.PrimaryArtist)
            .Include(song => song.AlbumContext)
            .Where(song =>
                (song.MbRecordingId != null && mbIds.Contains(song.MbRecordingId))
                || (song.DeezerId != null && deezerIds.Contains(song.DeezerId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byKey = new Dictionary<string, Song>(StringComparer.Ordinal);

        foreach (var song in songs)
        {
            byKey.TryAdd(DuplicateKey(song), song);
        }

        return byKey;
    }

    /// <summary>
    /// The library's album contexts, grouped by album key — the shape the album policy joins against.
    /// A compilation or Various Artists album is keyed with an empty artist, so no artist's songs can
    /// join it by accident.
    /// </summary>
    private async Task<LibraryAlbums> LoadLibraryAlbumsAsync(long libraryId, CancellationToken cancellationToken)
    {
        var rows = await _database.AlbumContexts
            .AsNoTracking()
            .Where(context => context.Song.LibraryId == libraryId)
            .Select(context => new
            {
                context.AlbumKey,
                context.Kind,
                context.AlbumTitle,
                context.AlbumArtist,
                context.Date,
                context.MbReleaseId,
                context.MbReleaseGroupId,
                context.TrackNo,
                context.CoverUrl,
                context.IsVariousArtists,
                context.Song.PrimaryArtistId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var albums = new List<ExistingAlbum>();
        var covers = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var group in rows.GroupBy(row => row.AlbumKey, StringComparer.Ordinal))
        {
            var first = group.First();
            var various = first.IsVariousArtists || first.Kind == AlbumContextKind.Compilation;

            albums.Add(new ExistingAlbum
            {
                AlbumKey = first.AlbumKey,
                Kind = first.Kind,
                ArtistKey = various ? string.Empty : first.PrimaryArtistId.ToString(CultureInfo.InvariantCulture),
                AlbumTitle = first.AlbumTitle,
                AlbumArtist = first.AlbumArtist,
                Date = first.Date,
                MbReleaseId = first.MbReleaseId,
                MbReleaseGroupId = first.MbReleaseGroupId,
                TrackCount = group.Count(),
                MaxTrackNo = group.Max(row => row.TrackNo ?? 0),
                IsVariousArtists = first.IsVariousArtists,
            });

            covers[first.AlbumKey] = group
                .Select(row => row.CoverUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        }

        return new LibraryAlbums(albums, covers);
    }

    /// <summary>The credited artists this batch could reuse: everything an id or a name already matches.</summary>
    private async Task<List<Artist>> LoadArtistsAsync(
        IReadOnlyList<SongIdentity> identities,
        List<int> pending,
        CancellationToken cancellationToken)
    {
        var mbIds = new List<string>();
        var deezerIds = new List<long?>();
        var names = new List<string>();

        foreach (var index in pending)
        {
            foreach (var credit in identities[index].Artists)
            {
                if (!string.IsNullOrWhiteSpace(credit.MbArtistId))
                {
                    var id = credit.MbArtistId!.ToLowerInvariant();
                    if (!mbIds.Contains(id, StringComparer.Ordinal))
                    {
                        mbIds.Add(id);
                    }
                }
                else if (credit.DeezerArtistId is { } deezerId)
                {
                    if (!deezerIds.Contains(deezerId))
                    {
                        deezerIds.Add(deezerId);
                    }
                }
                else
                {
                    // An artist with neither id is only ever matched by its exact name.
                    var name = credit.Name.ToLowerInvariant();
                    if (!names.Contains(name, StringComparer.Ordinal))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        if (mbIds.Count == 0 && deezerIds.Count == 0 && names.Count == 0)
        {
            return [];
        }

        return await _database.Artists
            .Where(artist =>
                (artist.MbArtistId != null && mbIds.Contains(artist.MbArtistId))
                || (artist.DeezerId != null && deezerIds.Contains(artist.DeezerId))
                || names.Contains(artist.Name.ToLower()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Finds the artist behind a credit, or creates it. New artists join <paramref name="known"/> at once.</summary>
    private Artist ResolveArtist(IdentityArtist credit, List<Artist> known)
    {
        Artist? match = null;

        if (!string.IsNullOrWhiteSpace(credit.MbArtistId))
        {
            match = known.FirstOrDefault(
                artist => string.Equals(artist.MbArtistId, credit.MbArtistId, StringComparison.OrdinalIgnoreCase));
        }
        else if (credit.DeezerArtistId is { } deezerId)
        {
            match = known.FirstOrDefault(artist => artist.DeezerId == deezerId);
        }
        else
        {
            match = known.FirstOrDefault(
                artist => string.Equals(artist.Name, credit.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (match is not null)
        {
            return match;
        }

        match = new Artist
        {
            Name = credit.Name,
            SortName = string.IsNullOrWhiteSpace(credit.SortName) ? credit.Name : credit.SortName!,
            MbArtistId = Lower(credit.MbArtistId),
            DeezerId = credit.DeezerArtistId,
        };

        _database.Artists.Add(match);
        known.Add(match);

        return match;
    }

    /// <summary>
    /// The album policy's per-artist key. A stored artist is its id; one created by this batch has no
    /// id yet, so it gets a batch-local key that cannot collide with an id.
    /// </summary>
    private static string ArtistKey(Artist artist, Dictionary<Artist, string> keys)
    {
        if (artist.Id != 0)
        {
            return artist.Id.ToString(CultureInfo.InvariantCulture);
        }

        if (!keys.TryGetValue(artist, out var key))
        {
            key = NewArtistPrefix + (keys.Count + 1).ToString(CultureInfo.InvariantCulture);
            keys.Add(artist, key);
        }

        return key;
    }

    /// <summary>The key a song is looked up by: its recording MBID, or its Deezer id without one.</summary>
    private static string? DuplicateKey(SongIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.MbRecordingId)
            ? "mb:" + identity.MbRecordingId!.ToLowerInvariant()
            : identity.DeezerId is { } deezerId
                ? "dz:" + deezerId.ToString(CultureInfo.InvariantCulture)
                : null;

    /// <summary>The same key, read off a stored song.</summary>
    private static string DuplicateKey(Song song) =>
        song.MbRecordingId is { Length: > 0 } mbRecordingId
            ? "mb:" + mbRecordingId.ToLowerInvariant()
            : "dz:" + song.DeezerId!.Value.ToString(CultureInfo.InvariantCulture);

    private async Task<Library> GetLibraryAsync(long? libraryId, CancellationToken cancellationToken)
    {
        var library = libraryId is { } id
            ? await _database.Libraries
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
                .ConfigureAwait(false)
            : await _database.Libraries
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.IsDefault, cancellationToken)
                .ConfigureAwait(false);

        return library ?? throw new ArgumentException(
            $"Library {libraryId?.ToString(CultureInfo.InvariantCulture) ?? "default"} does not exist.",
            nameof(libraryId));
    }

    private async Task<long> GetQualityProfileIdAsync(long? qualityProfileId, CancellationToken cancellationToken)
    {
        var id = qualityProfileId ?? SeedData.StandardProfileId;

        await RequireQualityProfileAsync(id, cancellationToken).ConfigureAwait(false);

        return id;
    }

    private async Task RequireQualityProfileAsync(long qualityProfileId, CancellationToken cancellationToken)
    {
        if (!await _database.QualityProfiles
            .AnyAsync(profile => profile.Id == qualityProfileId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Quality profile {qualityProfileId.ToString(CultureInfo.InvariantCulture)} does not exist.",
                nameof(qualityProfileId));
        }
    }

    /// <summary>MusicBrainz ids are stored lower-case, whatever case the provider used.</summary>
    private static string? Lower(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Added} songs, {Existing} already present")]
    private static partial void LogBatch(ILogger logger, int added, int existing);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-planned {Count} songs of library {LibraryId}")]
    private static partial void LogReplanned(ILogger logger, int count, long libraryId);

    /// <summary>An album the library holds, as the policy engine reads it, plus the covers to reuse.</summary>
    private sealed record LibraryAlbums(
        IReadOnlyList<ExistingAlbum> Albums,
        IReadOnlyDictionary<string, string?> Covers);

    /// <summary>One song being added, with everything the cover rules need.</summary>
    private sealed record PlannedSong(string Ref, int Index, SongIdentity Identity, Artist PrimaryArtist);
}
