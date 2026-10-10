// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookPayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookGrabPayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookImportPayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookDownloadFailurePayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookHealthPayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookTrack.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookRelease.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookTrackFile.cs, GPL-3.0.
// Adapted for Wondarr: one flat payload record instead of a class per event (Lidarr's `Artist`/`Albums`/
// `Tracks` collapse to Wondarr's one song), `System.Text.Json` camelCase property names, and Lidarr's
// PascalCase `eventType` values kept so existing *arr webhook consumers match.

using System.Text.Json.Serialization;

namespace Wondarr.Core.Notifications.Webhook;

/// <summary>
/// The event name a webhook payload carries. The values are Lidarr's, deliberately PascalCase: an
/// existing *arr webhook consumer switches on <c>"Grab"</c>, not on <c>"grab"</c>.
/// </summary>
internal static class WebhookEventTypes
{
    public const string Test = "Test";
    public const string Grab = "Grab";

    /// <summary>Sent for both a first import and an upgrade; <c>isUpgrade</c> tells the two apart.</summary>
    public const string Download = "Download";
    public const string DownloadFailure = "DownloadFailure";
    public const string Health = "Health";

    /// <summary>A newer Wondarr release exists. Lidarr has no equivalent, so the name is Wondarr's own.</summary>
    public const string Update = "Update";
}

/// <summary>
/// The body of one webhook. Property names are camelCase; only the fields the event has are written,
/// except <c>applicationUrl</c>, which is always present (and null until Wondarr knows its own URL)
/// because consumers read it unconditionally.
/// </summary>
/// <param name="EventType">One of <see cref="WebhookEventTypes"/>.</param>
/// <param name="InstanceName">Always <c>Wondarr</c>.</param>
internal sealed record WebhookPayload(string EventType, string InstanceName)
{
    /// <summary>The instance's own URL, or <see langword="null"/> while there is none to report.</summary>
    public string? ApplicationUrl { get; init; }

    /// <summary>The song the event is about.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookSong? Song { get; init; }

    /// <summary>What a grab took.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookRelease? Release { get; init; }

    /// <summary>The library file an import created.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookTrackFile? TrackFile { get; init; }

    /// <summary>Whether the import replaced a file the song already had.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsUpgrade { get; init; }

    /// <summary>A health result's severity, <c>warning</c> or <c>error</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; init; }

    /// <summary>The sentence the event carries: the health message, or the failure's reason.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>A health result's source, the name of the check that produced it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>The wiki page explaining a health result, or <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikiUrl { get; init; }

    /// <summary>The newer release an <c>Update</c> event is about.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookUpdate? Update { get; init; }
}

/// <summary>The newer release an <c>Update</c> webhook is about.</summary>
/// <param name="CurrentVersion">The version that is running.</param>
/// <param name="LatestVersion">The newer version.</param>
/// <param name="ReleaseUrl">The release's page on GitHub, or <see langword="null"/>.</param>
internal sealed record WebhookUpdate(string CurrentVersion, string LatestVersion, string? ReleaseUrl);

/// <summary>Wondarr's song object (Lidarr's <c>WebhookTrack</c>, narrowed to what a song is here).</summary>
/// <param name="Id">The song id.</param>
/// <param name="Title">The track title.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="AlbumTitle">The album the song is filed under, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
internal sealed record WebhookSong(
    long Id,
    string Title,
    string ArtistCredit,
    string? AlbumTitle,
    string? MbRecordingId);

/// <summary>What a grab took (Lidarr's <c>WebhookRelease</c>, narrowed to a single file).</summary>
/// <param name="Title">What the source called the file.</param>
/// <param name="SourceType">The source type it came from.</param>
/// <param name="Quality">The quality name it was judged to be.</param>
/// <param name="Size">The size the source reported, or <see langword="null"/>.</param>
internal sealed record WebhookRelease(string Title, string SourceType, string Quality, long? Size);

/// <summary>The library file an import created (Lidarr's <c>WebhookTrackFile</c>, narrowed).</summary>
/// <param name="Path">The absolute path of the file on disk.</param>
/// <param name="Quality">The quality name the file was matched to.</param>
internal sealed record WebhookTrackFile(string Path, string Quality);
