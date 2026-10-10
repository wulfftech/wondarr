using Wondarr.Core.Updates;

namespace Wondarr.Api.Updates;

/// <summary>What Wondarr knows about newer releases.</summary>
/// <param name="CurrentVersion">The running version, without the commit suffix.</param>
/// <param name="IsDevelopmentBuild">Whether this is a development build, which never reports an update.</param>
/// <param name="LatestVersion">The newest release that counts for this build, or <see langword="null"/> before the first successful check.</param>
/// <param name="UpdateAvailable">Whether <paramref name="LatestVersion"/> is newer than the running version.</param>
/// <param name="ReleaseName">The release's title.</param>
/// <param name="ReleaseUrl">The release's page on GitHub.</param>
/// <param name="ReleaseNotes">The release's notes, as Markdown.</param>
/// <param name="PublishedAt">When the release was published.</param>
/// <param name="CheckedAt">When GitHub last answered.</param>
/// <param name="LastError">A sentence about why the last check failed, or <see langword="null"/>.</param>
/// <param name="CheckEnabled">Whether update checks are on.</param>
public sealed record UpdateResource(
    string CurrentVersion,
    bool IsDevelopmentBuild,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseName,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? CheckedAt,
    string? LastError,
    bool CheckEnabled)
{
    /// <summary>Maps the service's status onto the resource.</summary>
    /// <param name="status">The status to map.</param>
    public static UpdateResource From(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new UpdateResource(
            status.CurrentVersion,
            status.IsDevelopmentBuild,
            status.LatestVersion,
            status.UpdateAvailable,
            status.ReleaseName,
            status.ReleaseUrl,
            status.ReleaseNotes,
            status.PublishedAt,
            status.CheckedAt,
            status.LastError,
            status.CheckEnabled);
    }
}

/// <summary>The Updates card of Settings &gt; General.</summary>
/// <param name="CheckEnabled">Whether Wondarr asks GitHub for new releases.</param>
/// <param name="CheckEnabledLocked">Whether the environment sets it (<c>APP__UPDATE__CHECK_ENABLED</c>), so it cannot be changed here.</param>
public sealed record UpdateSettingsResource(bool CheckEnabled, bool CheckEnabledLocked)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    /// <param name="settings">The settings to map.</param>
    public static UpdateSettingsResource From(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new UpdateSettingsResource(settings.CheckEnabled, settings.CheckEnabledLocked);
    }
}

/// <summary>A change to the update settings. An absent field is left alone.</summary>
/// <param name="CheckEnabled">Whether to check GitHub for new releases.</param>
public sealed record UpdateSettingsUpdateResource(bool? CheckEnabled = null);
