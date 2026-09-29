using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.References;

/// <summary>
/// The daily reference-library scan (ARCHITECTURE §5.5), and the "scan now" button behind it. One
/// library, or every enabled one, each in a scope of its own so a library that cannot be reached does
/// not take the others down with it.
/// </summary>
public sealed partial class ReferenceLibraryScanCommandHandler(
    IServiceScopeFactory scopes,
    ILogger<ReferenceLibraryScanCommandHandler> logger) : ICommandHandler
{
    /// <summary>The name the <c>ReferenceLibraryScan</c> scheduled task queues.</summary>
    public const string CommandName = "ReferenceLibraryScan";

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

        var requested = ReadLibraryId(context.Body);
        var libraries = await SelectAsync(requested, cancellationToken).ConfigureAwait(false);

        if (libraries.Count == 0)
        {
            return requested is { } id
                ? string.Concat("Reference library ", id.ToString(CultureInfo.InvariantCulture), " was not found.")
                : "No enabled reference libraries.";
        }

        var totals = new ReferenceScanResult(0, 0, 0, 0, 0, 0);
        var unavailable = 0;

        foreach (var libraryId in libraries)
        {
            // A library that cannot be scanned must not stop the batch, and a scope of its own keeps its
            // half-written rows out of the next library's context.
            using var scope = scopes.CreateScope();

            try
            {
                var scanner = scope.ServiceProvider.GetRequiredService<IReferenceScanner>();
                var result = await scanner
                    .ScanAsync(libraryId, context.ReportProgressAsync, cancellationToken)
                    .ConfigureAwait(false);

                totals = totals with
                {
                    Seen = totals.Seen + result.Seen,
                    Added = totals.Added + result.Added,
                    Changed = totals.Changed + result.Changed,
                    Unchanged = totals.Unchanged + result.Unchanged,
                    Missing = totals.Missing + result.Missing,
                    Unreadable = totals.Unreadable + result.Unreadable,
                };
            }
            catch (ReferenceLibraryUnavailableException exception)
            {
                unavailable++;
                LogUnavailable(logger, libraryId, exception.Message);
            }
        }

        return string.Concat(
            libraries.Count.ToString(CultureInfo.InvariantCulture),
            libraries.Count == 1 ? " library: " : " libraries: ",
            totals.Seen.ToString(CultureInfo.InvariantCulture),
            " files, ",
            totals.Added.ToString(CultureInfo.InvariantCulture),
            " added, ",
            totals.Changed.ToString(CultureInfo.InvariantCulture),
            " changed, ",
            totals.Unchanged.ToString(CultureInfo.InvariantCulture),
            " unchanged, ",
            totals.Missing.ToString(CultureInfo.InvariantCulture),
            " missing, ",
            totals.Unreadable.ToString(CultureInfo.InvariantCulture),
            " unreadable",
            unavailable > 0
                ? string.Concat(", ", unavailable.ToString(CultureInfo.InvariantCulture), " unavailable")
                : string.Empty);
    }

    /// <summary>The libraries to scan: the one named, or every enabled one, in id order.</summary>
    private async Task<List<long>> SelectAsync(long? requested, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await database.ReferenceLibraries
            .AsNoTracking()
            .Where(library => requested == null ? library.Enabled : library.Id == requested)
            .OrderBy(library => library.Id)
            .Select(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The library a scan body names, or <see langword="null"/> for "every enabled library".</summary>
    private static long? ReadLibraryId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        ReferenceLibraryScanBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ReferenceLibraryScanBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ReferenceLibraryScan command body is not valid JSON.", exception);
        }

        if (parsed?.ReferenceLibraryId is { } id and > 0)
        {
            return id;
        }

        // An empty object means "every enabled library"; anything else is a body we cannot read.
        return body.Trim().Trim('{', '}', ' ', '\t', '\r', '\n').Length == 0
            ? null
            : throw new ArgumentException(
                "The ReferenceLibraryScan command body must name a referenceLibraryId, or be empty.");
    }

    /// <summary>The body of a scan now: which library to scan, or nothing for all of them.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="ReferenceLibraryId">The library to scan, or <see langword="null"/> for every enabled one.</param>
    private sealed record ReferenceLibraryScanBody(string? Name, long? ReferenceLibraryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reference library {ReferenceLibraryId} was not scanned; the batch continues")]
    private static partial void LogUnavailable(ILogger logger, long referenceLibraryId, string message);
}