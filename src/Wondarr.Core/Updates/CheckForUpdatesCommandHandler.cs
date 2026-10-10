using Wondarr.Core.Jobs;

namespace Wondarr.Core.Updates;

/// <summary>
/// The scheduled <c>CheckForUpdates</c> task: asks GitHub for the latest release (see
/// <see cref="IUpdateCheckService"/>). Does nothing when <c>update.check_enabled</c> is off.
/// </summary>
public sealed class CheckForUpdatesCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/command</c> uses.</summary>
    public const string CommandName = "CheckForUpdates";

    /// <summary>How long after startup the first check runs, so the rest of the host is up first.</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

    private readonly IUpdateCheckService _updates;

    /// <summary>Initialises a new instance of the <see cref="CheckForUpdatesCommandHandler"/> class.</summary>
    /// <param name="updates">Asks GitHub.</param>
    public CheckForUpdatesCommandHandler(IUpdateCheckService updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        _updates = updates;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var status = await _updates.CheckAsync(manual: false, cancellationToken).ConfigureAwait(false);

        if (!status.CheckEnabled)
        {
            return "Update checks are off";
        }

        if (status.LastError is { } error)
        {
            return error;
        }

        if (status.UpdateAvailable)
        {
            return $"Wondarr {status.LatestVersion} is available (you have {status.CurrentVersion})";
        }

        return status.IsDevelopmentBuild
            ? $"Development build; the latest release is {status.LatestVersion ?? "unknown"}"
            : "Up to date";
    }
}
