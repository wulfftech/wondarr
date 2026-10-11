using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Compaction;

/// <summary>
/// At startup, queues <c>CompactLibrary</c> for every library that has unfinished <c>compact_move</c>
/// rows, so a compaction a restart interrupted finishes on its own instead of leaving its files in the
/// hidden staging folder. It goes through the command queue, so a run that is already queued is not
/// queued twice.
/// </summary>
/// <remarks>
/// Registered after the migration service and the command executor: the tables exist, and the executor
/// has already orphaned the commands the last process left behind, so what is queued here survives.
/// </remarks>
public sealed partial class CompactResumeHostedService : IHostedService
{
    private static readonly JsonSerializerOptions BodyJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CompactResumeHostedService> _logger;

    /// <summary>Initialises a new instance of the <see cref="CompactResumeHostedService"/> class.</summary>
    /// <param name="scopeFactory">Gives the database and the command queue a scope of their own.</param>
    /// <param name="logger">The log sink.</param>
    public CompactResumeHostedService(IServiceScopeFactory scopeFactory, ILogger<CompactResumeHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            var commands = scope.ServiceProvider.GetRequiredService<ICommandQueue>();

            var libraries = await database.CompactMoves
                .AsNoTracking()
                .Where(CompactMoveRules.IsUnfinished)
                .Select(row => row.LibraryId)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var libraryId in libraries)
            {
                var body = JsonSerializer.Serialize(
                    new { name = CompactLibraryCommandHandler.CommandName, libraryId, resumeOnly = true },
                    BodyJson);

                await commands
                    .EnqueueAsync(
                        CompactLibraryCommandHandler.CommandName,
                        body,
                        CommandTrigger.Scheduled,
                        cancellationToken)
                    .ConfigureAwait(false);

                LogResuming(_logger, libraryId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A start-up nicety must never stop the host: the health warning still names the library.
            LogFailed(_logger, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library {LibraryId} has unfinished compaction moves from before the restart; queueing Compact library to finish them")]
    private static partial void LogResuming(ILogger logger, long libraryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not queue the compactions an earlier run left unfinished")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
