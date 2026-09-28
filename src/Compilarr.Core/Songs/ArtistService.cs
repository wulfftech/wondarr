using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compilarr.Core.Songs;

/// <summary>An artist together with how many songs credit them.</summary>
/// <param name="Artist">The artist.</param>
/// <param name="SongCount">How many songs credit them, in any role.</param>
public sealed record ArtistSummary(Artist Artist, int SongCount);

/// <summary>The read side of the artist list.</summary>
public interface IArtistService
{
    /// <summary>Lists every artist with its song count, ordered by sort name.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The artists, sort name first.</returns>
    Task<IReadOnlyList<ArtistSummary>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one artist.</summary>
    /// <param name="id">The artist id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The artist, or <see langword="null"/> when the id is unknown.</returns>
    Task<Artist?> GetAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Backs the artist list. A featured credit counts exactly like a main one: an artist who only ever
/// guests on other people's songs still shows up with those songs.
/// </summary>
public sealed class ArtistService : IArtistService
{
    private readonly CompilarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="ArtistService"/> class.</summary>
    /// <param name="database">The Compilarr database.</param>
    public ArtistService(CompilarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtistSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _database.SongArtists
            .AsNoTracking()
            .GroupBy(credit => credit.ArtistId)
            .Select(group => new { ArtistId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ArtistId, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        var artists = await _database.Artists
            .AsNoTracking()
            .OrderBy(artist => artist.SortName)
            .ThenBy(artist => artist.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. artists.Select(artist => new ArtistSummary(artist, counts.GetValueOrDefault(artist.Id)))];
    }

    /// <inheritdoc />
    public async Task<Artist?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        await _database.Artists
            .AsNoTracking()
            .FirstOrDefaultAsync(artist => artist.Id == id, cancellationToken)
            .ConfigureAwait(false);
}
