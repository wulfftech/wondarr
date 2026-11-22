using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Persistence;

namespace Wondarr.Sources.YouTube;

/// <summary>What the yt-dlp update check last found, as it is kept across restarts.</summary>
/// <param name="LastCheckedAt">When the last check ran, whatever it found.</param>
/// <param name="LastCheckSucceeded">Whether the last check reached yt-dlp's updater and it finished.</param>
/// <param name="LastCheckMessage">What the last check found, in a sentence.</param>
/// <param name="LastSuccessAt">When a check last succeeded.</param>
/// <param name="LatestVersion">The newest release the last successful check saw.</param>
/// <param name="JsRuntimeWarning">yt-dlp's complaint about the JS runtime, when it made one.</param>
public sealed record StoredYtDlpUpdate(
    DateTimeOffset? LastCheckedAt,
    bool? LastCheckSucceeded,
    string? LastCheckMessage,
    DateTimeOffset? LastSuccessAt,
    string? LatestVersion,
    string? JsRuntimeWarning);

/// <summary>Keeps the yt-dlp update status across restarts, so the settings card and the health check do not start from nothing.</summary>
public interface IYtDlpUpdateStore
{
    /// <summary>Reads what was kept.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The kept status, or <see langword="null"/> when none was kept.</returns>
    Task<StoredYtDlpUpdate?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Keeps the status, replacing what was kept.</summary>
    /// <param name="status">The status to keep.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAsync(StoredYtDlpUpdate status, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the update status in the <c>setting</c> table, the way <c>UpdateCheckService</c> keeps the last announced
/// version. The binary is a singleton, so each call opens a scope of its own.
/// </summary>
public sealed class SettingsYtDlpUpdateStore : IYtDlpUpdateStore
{
    /// <summary>The <c>setting</c> key the status is kept under.</summary>
    public const string Key = "youtube.ytdlp_update_status";

    private readonly IServiceScopeFactory _scopes;

    /// <summary>Initialises a new instance of the <see cref="SettingsYtDlpUpdateStore"/> class.</summary>
    /// <param name="scopes">Opens the scope the settings repository lives in.</param>
    public SettingsYtDlpUpdateStore(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task<StoredYtDlpUpdate?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<ISettingsRepository>()
            .GetAsync<StoredYtDlpUpdate>(Key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredYtDlpUpdate status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);

        await using var scope = _scopes.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISettingsRepository>()
            .SetAsync(Key, status, cancellationToken)
            .ConfigureAwait(false);
    }
}
