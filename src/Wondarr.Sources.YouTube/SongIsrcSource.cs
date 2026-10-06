using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Persistence;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Reads the ISRCs a song carries, so the YouTube source can run the ISRC-first query spotDL runs.
/// The search request does not carry them (which metadata a source needs is the source's own
/// business), and the provider is a singleton while the database is scoped, so this opens a scope of
/// its own per call.
/// </summary>
public interface ISongIsrcSource
{
    /// <summary>The song's ISRCs, in stored order; empty when the song has none.</summary>
    Task<IReadOnlyList<string>> GetIsrcsAsync(long songId, CancellationToken cancellationToken);
}

/// <summary>The database-backed reader: one song row, no tracking, no caching.</summary>
public sealed class SongIsrcSource : ISongIsrcSource
{
    private readonly IServiceScopeFactory _scopes;

    /// <summary>Initialises a new instance of the <see cref="SongIsrcSource"/> class.</summary>
    /// <param name="scopes">Builds the scope the scoped <see cref="WondarrDbContext"/> lives in.</param>
    public SongIsrcSource(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetIsrcsAsync(long songId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var song = await database.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        return song?.Isrcs ?? [];
    }
}
