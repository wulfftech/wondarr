using System.Globalization;
using System.Text.RegularExpressions;
using Wondarr.Core.Jobs;

namespace Wondarr.Api.Commands;

/// <summary>
/// A command as the *arr clients expect it. The field names match Lidarr's
/// <c>CommandResource</c> (<c>src/Lidarr.Api.V1/Commands/CommandResource.cs</c>); the priority is a
/// constant because Wondarr has no priorities yet.
/// </summary>
/// <param name="Id">The <c>command</c> row id.</param>
/// <param name="Name">The command name as queued, for example <c>CheckHealth</c>.</param>
/// <param name="CommandName">The display form of the name, for example <c>Check Health</c>.</param>
/// <param name="Message">The completion or progress message, or <see langword="null"/>.</param>
/// <param name="Body">The raw JSON body the caller sent, or <see langword="null"/>.</param>
/// <param name="Priority">Always <c>normal</c> until priorities exist.</param>
/// <param name="Status">Where the command is in its life cycle.</param>
/// <param name="Result">Whether it succeeded.</param>
/// <param name="Queued">The UTC instant the command was queued.</param>
/// <param name="Started">The UTC instant it started, or <see langword="null"/>.</param>
/// <param name="Ended">The UTC instant it ended, or <see langword="null"/>.</param>
/// <param name="Duration">How long it ran, formatted <c>hh:mm:ss.fffffff</c>, or <see langword="null"/>.</param>
/// <param name="Exception">The failure message, or <see langword="null"/>.</param>
/// <param name="Trigger">What queued the command.</param>
/// <param name="StateChangeTime">The instant of the latest state change, as Lidarr reports it.</param>
public sealed record CommandResource(
    long Id,
    string Name,
    string CommandName,
    string? Message,
    string? Body,
    string Priority,
    CommandStatus Status,
    CommandResult Result,
    DateTime Queued,
    DateTime? Started,
    DateTime? Ended,
    string? Duration,
    string? Exception,
    CommandTrigger Trigger,
    DateTime? StateChangeTime);

/// <summary>Maps a stored <see cref="CommandRecord"/> onto the API resource.</summary>
public static partial class CommandResourceMapper
{
    /// <summary>The only priority Wondarr has; the field exists so *arr clients do not choke on it.</summary>
    public const string NormalPriority = "normal";

    /// <summary>Builds the resource for <paramref name="record"/>.</summary>
    public static CommandResource ToResource(this CommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new CommandResource(
            record.Id,
            record.Name,
            SplitCamelCase(record.Name),
            record.Message,
            record.Body,
            NormalPriority,
            record.Status,
            record.Result,
            record.QueuedAt,
            record.StartedAt,
            record.EndedAt,
            FormatDuration(record.StartedAt, record.EndedAt),
            record.Exception,
            record.Trigger,
            record.StartedAt ?? record.EndedAt);
    }

    /// <summary>Turns <c>CheckHealth</c> into <c>Check Health</c> for the UI.</summary>
    internal static string SplitCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : CamelBoundary().Replace(name, " $1");

    /// <summary>
    /// Renders the run time the way the *arrs do, or <see langword="null"/> when the command has not
    /// finished.
    /// </summary>
    internal static string? FormatDuration(DateTime? startedAt, DateTime? endedAt)
    {
        if (startedAt is null || endedAt is null)
        {
            return null;
        }

        return (endedAt.Value - startedAt.Value)
            .ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();
}
