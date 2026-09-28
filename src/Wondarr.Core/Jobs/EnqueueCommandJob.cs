using Microsoft.Extensions.Logging;
using Quartz;

namespace Wondarr.Core.Jobs;

/// <summary>
/// The Quartz job behind every scheduled task. It does one thing — put the command on the queue as a
/// scheduled trigger — so all the state a run leaves behind is written by the executor and
/// <see cref="JobTableUpdater"/>, exactly as for a command a person asked for.
/// <para>
/// Concurrent execution is disallowed so a slow run cannot pile up behind itself; the queue would
/// return the command already running under that name anyway.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class EnqueueCommandJob : IJob
{
    /// <summary>The job-data key holding the command name.</summary>
    public const string CommandNameKey = "CommandName";

    private readonly ICommandQueue _commandQueue;
    private readonly ILogger<EnqueueCommandJob> _logger;

    /// <summary>Initialises a new instance of the <see cref="EnqueueCommandJob"/> class.</summary>
    public EnqueueCommandJob(ICommandQueue commandQueue, ILogger<EnqueueCommandJob> logger)
    {
        ArgumentNullException.ThrowIfNull(commandQueue);
        ArgumentNullException.ThrowIfNull(logger);

        _commandQueue = commandQueue;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var name = context.MergedJobDataMap.GetString(CommandNameKey);

        if (string.IsNullOrWhiteSpace(name))
        {
            LogMissingCommandName(context.JobDetail.Key.Name);

            return;
        }

        try
        {
            await _commandQueue
                .EnqueueAsync(name, null, CommandTrigger.Scheduled, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnknownCommandException exception)
        {
            // A task whose handler was removed must not kill the trigger: log it and let the next
            // tick try again, so re-adding the handler heals the schedule without a restart.
            LogUnknownCommand(name, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job {JobName} has no command name in its job data map")]
    private partial void LogMissingCommandName(string jobName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled command {CommandName} has no handler")]
    private partial void LogUnknownCommand(string commandName, Exception exception);
}
