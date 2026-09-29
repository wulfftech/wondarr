using System.Globalization;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Importing;

/// <summary>
/// The <c>queue</c> section of <c>config.yml</c>: how often the queue poll runs, and how long a grab
/// may sit in one state before Wondarr gives up on the peer (ARCHITECTURE §5.5).
/// </summary>
public sealed class QueueOptions
{
    /// <summary>How long the poll waits between cycles while a download is in flight.</summary>
    public int ActivePollSeconds { get; set; } = 10;

    /// <summary>How long the poll waits between cycles when nothing is being downloaded.</summary>
    public int IdlePollSeconds { get; set; } = 60;

    /// <summary>How long a grab may stay <c>Queued</c> — the peer has not acknowledged it — before it is failed.</summary>
    public int StartTimeoutMinutes { get; set; } = 10;

    /// <summary>How long a grab may stay <c>RemotelyQueued</c> before it is failed.</summary>
    public int RemoteQueueTimeoutMinutes { get; set; } = 30;

    /// <summary>How long a download may move no bytes before it is failed as stalled.</summary>
    public int StallTimeoutMinutes { get; set; } = 5;

    /// <summary>The longest the poll sleeps when every active item is waiting on a deferred retry.</summary>
    public int DeferredRetryMinutes { get; set; } = 15;
}

/// <summary>
/// Validates <see cref="QueueOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class QueueOptionsValidator : IValidateOptions<QueueOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, QueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Check(failures, "queue.active_poll_seconds", options.ActivePollSeconds, 2, 120);
        Check(failures, "queue.idle_poll_seconds", options.IdlePollSeconds, 10, 600);
        Check(failures, "queue.start_timeout_minutes", options.StartTimeoutMinutes, 1, 60);
        Check(failures, "queue.remote_queue_timeout_minutes", options.RemoteQueueTimeoutMinutes, 5, 240);
        Check(failures, "queue.stall_timeout_minutes", options.StallTimeoutMinutes, 1, 60);
        Check(failures, "queue.deferred_retry_minutes", options.DeferredRetryMinutes, 1, 240);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{key}: must be between {minimum} and {maximum} (was {value})"));
        }
    }
}