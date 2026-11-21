using Wondarr.Core.Jobs;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// The scheduled <c>UpdateYtDlp</c> task: keeps Wondarr's own copy of yt-dlp current (see <see cref="IYtDlpBinary"/>).
/// It does nothing, and makes no network call, when automatic updates are off, when neither the YouTube source nor
/// music videos is on, or when the yt-dlp path is a custom one. A failure is a Warning in the log and a message on the
/// task: the binary already in use stays, and the next run tries again.
/// </summary>
public sealed class UpdateYtDlpCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/command</c> uses.</summary>
    public const string CommandName = "UpdateYtDlp";

    /// <summary>How long after startup the first run comes, so the rest of the host is up first.</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);

    /// <summary>How often the update runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IYtDlpBinary _binary;

    /// <summary>Initialises a new instance of the <see cref="UpdateYtDlpCommandHandler"/> class.</summary>
    /// <param name="binary">Copies and updates yt-dlp.</param>
    public UpdateYtDlpCommandHandler(IYtDlpBinary binary)
    {
        ArgumentNullException.ThrowIfNull(binary);

        _binary = binary;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var run = await _binary.UpdateAsync(cancellationToken).ConfigureAwait(false);

        return run.Message;
    }
}

/// <summary>Puts <c>UpdateYtDlp</c> on the scheduler, once a day.</summary>
public sealed class YtDlpScheduledTasks : IScheduledTaskSource
{
    /// <inheritdoc />
    // Always listed, so it shows on the Tasks page with Run now; the handler itself skips a run that has nothing to do.
    public IReadOnlyList<ScheduledTaskDefinition> Tasks =>
    [
        new(UpdateYtDlpCommandHandler.CommandName, UpdateYtDlpCommandHandler.Interval, UpdateYtDlpCommandHandler.FirstRunDelay),
    ];

    /// <inheritdoc />
    public IDisposable OnChange(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        // The schedule does not depend on any setting.
        return NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
