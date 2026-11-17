using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Warns about music videos whose file is no longer where their row says (ADR-0014): the video was moved or
/// deleted outside Wondarr, or a move of the audio file missed it. It names at most
/// <see cref="MaxNamed"/> of them and counts all. A video whose row names a path inside a Compact staging folder
/// counts too: it is where the row says, but beside no track. It is off while the feature is off, and a share that does not
/// answer is a notice, not a warning: the files are not known to be gone.
/// </summary>
public sealed class MusicVideoOrphanHealthCheck : IHealthCheck
{
    /// <summary>The most videos the message names.</summary>
    internal const int MaxNamed = 20;

    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly WondarrDbContext _database;
    private readonly IDiskOperations _disk;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoOrphanHealthCheck"/> class.</summary>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="database">Where the video rows are.</param>
    /// <param name="disk">Asked whether each video file exists.</param>
    public MusicVideoOrphanHealthCheck(
        IOptionsMonitor<MusicVideoOptions> options,
        WondarrDbContext database,
        IDiskOperations disk)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(disk);

        _options = options;
        _database = database;
        _disk = disk;
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies inside a Compact staging folder: a video left there (a taken name, a
    /// move that could not go back) is not beside any track.
    /// </summary>
    private static bool InStagingFolder(string path) =>
        path.Split('/', '\\').Contains(CompactExecutor.StagingFolderName, StringComparer.Ordinal);

    /// <inheritdoc />
    public string Name => nameof(MusicVideoOrphanHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return new HealthCheck(Name, HealthCheckResult.Ok, "Music videos are turned off", null);
        }

        var rows = await _database.MusicVideos
            .AsNoTracking()
            .Where(video => video.Status == MusicVideoStatus.Present && video.Path != null)
            .OrderBy(video => video.SongId)
            .Select(video => video.Path!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string> missing;

        try
        {
            // One bounded call for the whole list: a hung share costs one timeout, not one per video.
            missing = await MusicVideoDisk
                .RunAsync(() => rows.Where(path => InStagingFolder(path) || !_disk.FileExists(path)).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            return new HealthCheck(
                Name,
                HealthCheckResult.Notice,
                "The music video files could not be checked because the disk did not answer in time",
                null);
        }

        if (missing.Count == 0)
        {
            return new HealthCheck(Name, HealthCheckResult.Ok, "Every music video file is where its row says", null);
        }

        var named = string.Join(", ", missing.Take(MaxNamed));
        var more = missing.Count > MaxNamed
            ? string.Concat(" and ", (missing.Count - MaxNamed).ToString(CultureInfo.InvariantCulture), " more")
            : string.Empty;

        return new HealthCheck(
            Name,
            HealthCheckResult.Warning,
            string.Concat(
                missing.Count.ToString(CultureInfo.InvariantCulture),
                missing.Count == 1 ? " music video file is" : " music video files are",
                " missing, or stranded in a Compact staging folder, where Wondarr recorded ",
                missing.Count == 1 ? "it" : "them",
                ": ",
                named,
                more,
                ". Remove the video from its song, or put the file back."),
            null);
    }
}
