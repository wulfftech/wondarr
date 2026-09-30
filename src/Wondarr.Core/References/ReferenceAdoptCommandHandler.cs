using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.References;

/// <summary>
/// The "adopt now" command: hands the identified files of an adopt-mode reference library over to its
/// target library. One library, or every enabled adopt-mode one, each in a scope of its own so a
/// library that fails does not take the others down with it.
/// </summary>
public sealed partial class ReferenceAdoptCommandHandler(
    IServiceScopeFactory scopes,
    ILogger<ReferenceAdoptCommandHandler> logger) : ICommandHandler
{
    /// <summary>The name the <c>ReferenceAdopt</c> command answers to.</summary>
    public const string CommandName = "ReferenceAdopt";

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
                : "No enabled reference libraries to adopt from.";
        }

        var totals = new ReferenceAdoptResult(0, 0, 0);
        var unavailable = 0;

        foreach (var libraryId in libraries)
        {
            // A library that cannot be adopted from must not stop the batch, and a scope of its own
            // keeps its half-written rows out of the next library's context.
            using var scope = scopes.CreateScope();

            try
            {
                var adopter = scope.ServiceProvider.GetRequiredService<IReferenceAdopter>();
                var result = await adopter
                    .AdoptAsync(libraryId, context.ReportProgressAsync, cancellationToken)
                    .ConfigureAwait(false);

                totals = totals with
                {
                    Adopted = totals.Adopted + result.Adopted,
                    Skipped = totals.Skipped + result.Skipped,
                    Failed = totals.Failed + result.Failed,
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unavailable++;
                LogAdoptFailed(logger, libraryId, exception);
            }
        }

        return string.Concat(
            libraries.Count.ToString(CultureInfo.InvariantCulture),
            libraries.Count == 1 ? " library: " : " libraries: ",
            totals.Adopted.ToString(CultureInfo.InvariantCulture),
            " adopted, ",
            totals.Skipped.ToString(CultureInfo.InvariantCulture),
            " skipped, ",
            totals.Failed.ToString(CultureInfo.InvariantCulture),
            " failed",
            unavailable > 0
                ? string.Concat(", ", unavailable.ToString(CultureInfo.InvariantCulture), " unavailable")
                : string.Empty);
    }

    /// <summary>The libraries to adopt from: the one named, or every enabled adopt-mode one, in id order.</summary>
    private async Task<List<long>> SelectAsync(long? requested, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await database.ReferenceLibraries
            .AsNoTracking()
            .Where(library => requested == null
                ? library.Enabled && library.Mode == ReferenceLibraryMode.Adopt
                : library.Id == requested)
            .OrderBy(library => library.Id)
            .Select(library => library.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The library an adopt body names, or <see langword="null"/> for "every enabled library".</summary>
    private static long? ReadLibraryId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        ReferenceAdoptBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ReferenceAdoptBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ReferenceAdopt command body is not valid JSON.", exception);
        }

        if (parsed?.ReferenceLibraryId is { } id and > 0)
        {
            return id;
        }

        // An empty object means "every enabled library"; anything else is a body we cannot read.
        return body.Trim().Trim('{', '}', ' ', '\t', '\r', '\n').Length == 0
            ? null
            : throw new ArgumentException(
                "The ReferenceAdopt command body must name a referenceLibraryId, or be empty.");
    }

    /// <summary>The body of an adopt now: which library to adopt from, or nothing for all of them.</summary>
    /// <param name="Name">The command name, echoed by the API for readability.</param>
    /// <param name="ReferenceLibraryId">The library to adopt from, or <see langword="null"/> for every enabled one.</param>
    private sealed record ReferenceAdoptBody(string? Name, long? ReferenceLibraryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Adopting from reference library {ReferenceLibraryId} failed; the batch continues")]
    private static partial void LogAdoptFailed(ILogger logger, long referenceLibraryId, Exception exception);
}
