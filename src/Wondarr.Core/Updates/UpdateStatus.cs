using Wondarr.Core.Messaging;

namespace Wondarr.Core.Updates;

/// <summary>What Wondarr knows about newer releases, as the UI shows it.</summary>
/// <param name="CurrentVersion">The running version, without the commit suffix.</param>
/// <param name="IsDevelopmentBuild">Whether this is a development build, which never reports an update.</param>
/// <param name="LatestVersion">The newest release that counts for this build, or <see langword="null"/> before the first check.</param>
/// <param name="UpdateAvailable">Whether <paramref name="LatestVersion"/> is newer than the running version.</param>
/// <param name="ReleaseName">The release's title.</param>
/// <param name="ReleaseUrl">The release's page on GitHub.</param>
/// <param name="ReleaseNotes">The release's notes as Markdown, capped at <see cref="UpdateCheckService.MaxReleaseNotesLength"/> characters.</param>
/// <param name="PublishedAt">When the release was published.</param>
/// <param name="CheckedAt">When GitHub last answered.</param>
/// <param name="LastError">A sentence about why the last check failed, or <see langword="null"/>.</param>
/// <param name="CheckEnabled">Whether update checks are on.</param>
public sealed record UpdateStatus(
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
    bool CheckEnabled);

/// <summary>Published when a check finished and the status may have changed; the UI refetches it.</summary>
/// <param name="Status">The status after the check.</param>
public sealed record UpdateCheckedEvent(UpdateStatus Status) : IEvent;

/// <summary>
/// Published once per new version that is available (never for a development build); notifications
/// send it.
/// </summary>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newer version.</param>
/// <param name="ReleaseUrl">The release's page on GitHub.</param>
public sealed record UpdateAvailableEvent(string CurrentVersion, string LatestVersion, string? ReleaseUrl) : IEvent;
