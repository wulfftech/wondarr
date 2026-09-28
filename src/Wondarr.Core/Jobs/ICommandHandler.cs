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
    /// Runs the command.
    /// </summary>
    /// <param name="context">The command's id, body and progress callback.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>A completion message for the user, or <see langword="null"/>.</returns>
    /// <exception cref="Exception">Anything thrown fails the command and is recorded as its exception.</exception>
    Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}
