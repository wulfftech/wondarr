using Wondarr.Sources.Slskd;

namespace Wondarr.Api.Soulseek;

/// <summary>
/// The Soulseek settings as the settings page reads them. The password is never part of this
/// resource; <see cref="PasswordSet"/> says whether one is stored.
/// </summary>
/// <param name="Username">The Soulseek account name, if one is configured.</param>
/// <param name="PasswordSet">Whether a password is stored.</param>
/// <param name="ListenPort">Port slskd listens on for peer connections.</param>
/// <param name="ShareLibrary">The "share my library" toggle.</param>
/// <param name="SharedFolders">Folders shared back to the network.</param>
/// <param name="UploadSlots">Concurrent upload slots.</param>
/// <param name="UploadSpeedLimitKib">Upload speed limit in KiB/s; 0 means unlimited.</param>
/// <param name="DistributedNetwork">Whether slskd joins the distributed network.</param>
/// <param name="DownloadsDir">Directory slskd downloads into.</param>
/// <param name="IncompleteDir">Directory slskd keeps partial transfers in.</param>
/// <param name="ReadOnlyFields">
/// camelCase names of the fields the environment sets through <c>APP__SOULSEEK__…</c>; the UI shows
/// them as read-only because a change written to <c>config.yml</c> would be overridden.
/// </param>
public sealed record SoulseekSettingsResource(
    string? Username,
    bool PasswordSet,
    int ListenPort,
    bool ShareLibrary,
    List<string> SharedFolders,
    int UploadSlots,
    int UploadSpeedLimitKib,
    bool DistributedNetwork,
    string DownloadsDir,
    string IncompleteDir,
    List<string> ReadOnlyFields)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    public static SoulseekSettingsResource From(SoulseekSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new SoulseekSettingsResource(
            settings.Username,
            settings.PasswordSet,
            settings.ListenPort,
            settings.ShareLibrary,
            [.. settings.SharedFolders],
            settings.UploadSlots,
            settings.UploadSpeedLimitKib,
            settings.DistributedNetwork,
            settings.DownloadsDir,
            settings.IncompleteDir,
            [.. settings.ReadOnlyFields]);
    }
}

/// <summary>
/// A change to the Soulseek settings. Every field is optional and an absent field is left alone;
/// <see cref="Password"/> is <see langword="null"/> to keep the stored one and an empty string to
/// clear it.
/// </summary>
/// <param name="Username">New account name.</param>
/// <param name="Password">New password; never returned by the API.</param>
/// <param name="ListenPort">New peer-connection port.</param>
/// <param name="ShareLibrary">New state of the "share my library" toggle.</param>
/// <param name="SharedFolders">New shared folders; an empty list shares nothing.</param>
/// <param name="UploadSlots">New upload slot count.</param>
/// <param name="UploadSpeedLimitKib">New upload speed limit.</param>
/// <param name="DistributedNetwork">New distributed-network state.</param>
/// <param name="DownloadsDir">New download directory.</param>
/// <param name="IncompleteDir">New incomplete-transfer directory.</param>
public sealed record SoulseekSettingsUpdateResource(
    string? Username = null,
    string? Password = null,
    int? ListenPort = null,
    bool? ShareLibrary = null,
    List<string>? SharedFolders = null,
    int? UploadSlots = null,
    int? UploadSpeedLimitKib = null,
    bool? DistributedNetwork = null,
    string? DownloadsDir = null,
    string? IncompleteDir = null)
{
    /// <summary>Maps the request body onto the service's update.</summary>
    public SoulseekSettingsUpdate ToUpdate() => new(
        Username,
        Password,
        ListenPort,
        ShareLibrary,
        SharedFolders is null ? null : [.. SharedFolders],
        UploadSlots,
        UploadSpeedLimitKib,
        DistributedNetwork,
        DownloadsDir,
        IncompleteDir);
}

/// <summary>What a successful settings change returns: the stored settings and whether slskd restarts.</summary>
/// <param name="Settings">The settings as they are now stored.</param>
/// <param name="RestartsSlskd">Whether the change is one the supervisor restarts slskd for.</param>
public sealed record SoulseekSettingsUpdateResponseResource(SoulseekSettingsResource Settings, bool RestartsSlskd);

/// <summary>What Soulseek is sharing, as slskd last reported it.</summary>
/// <param name="Enabled">The "share my library" toggle.</param>
/// <param name="Folders">Folders the settings say to share.</param>
/// <param name="Directories">Directories slskd has scanned, or <see langword="null"/> if it has not answered.</param>
/// <param name="Files">Files slskd has scanned, or <see langword="null"/> if it has not answered.</param>
public sealed record SoulseekSharingResource(bool Enabled, List<string> Folders, int? Directories, int? Files);

/// <summary>How much of the Soulseek search allowance is left.</summary>
/// <param name="SubmittedInWindow">Searches submitted inside the current window.</param>
/// <param name="MaxSearches">Searches allowed inside the window.</param>
/// <param name="Outstanding">Searches in flight.</param>
/// <param name="MaxOutstanding">Searches allowed in flight at once.</param>
/// <param name="NextAllowedAt">When the next search may be submitted, or null if it may go now.</param>
public sealed record SoulseekSearchBudgetResource(
    int SubmittedInWindow,
    int MaxSearches,
    int Outstanding,
    int MaxOutstanding,
    DateTimeOffset? NextAllowedAt);

/// <summary>Everything the Soulseek page needs to show whether the source is usable.</summary>
/// <param name="Mode">How Wondarr searches: <c>bundled</c> or <c>external</c>.</param>
/// <param name="State">The supervisor's view of the bundled slskd, camelCase.</param>
/// <param name="Version">The version slskd reports, once it has answered.</param>
/// <param name="LoggedIn">Whether the Soulseek account is logged in.</param>
/// <param name="Username">The username slskd reports being logged in as.</param>
/// <param name="LoginProblem">A login problem slskd's log reported, camelCase, or null.</param>
/// <param name="LastError">What went wrong, if anything.</param>
/// <param name="PendingRestart">Whether slskd is waiting to be restarted.</param>
/// <param name="Sharing">What is shared.</param>
/// <param name="SearchBudget">The search allowance.</param>
public sealed record SoulseekStatusResource(
    string Mode,
    string State,
    string? Version,
    bool LoggedIn,
    string? Username,
    string? LoginProblem,
    string? LastError,
    bool PendingRestart,
    SoulseekSharingResource Sharing,
    SoulseekSearchBudgetResource SearchBudget);
