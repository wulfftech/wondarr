namespace Compilarr.Core.Jobs;

/// <summary>Thrown when a command is enqueued under a name no <see cref="ICommandHandler"/> answers to.</summary>
public sealed class UnknownCommandException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="UnknownCommandException"/> class.</summary>
    public UnknownCommandException()
    {
    }

    /// <summary>Initialises a new instance of the <see cref="UnknownCommandException"/> class.</summary>
    /// <param name="name">The unrecognised command name.</param>
    public UnknownCommandException(string name)
        : base($"Unknown command '{name}'.")
    {
        CommandName = name;
    }

    /// <summary>Initialises a new instance of the <see cref="UnknownCommandException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public UnknownCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the unrecognised command name, or an empty string when it was not supplied.</summary>
    public string CommandName { get; } = string.Empty;
}
