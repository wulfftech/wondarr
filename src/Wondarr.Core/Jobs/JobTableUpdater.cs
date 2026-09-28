using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Jobs;

/// <summary>
/// Writes the outcome of a command back to its <c>job</c> row, so the Tasks list shows when a task
/// last ran whether it was scheduled or asked for by hand — "run now" is just
/// <c>POST /api/v1/command</c>.
/// <para>
/// A command with no job row (anything that is not a scheduled task) is ignored: this handler follows
/// the schedule, it does not create one.
/// </para>
/// </summary>
public sealed class JobTableUpdater : IHandle<CommandUpdatedEvent>
{
    /// <summary>How much of the outcome the <c>job</c> table keeps.</summary>
    public const int MaxResultLength = 500;

    private readonly WondarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="JobTableUpdater"/> class.</summary>
    public JobTableUpdater(WondarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public async Task HandleAsync(CommandUpdatedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var command = message.Command;

        // Only a finished run says anything about the schedule; queued, started and cancelled rows
        // leave the previous run's numbers in place.
        if (command.Status is not (CommandStatus.Completed or CommandStatus.Failed)
            || command.EndedAt is not { } endedAt)
        {
            return;
        }

        var job = await _context.Jobs
            .FirstOrDefaultAsync(row => row.Name == command.Name, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            return;
        }

        job.LastRunAt = endedAt;
        job.NextRunAt = job.Interval is { } interval ? endedAt + interval : null;
        job.LastResult = Describe(command);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders <c>"&lt;result&gt;: &lt;message or exception&gt;"</c>, trimmed to what the table keeps.</summary>
    internal static string Describe(CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var successful = command.Result == CommandResult.Successful;
        var detail = successful
            ? command.Message ?? command.Exception
            : command.Exception ?? command.Message;

        var result = successful ? "successful" : "unsuccessful";
        var text = string.IsNullOrWhiteSpace(detail) ? result : $"{result}: {detail}";

        return text.Length <= MaxResultLength ? text : text[..MaxResultLength];
    }
}
