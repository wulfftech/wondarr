using Wondarr.Core.HealthCheck;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Proves the folder slskd downloads into can be written to. slskd refuses to start when a
/// configured directory is missing, so an unwritable <c>/data</c> is a Soulseek outage.
/// </summary>
public sealed class SlskdDownloadFolderHealthCheck : IHealthCheck
{
    /// <summary>Name this check reports its results under.</summary>
    public const string CheckName = "slskd-download-folder";

    /// <summary>How long the write probe gets before the folder counts as not answering.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<SoulseekOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="SlskdDownloadFolderHealthCheck"/> class.</summary>
    public SlskdDownloadFolderHealthCheck(IOptionsMonitor<SoulseekOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (options.Mode == SoulseekMode.External)
        {
            return Result(HealthCheckResult.Ok, SoulseekHealthMessages.ExternalMode);
        }

        var folder = options.DownloadsDir;

        // Missing is its own answer: this check reports the state of the folder, it does not create
        // it. Making one here would hide a configuration mistake the host is about to report too.
        if (!Directory.Exists(folder))
        {
            return Result(HealthCheckResult.Error, $"{folder} does not exist");
        }

        // The probe file is named for this check, so a leftover one is traceable to it.
        var probeFile = Path.Combine(folder, $".wondarr-write-test-{Guid.NewGuid():N}");

        try
        {
            // Off the request thread and bounded: a wedged network mount answers neither way, and a
            // health check that hangs is worse than one that reports a timeout.
            await Task
                .Run(() => File.WriteAllText(probeFile, string.Empty), cancellationToken)
                .WaitAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            return Result(HealthCheckResult.Ok, $"{folder} is writable");
        }
        catch (TimeoutException)
        {
            return Result(
                HealthCheckResult.Error,
                $"{folder} did not answer within {ProbeTimeout.TotalSeconds:0} s");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(HealthCheckResult.Error, $"{folder} is not writable: {exception.Message}");
        }
        finally
        {
            TryDeleteProbeFile(probeFile);
        }
    }

    private static void TryDeleteProbeFile(string probeFile)
    {
        try
        {
            File.Delete(probeFile);
        }
        catch (IOException)
        {
            // A leftover probe file is not worth failing the check over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the result already says whether the folder is writable.
        }
    }

    private static HealthCheck Result(HealthCheckResult type, string message) =>
        new(CheckName, type, message, null);
}

/// <summary>
/// Says whether this instance shares anything. Soulseek is give-and-take: peers ban clients that
/// share nothing, so sharing being off is worth telling the user about before it bites.
/// </summary>
public sealed class SoulseekSharingHealthCheck : IHealthCheck
{
    /// <summary>Name this check reports its results under.</summary>
    public const string CheckName = "soulseek-sharing";

    private readonly IOptionsMonitor<SoulseekOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="SoulseekSharingHealthCheck"/> class.</summary>
    public SoulseekSharingHealthCheck(IOptionsMonitor<SoulseekOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    /// <remarks>
    /// Only whether folders exist is checked here; how many files they hold is slskd's own state
    /// (<c>GET /api/v0/application</c>'s <c>shares</c>).
    /// </remarks>
    public Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (options.Mode == SoulseekMode.External)
        {
            return Task.FromResult(Result(HealthCheckResult.Ok, SoulseekHealthMessages.ExternalMode));
        }

        if (!options.ShareLibrary)
        {
            return Task.FromResult(Result(
                HealthCheckResult.Warning,
                "Sharing is off: Soulseek users often ban peers who share nothing"));
        }

        var folders = options.SharedFolders ?? [];
        var existing = folders.Where(Directory.Exists).ToList();

        if (existing.Count == 0)
        {
            return Task.FromResult(Result(
                HealthCheckResult.Warning,
                $"None of the shared folders exist: {string.Join(", ", folders)}"));
        }

        return Task.FromResult(Result(HealthCheckResult.Ok, $"Sharing {existing.Count} folder(s)"));
    }

    private static HealthCheck Result(HealthCheckResult type, string message) =>
        new(CheckName, type, message, null);
}

/// <summary>Messages both Soulseek storage checks share.</summary>
internal static class SoulseekHealthMessages
{
    /// <summary>What a check says when Wondarr owns no process to check.</summary>
    public const string ExternalMode = "External slskd mode (checked in Phase 5)";
}