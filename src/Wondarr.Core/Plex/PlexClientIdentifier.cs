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
/// Reads the client identifier out of the <c>plex</c> setting. The Plex Media Server client needs it
/// too, and it cannot ask the connection service for it without a dependency cycle.
/// </summary>
public sealed class PlexClientIdentifier : IPlexClientIdentifier
{
    private readonly ISettingsRepository _repository;

    private string? _cached;

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
        if (_cached is not null)
        {
            return _cached;
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
}
