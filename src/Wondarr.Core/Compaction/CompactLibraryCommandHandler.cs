using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Compaction;

/// <summary>
/// The "Compact library" command: applies a library's Compact plan, which is the same plan the dry run
/// shows, to the files, the tags and Plex. It runs in a scope of its own, so the moves it makes cannot
/// leak into whatever resolved it.
/// </summary>
public sealed partial class CompactLibraryCommandHandler(
    IServiceScopeFactory scopes,
    ILogger<CompactLibraryCommandHandler> logger) : ICommandHandler
{
    /// <summary>The name the <c>CompactLibrary</c> command answers to.</summary>
    public const string CommandName = "CompactLibrary";

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var libraryId = ReadLibraryId(context.Body);

        // One scope for the library lookup and the run: the run reads the library again through the
        // same context, and it must be the one the user is looking at.
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var library = await database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == libraryId, cancellationToken)
            .ConfigureAwait(false);

        if (library is null)
        {
            // Thrown, not returned: the command executor turns this into the command's failure message,
            // which is where a user looks for a command that did nothing.
            throw new InvalidOperationException(
                string.Concat(
                    "Library ",
                    libraryId.ToString(CultureInfo.InvariantCulture),
                    " was not found."));
        }

        var executor = scope.ServiceProvider.GetRequiredService<ICompactExecutor>();
        var result = await executor
            .RunAsync(libraryId, context.ReportProgressAsync, cancellationToken)
            .ConfigureAwait(false);

        LogCompacted(logger, libraryId);

        return string.Concat(
            "Compacted ",
            library.Name,
            ": ",
            result.Moved.ToString(CultureInfo.InvariantCulture),
            " files moved, ",
            result.ContextOnly.ToString(CultureInfo.InvariantCulture),
            " album changes without files, ",
            result.Failed.ToString(CultureInfo.InvariantCulture),
            " failed");
    }

    /// <summary>The library a compact body names. Unlike the adopt body, a body without one is a mistake.</summary>
    private static long ReadLibraryId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The CompactLibrary command body must name a libraryId.");
        }

        CompactLibraryBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<CompactLibraryBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The CompactLibrary command body is not valid JSON.", exception);
        }

        if (parsed?.LibraryId is { } id and > 0)
        {
            return id;
        }

        throw new ArgumentException("The CompactLibrary command body must name a libraryId.");
    }

    /// <summary>The body of a Compact library run: which library to compact.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="LibraryId">The library to compact.</param>
    private sealed record CompactLibraryBody(string? Name, long? LibraryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compacted library {LibraryId} through the CompactLibrary command")]
    private static partial void LogCompacted(ILogger logger, long libraryId);
}
