using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// At startup, fails the music video rows that are still <see cref="MusicVideoStatus.Queued"/> or
/// <see cref="MusicVideoStatus.Downloading"/> with no queued or running <c>MusicVideoDownload</c> command behind them:
/// the last process was stopped before the download finished, and a list would otherwise show them as on their way for ever.
/// </summary>
/// <remarks>
/// Registered after the command executor, which orphans the commands the last process left behind, so a command that is
/// still queued or started here really is this process's.
/// </remarks>
public sealed partial class MusicVideoStartupReclaim : IHostedService
{
    /// <summary>The reason a reclaimed row gives.</summary>
    public const string Reason = "Wondarr restarted before the download finished";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<MusicVideoStartupReclaim> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoStartupReclaim"/> class.</summary>
    /// <param name="scopeFactory">Gives the database a scope of its own.</param>
    /// <param name="time">The clock <c>updated_at</c> is read from.</param>
    /// <param name="logger">The log sink.</param>
    public MusicVideoStartupReclaim(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<MusicVideoStartupReclaim> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            await ReclaimAsync(database, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A reclaim that fails must never stop Wondarr from starting.
            LogFailed(_logger, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Fails the rows that wait for a download nobody is running.</summary>
    /// <param name="database">The database.</param>
    /// <param name="now">The UTC instant to stamp.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many rows were failed.</returns>
    public async Task<int> ReclaimAsync(WondarrDbContext database, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var stuck = await database.MusicVideos
            .AsNoTracking()
            .Where(video => video.Status == MusicVideoStatus.Queued || video.Status == MusicVideoStatus.Downloading)
            .Select(video => video.SongId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reclaimed = 0;

        foreach (var songId in stuck)
        {
            if (await ActiveDownloads.ExistsAsync(database, songId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            reclaimed += await database.MusicVideos
                .Where(video => video.SongId == songId
                    && (video.Status == MusicVideoStatus.Queued || video.Status == MusicVideoStatus.Downloading))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(video => video.Status, MusicVideoStatus.Failed)
                        .SetProperty(video => video.FailureReason, Reason)
                        .SetProperty(video => video.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (reclaimed > 0)
        {
            LogReclaimed(_logger, reclaimed);
        }

        return reclaimed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed {Count} music video row(s) whose download was interrupted by a restart")]
    private static partial void LogReclaimed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reclaim interrupted music video downloads at startup")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
