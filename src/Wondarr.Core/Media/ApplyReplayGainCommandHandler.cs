using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Media;

/// <summary>
/// The <c>ApplyReplayGain</c> command: gives every file of a library that has no ReplayGain values
/// its track gain and peak, one song at a time under that song's lock. A reference file is never
/// touched, and one file's failure never stops the others. Every song runs in a scope of its own, so
/// a library of thousands does not grow one change tracker.
/// </summary>
public sealed partial class ApplyReplayGainCommandHandler : ICommandHandler
{
    /// <summary>The command name the queue and the API use.</summary>
    public const string CommandName = "ApplyReplayGain";

    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ApplyReplayGainCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="ApplyReplayGainCommandHandler"/> class.</summary>
    /// <param name="scopes">Builds one scope per run for the lookup and one per song.</param>
    /// <param name="logger">The logger.</param>
    public ApplyReplayGainCommandHandler(IServiceScopeFactory scopes, ILogger<ApplyReplayGainCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var libraryId = ReadLibraryId(context.Body);

        List<long> todo;
        var skipped = 0;
        string libraryName;

        using (var scope = _scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

            var library = await database.Libraries
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == libraryId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Library {libraryId} was not found."));

            if (!library.ReplayGain)
            {
                throw new InvalidOperationException(
                    $"ReplayGain is off for {library.Name}; turn it on in the library's settings first.");
            }

            libraryName = library.Name;

            var files = await database.SongFiles
                .AsNoTracking()
                .Where(file => file.Song.LibraryId == libraryId)
                .OrderBy(file => file.Id)
                .Select(file => new { file.Id, file.SourceType, file.ReplayGainDb, file.ReplayGainPeak })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var eligible = new List<long>();

            foreach (var file in files)
            {
                if (file.SourceType == SourceTypes.Reference
                    || (file.ReplayGainDb is not null && file.ReplayGainPeak is not null))
                {
                    skipped++;
                }
                else
                {
                    eligible.Add(file.Id);
                }
            }

            todo = eligible;
        }

        var done = 0;
        var failed = 0;
        var processed = 0;
        string? firstProblem = null;

        foreach (var fileId in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (ReplayGainOutcome Outcome, string? Reason) result;

            try
            {
                using var scope = _scopes.CreateScope();
                result = await scope.ServiceProvider
                    .GetRequiredService<IReplayGainApplier>()
                    .ApplyAsync(fileId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFileFailed(_logger, fileId, exception);
                result = (ReplayGainOutcome.Failed, exception.Message);
            }

            switch (result.Outcome)
            {
                case ReplayGainOutcome.Done:
                    done++;
                    break;
                case ReplayGainOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    firstProblem ??= string.Create(CultureInfo.InvariantCulture, $"file {fileId}: {result.Reason}");
                    break;
            }

            processed++;
            await context
                .ReportProgressAsync(string.Create(CultureInfo.InvariantCulture, $"{processed} of {todo.Count} done"))
                .ConfigureAwait(false);
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"ReplayGain for {libraryName}: {done} done, {failed} failed, {skipped} skipped");

        return firstProblem is null ? summary : string.Concat(summary, " (first problem: ", firstProblem, ")");
    }

    private static long ReadLibraryId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The ApplyReplayGain command body must name a libraryId.");
        }

        ApplyReplayGainBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ApplyReplayGainBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ApplyReplayGain command body is not valid JSON.", exception);
        }

        return parsed?.LibraryId is { } id and > 0
            ? id
            : throw new ArgumentException("The ApplyReplayGain command body must name a libraryId.");
    }

    private sealed record ApplyReplayGainBody(string? Name, long? LibraryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying ReplayGain to song file {FileId} failed unexpectedly")]
    private static partial void LogFileFailed(ILogger logger, long fileId, Exception exception);
}
