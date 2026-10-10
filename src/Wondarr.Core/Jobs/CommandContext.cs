namespace Wondarr.Core.Jobs;

/// <summary>Everything a command handler is given about the command it is running.</summary>
/// <param name="CommandId">The <c>command</c> row this handler is running.</param>
/// <param name="Body">The raw JSON body the caller sent, or <see langword="null"/>.</param>
/// <param name="Trigger">What queued the command.</param>
/// <param name="ReportProgressAsync">
/// Writes a progress message onto the command row and publishes an update. Long-running handlers
/// call this so the UI and the API see progress.
/// </param>
/// <param name="YieldWorker">
/// Lets a handler that is about to sit idle (waiting for a download slot, say) hand its executor
/// worker back so other commands can run meanwhile. It returns a lease to dispose when the handler
/// wants to work again (disposing waits for a worker), or <see langword="null"/> when no worker could
/// be spared and the handler keeps its own. <see langword="null"/> when the host has no such pool.
/// </param>
public sealed record CommandContext(
    long CommandId,
    string? Body,
    CommandTrigger Trigger,
    Func<string, Task> ReportProgressAsync,
    Func<IAsyncDisposable?>? YieldWorker = null);
