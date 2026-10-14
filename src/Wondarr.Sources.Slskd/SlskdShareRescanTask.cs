using Microsoft.Extensions.Options;
using Wondarr.Core.Jobs;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// The scheduled <c>RescanSoulseekShares</c> task ("Rescan Soulseek Shares" in the task list): asks slskd
/// to rescan its shares once a day (<c>soulseek.share_rescan_interval_hours</c>). The rescan after an
/// import covers files Wondarr put there; this covers files that arrive any other way (a reference
/// library, the user's own copies). It does nothing, and says so, when sharing is off or slskd cannot be asked.
/// </summary>
public sealed class RescanSoulseekSharesCommandHandler : ICommandHandler
{
    /// <summary>The name <c>POST /api/v1/command</c> and the task list use.</summary>
    public const string CommandName = "RescanSoulseekShares";

    private readonly SlskdShareRescanner _rescanner;

    /// <summary>Initialises a new instance of the <see cref="RescanSoulseekSharesCommandHandler"/> class.</summary>
    /// <param name="rescanner">Makes the rescan request, the same one an import makes.</param>
    public RescanSoulseekSharesCommandHandler(SlskdShareRescanner rescanner)
    {
        ArgumentNullException.ThrowIfNull(rescanner);

        _rescanner = rescanner;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        await _rescanner.RescanNowAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Puts the share rescan on the scheduler. The interval is read from the live Soulseek settings, so a
/// change moves the schedule without a restart.
/// </summary>
public sealed class SlskdScheduledTasks : IScheduledTaskSource
{
    private readonly IOptionsMonitor<SoulseekOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="SlskdScheduledTasks"/> class.</summary>
    /// <param name="options">The Soulseek settings, which decide how often the rescan runs.</param>
    public SlskdScheduledTasks(IOptionsMonitor<SoulseekOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDefinition> Tasks =>
    [
        new(
            RescanSoulseekSharesCommandHandler.CommandName,
            TimeSpan.FromHours(_options.CurrentValue.ShareRescanIntervalHours)),
    ];

    /// <inheritdoc />
    public IDisposable OnChange(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return _options.OnChange(_ => listener()) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
