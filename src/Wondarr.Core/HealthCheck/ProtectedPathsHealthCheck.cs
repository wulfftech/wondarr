using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Reports a folder setting that names the file system root, Wondarr's data directory or something
/// inside it. Such a setting is refused when it is saved, but a <c>config.yml</c> or a library
/// written before that rule still loads, so this says which setting to change. For
/// <c>soulseek.shared_folders</c> the entry is also left out of what is sent to slskd.
/// </summary>
public sealed class ProtectedPathsHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;
    private readonly IConfiguration _configuration;
    private readonly WondarrPaths _paths;

    /// <summary>Initialises a new instance of the <see cref="ProtectedPathsHealthCheck"/> class.</summary>
    /// <param name="database">Where the library and reference-library roots are.</param>
    /// <param name="configuration">The configuration the folder settings were bound from.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    public ProtectedPathsHealthCheck(WondarrDbContext database, IConfiguration configuration, WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        _database = database;
        _configuration = configuration;
        _paths = paths;
    }

    /// <inheritdoc />
    public string Name => nameof(ProtectedPathsHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        foreach (var folder in _configuration.GetSection("Soulseek:SharedFolders").GetChildren().Select(child => child.Value))
        {
            Add(problems, ProtectedPaths.Failure("soulseek.shared_folders", folder, _paths, sharing: true));
        }

        Add(problems, ProtectedPaths.Failure("soulseek.downloads_dir", _configuration["Soulseek:DownloadsDir"], _paths));
        Add(problems, ProtectedPaths.Failure("youtube.downloads_dir", _configuration["YouTube:DownloadsDir"], _paths));

        var libraries = await _database.Libraries
            .AsNoTracking()
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in libraries)
        {
            Add(problems, ProtectedPaths.Failure($"library '{library.Name}' root", library.RootPath, _paths));
        }

        var references = await _database.ReferenceLibraries
            .AsNoTracking()
            .Select(library => new { library.Name, library.RootPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in references)
        {
            Add(problems, ProtectedPaths.Failure($"reference library '{library.Name}' root", library.RootPath, _paths));
        }

        return problems.Count == 0
            ? new HealthCheck(Name, HealthCheckResult.Ok, "No folder setting names Wondarr's data directory", null)
            : new HealthCheck(Name, HealthCheckResult.Error, string.Join(" ", problems), null);
    }

    private static void Add(List<string> problems, string? problem)
    {
        if (problem is not null)
        {
            problems.Add(problem + ".");
        }
    }
}
