using Wondarr.Core.Domain;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Wanted;

/// <summary>The read side of the wanted list: what is still missing and what is below its cutoff.</summary>
public interface IWantedService
{
    /// <summary>Gets the monitored songs that have no file yet, paged and sorted.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<Song>> GetMissingAsync(PagingSpec paging, CancellationToken cancellationToken);

    /// <summary>Gets the monitored songs whose file is below the song's profile cutoff, paged and sorted.</summary>
    /// <param name="paging">The page, size and sort the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<Song>> GetCutoffUnmetAsync(PagingSpec paging, CancellationToken cancellationToken);
}

/// <summary>
/// Backs <c>/api/v1/wanted/missing</c> and <c>/api/v1/wanted/cutoff</c>. "Missing" is a SQL question
/// — a monitored song with no <c>song_file</c> row. "Cutoff unmet" is not, because it needs the
/// profile's group indexes, so the monitored songs that do have a file are loaded and filtered in
/// memory against the profile table, which is read once (ARCHITECTURE §5.6 keeps the Lidarr shapes).
/// </summary>
public sealed class WantedService : IWantedService
{
    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="WantedService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    public WantedService(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetMissingAsync(PagingSpec paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = Songs().Where(song => song.Monitored && song.File == null);

        var totalRecords = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await ApplySort(query, paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Song>(records, totalRecords);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetCutoffUnmetAsync(PagingSpec paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        // A song owned through a reference file is never "below its cutoff" in a way Wondarr may act
        // on: the file is the user's own, so it is never replaced. It is not a wanted song.
        var withFile = await Songs()
            .Where(song => song.Monitored
                && song.File != null
                && song.File.SourceType != SourceTypes.Reference)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (withFile.Count == 0)
        {
            return new PagedResult<Song>([], 0);
        }

        var profiles = await _database.QualityProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Id, cancellationToken)
            .ConfigureAwait(false);

        var unmet = withFile
            .Where(song => !MeetsCutoff(profiles, song))
            .ToList();

        // Already in memory: the filtering above cannot be expressed in SQL, so page it here too.
        var records = ApplySort(unmet.AsQueryable(), paging)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToList();

        return new PagedResult<Song>(records, unmet.Count);
    }

    /// <summary>Everything a song resource needs, so no endpoint has to fall back to lazy loading.</summary>
    private IQueryable<Song> Songs() => _database.Songs
        .AsNoTracking()
        .Include(song => song.PrimaryArtist)
        .Include(song => song.AlbumContext)
        .Include(song => song.File);

    /// <summary>
    /// A profile the song does not have never counts as met: without one there is no cutoff to reach.
    /// </summary>
    private static bool MeetsCutoff(Dictionary<long, QualityProfile> profiles, Song song) =>
        song.File is not null
        && profiles.TryGetValue(song.QualityProfileId, out var profile)
        && profile.MeetsCutoff(song.File.QualityId);

    /// <summary>
    /// Sort keys are case-insensitive; anything unknown — including the default — is
    /// <c>added</c> (<see cref="Wondarr.Core.Persistence.EntityBase.CreatedAt"/>). The id breaks
    /// ties so that paging the same query twice cannot reorder or drop rows.
    /// </summary>
    private static IQueryable<Song> ApplySort(IQueryable<Song> query, PagingSpec paging) =>
        paging.SortKey?.ToLowerInvariant() switch
        {
            "title" => paging.Descending
                ? query.OrderByDescending(song => song.Title).ThenBy(song => song.Id)
                : query.OrderBy(song => song.Title).ThenBy(song => song.Id),
            "artist" => paging.Descending
                ? query.OrderByDescending(song => song.ArtistCredit).ThenBy(song => song.Id)
                : query.OrderBy(song => song.ArtistCredit).ThenBy(song => song.Id),
            _ => paging.Descending
                ? query.OrderByDescending(song => song.CreatedAt).ThenBy(song => song.Id)
                : query.OrderBy(song => song.CreatedAt).ThenBy(song => song.Id),
        };
}
