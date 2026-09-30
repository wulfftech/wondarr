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
    private readonly ISettingsRepository _repository;
    private readonly PlexClientIdentifierState _state;

    /// <summary>Initialises a new instance of the <see cref="PlexClientIdentifier"/> class.</summary>
    /// <param name="repository">The settings table the identifier lives in.</param>
    /// <param name="state">The app-wide lock and cache every scope's instance shares.</param>
    public PlexClientIdentifier(ISettingsRepository repository, PlexClientIdentifierState state)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(state);

        _repository = repository;
        _state = state;
    }

    /// <inheritdoc />
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_state.Cached is { } cached)
        {
            return cached;
        }

        await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-read under the lock: another scope may have generated one while this one waited.
            if (_state.Cached is { } current)
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

            _state.Cached = settings.ClientIdentifier;

            return settings.ClientIdentifier;
        }
        finally
        {
            _state.Gate.Release();
        }
    }
}

/// <summary>
/// The lock and cache behind <see cref="PlexClientIdentifier"/>, registered as a singleton: the
/// identifier service is scoped, so its own fields would leave each scope free to generate an
/// identifier of its own. One per app (not a static) keeps two hosts in one process — the API tests —
/// from sharing an identifier that belongs to another host's database.
/// </summary>
public sealed class PlexClientIdentifierState
{
    /// <summary>Gets the lock that serialises get-or-create.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Gets or sets the identifier once read or generated.</summary>
    internal string? Cached
    {
        get => Volatile.Read(ref _cached);
        set => Volatile.Write(ref _cached, value);
    }

    private string? _cached;
}
