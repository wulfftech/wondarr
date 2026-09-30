using Wondarr.Core.HealthCheck;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Plex;

/// <summary>
/// Reports a partial scan Plex would not run. A scan that fails is not an error — the file is on disk
/// and Plex finds it on its next full scan — but nothing else would tell the user why the album is
/// still missing from their server.
/// </summary>
public sealed class PlexHealthCheck : IHealthCheck
{
    private readonly IPlexLibraryUpdater _updater;
    private readonly IPlexConnectionService _connection;

    /// <summary>Initialises a new instance of the <see cref="PlexHealthCheck"/> class.</summary>
    /// <param name="updater">Holds the last failure the scan loop saw.</param>
    /// <param name="connection">Says whether a server is connected at all.</param>
    public PlexHealthCheck(IPlexLibraryUpdater updater, IPlexConnectionService connection)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(connection);

        _updater = updater;
        _connection = connection;
    }

    /// <inheritdoc />
    public string Name => nameof(PlexHealthCheck);

    /// <inheritdoc />
    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        // Nothing is reported until a scan has actually failed: not signed in, or never imported to a
        // linked library, is the normal state of a new install.
        if (_updater.LastError is not { } error)
        {
            return new HealthReport(Name, HealthCheckResult.Ok, "Plex partial scans are up to date", WikiUrl: null);
        }

        if (await _connection.GetServerContextAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new HealthReport(Name, HealthCheckResult.Ok, "No Plex server is connected", WikiUrl: null);
        }

        // The message comes from a PlexException, which never carries the token.
        return new HealthReport(
            Name,
            HealthCheckResult.Warning,
            $"Plex partial scan failed: {error}",
            WikiUrl: null);
    }
}
