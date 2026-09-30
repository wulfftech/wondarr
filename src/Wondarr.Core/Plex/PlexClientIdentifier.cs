using Wondarr.Core.Persistence;

namespace Wondarr.Core.Plex;

/// <summary>The stable client identifier every Plex request is stamped with.</summary>
public interface IPlexClientIdentifier
{
    /// <summary>
    /// Returns the identifier, generating and storing one on first use. It identifies this Wondarr
    /// install to plex.tv, so it is created once and kept for the life of the installation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The only place the client identifier is read or generated. The identifier names this Wondarr
/// install to plex.tv, so it has to be the same one for the sign-in, for the connection service and
/// for every request to a server — and it must be generated exactly once, however many scopes ask
/// for it at the same moment.
/// </summary>
public sealed class PlexClientIdentifier : IPlexClientIdentifier
{
    /// <summary>
    /// Serialises get-or-create across the whole process: the class is registered per scope, so an
    /// instance field would leave each scope free to generate its own identifier.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static volatile string? _cached;

    private readonly ISettingsRepository _repository;

    /// <summary>Initialises a new instance of the <see cref="PlexClientIdentifier"/> class.</summary>
    /// <param name="repository">The settings table the identifier lives in.</param>
    public PlexClientIdentifier(ISettingsRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        _repository = repository;
    }

    /// <inheritdoc />
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-read under the lock: another scope may have generated one while this one waited.
            if (_cached is { } current)
            {
                return current;
            }

            var settings = await _repository
                .GetAsync<PlexConnectionSettings>(PlexConnectionService.SettingKey, cancellationToken)
                .ConfigureAwait(false);

            if (settings is null || string.IsNullOrWhiteSpace(settings.ClientIdentifier))
            {
                settings = (settings ?? new PlexConnectionSettings())
                    with { ClientIdentifier = Guid.NewGuid().ToString("N") };

                await _repository
                    .SetAsync(PlexConnectionService.SettingKey, settings, cancellationToken)
                    .ConfigureAwait(false);
            }

            _cached = settings.ClientIdentifier;

            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Forgets the cached identifier. The cache is process-wide, so a test that starts from its own
    /// empty database has to drop it first; production never has a second database to move to.
    /// </summary>
    internal static void ResetCacheForTests() => _cached = null;
}
