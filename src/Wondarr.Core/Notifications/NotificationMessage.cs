namespace Wondarr.Core.Notifications;

/// <summary>The song a notification is about, as the providers need it.</summary>
/// <param name="Id">The song id.</param>
/// <param name="Title">The track title.</param>
/// <param name="ArtistCredit">The display credit, for example "Oasis".</param>
/// <param name="AlbumTitle">The album the song is filed under, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
public sealed record NotificationSong(
    long Id,
    string Title,
    string ArtistCredit,
    string? AlbumTitle,
    string? MbRecordingId);

/// <summary>The release a grab took, as the providers need it.</summary>
/// <param name="Title">What the source called the file.</param>
/// <param name="SourceType">The source type it came from, for example <c>soulseek</c>.</param>
/// <param name="Quality">The quality name it was judged to be.</param>
/// <param name="SizeBytes">The size the source reported, or <see langword="null"/>.</param>
public sealed record NotificationRelease(string Title, string SourceType, string Quality, long? SizeBytes);

/// <summary>The library file an import created.</summary>
/// <param name="Path">The absolute path of the file on disk.</param>
/// <param name="Quality">The quality name the file was matched to.</param>
public sealed record NotificationFile(string Path, string Quality);

/// <summary>One health check that started failing.</summary>
/// <param name="Source">The name of the check that produced the result.</param>
/// <param name="Level">The severity, lower-cased: <c>warning</c> or <c>error</c>.</param>
/// <param name="Message">The sentence the check produced.</param>
/// <param name="WikiUrl">The wiki page explaining it, or <see langword="null"/>.</param>
public sealed record NotificationHealth(string Source, string Level, string Message, string? WikiUrl);

/// <summary>
/// What a provider is asked to send: a one-line title, a short body, and the structured parts the
/// richer providers (the webhook) render.
/// </summary>
/// <param name="Event">One of the <see cref="NotificationEventNames"/> values.</param>
/// <param name="Title">A single line, for example "Grabbed: Oasis – Wonderwall".</param>
/// <param name="Body">One or two plain lines.</param>
public sealed record NotificationMessage(string Event, string Title, string Body)
{
    /// <summary>Gets the song the message is about, or <see langword="null"/>.</summary>
    public NotificationSong? Song { get; init; }

    /// <summary>Gets the release a grab took, or <see langword="null"/>.</summary>
    public NotificationRelease? Release { get; init; }

    /// <summary>Gets the library file an import created, or <see langword="null"/>.</summary>
    public NotificationFile? File { get; init; }

    /// <summary>Gets a value indicating whether the import replaced a file the song already had.</summary>
    public bool IsUpgrade { get; init; }

    /// <summary>Gets the health check the message is about, or <see langword="null"/>.</summary>
    public NotificationHealth? Health { get; init; }
}
