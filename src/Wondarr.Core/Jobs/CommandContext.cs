namespace Wondarr.Core.Jobs;

/// <summary>Everything a command handler is given about the command it is running.</summary>
/// <param name="CommandId">The <c>command</c> row this handler is running.</param>
/// <param name="Body">The raw JSON body the caller sent, or <see langword="null"/>.</param>
/// <param name="Trigger">What queued the command.</param>
/// <param name="ReportProgressAsync">
/// Writes a progress message onto the command row and publishes an update. Long-running handlers
/// call this so the UI and the API see progress.
/// </param>
public sealed record CommandContext(
    long CommandId,
    string? Body,
    CommandTrigger Trigger,
    Func<string, Task> ReportProgressAsync);
