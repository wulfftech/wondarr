namespace Wondarr.Core.Jobs;

/// <summary>
/// Runs one named command. Handlers are registered as <see cref="ICommandHandler"/> services and
/// resolved in a fresh DI scope for every command the executor runs.
/// </summary>
public interface ICommandHandler
{
    /// <summary>Gets the command name this handler answers to, for example <c>Heartbeat</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether a user may cancel the command while it runs. Most handlers cannot
    /// be stopped half way and keep the default; one that has a stretch where it only waits (a search
    /// waiting for a download slot) opts in. The executor then fires <see cref="CommandContext.UserCancellation"/>
    /// on a cancel request. The handler honours it only where stopping is safe and lets the
    /// <see cref="OperationCanceledException"/> out; the run then ends <see cref="CommandStatus.Cancelled"/>. A
    /// handler that ignores it simply completes.
    /// </summary>
    bool CancelWhileRunning => false;

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="context">The command's id, body and progress callback.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>A completion message for the user, or <see langword="null"/>.</returns>
    /// <exception cref="Exception">Anything thrown fails the command and is recorded as its exception.</exception>
    Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}
