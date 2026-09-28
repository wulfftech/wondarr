using Microsoft.Extensions.Logging;

namespace Compilarr.Core.Jobs;

/// <summary>
/// The cheapest command there is: proves the queue, the executor and the API are wired up.
/// </summary>
public sealed partial class HeartbeatCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/command</c> uses.</summary>
    public const string CommandName = "Heartbeat";

    private readonly ILogger<HeartbeatCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="HeartbeatCommandHandler"/> class.</summary>
    public HeartbeatCommandHandler(ILogger<HeartbeatCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        LogHeartbeat();

        return Task.FromResult<string?>("Heartbeat OK");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat received")]
    private partial void LogHeartbeat();
}
