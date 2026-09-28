using Compilarr.Core.Jobs;

namespace Compilarr.Core.Tests.Jobs;

/// <summary>A handler that always throws, for the failed-command path.</summary>
internal sealed class ThrowingCommandHandler : ICommandHandler
{
    /// <summary>The message the executor has to record on the failed command.</summary>
    public const string FailureMessage = "the handler exploded";

    public ThrowingCommandHandler(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
    }

    public string Name { get; }

    public Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(FailureMessage);
}
