namespace Wondarr.Core.Searching;

/// <summary>
/// What a search that has to wait for a download slot may use from the command that runs it: a way to
/// say so on the Tasks page, and a way to hand the executor worker back while it waits. One instance
/// per DI scope, filled in by the command handler; empty for a search that is not a command (the
/// missing-song batch, an API call), which then waits on its own thread.
/// </summary>
public sealed class SlotWaitContext
{
    /// <summary>Gets or sets the command's progress report, or <see langword="null"/> outside a command.</summary>
    public Func<string, Task>? ReportProgressAsync { get; set; }

    /// <summary>
    /// Gets or sets the command's worker hand-back (see
    /// <see cref="Jobs.CommandContext.YieldWorker"/>), or <see langword="null"/> outside a command.
    /// </summary>
    public Func<IAsyncDisposable?>? YieldWorker { get; set; }
}
