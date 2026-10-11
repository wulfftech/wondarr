using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Reports a recycle bin (<c>import.recycle_bin_path</c>) that is, holds or sits inside a library or
/// reference-library root. The bin's cleanup deletes every old file under it, so such a setting would
/// delete somebody's collection; the cleanup refuses to run on it, and this says why. The setting
/// lives in <c>config.yml</c>, which cannot see the database, so the check is made here.
/// </summary>
public sealed class RecycleBinHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly WondarrPaths _paths;

    /// <summary>Initialises a new instance of the <see cref="RecycleBinHealthCheck"/> class.</summary>
    /// <param name="database">Where the library and reference-library roots are.</param>
    /// <param name="options">The configured bin.</param>
    /// <param name="paths">The resolved paths, which decide the default bin.</param>
    public RecycleBinHealthCheck(WondarrDbContext database, IOptionsMonitor<ImportOptions> options, WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        _database = database;
        _options = options;
        _paths = paths;
    }

    /// <inheritdoc />
    public string Name => nameof(RecycleBinHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var bin = _options.CurrentValue.ResolveRecycleBinPath(_paths);

        var roots = await _database.Libraries
            .AsNoTracking()
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var references = await _database.ReferenceLibraries
            .AsNoTracking()
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var root in roots.Concat(references))
        {
            try
            {
                if (PathRules.IsInside(bin, root.RootPath) || PathRules.IsInside(root.RootPath, bin))
                {
                    return new HealthCheck(
                        Name,
                        HealthCheckResult.Error,
                        $"The recycle bin '{bin}' overlaps the library '{root.Name}' ('{root.RootPath}'), so its cleanup is switched off "
                        + "to protect that library's files. Move import.recycle_bin_path somewhere else.",
                        null);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path: it cannot overlap anything.
            }
        }

        return new HealthCheck(Name, HealthCheckResult.Ok, "The recycle bin is clear of every library", null);
    }
}
