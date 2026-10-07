using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists;

/// <summary>
/// One kind of synced import list (ADR-0012): a CSV export, a Deezer or YouTube Music playlist, a
/// scrobble list. A provider only reads its source; resolving, adding and the sync policies are the
/// sync service's. Implementations are registered as singletons and are stateless: everything a read
/// needs arrives in the list.
/// </summary>
public interface IImportListProvider
{
    /// <summary>Gets the type stored in <see cref="ImportList.Type"/>, for example <c>csv</c>.</summary>
    string Type { get; }

    /// <summary>Gets the name the UI shows for the type, for example <c>CSV file</c>.</summary>
    string DisplayName { get; }

    /// <summary>Gets the fields of the settings form the UI renders (the notification forms' shape).</summary>
    IReadOnlyList<NotificationField> Fields { get; }

    /// <summary>
    /// Checks a list's settings (and, for an uploaded file, its <see cref="ImportList.SourceText"/>).
    /// Returns the human messages to show the user, empty when the list is usable.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    /// <param name="sourceText">The uploaded content, or <see langword="null"/> for a provider that reads a service.</param>
    IReadOnlyList<string> Validate(JsonElement settings, string? sourceText);

    /// <summary>Reads the list's current items, in the source's order.</summary>
    /// <param name="list">The list, with its settings and uploaded content.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The items, or why the source could not be read.</returns>
    Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken);
}

/// <summary>
/// One item of a source, as the provider read it. Every field but <see cref="ExternalId"/> is optional;
/// the sync resolves the item by the strongest id it carries (DECISIONS build session 7 #7).
/// </summary>
/// <param name="ExternalId">The source's own id for the item, stable across syncs (a Spotify track URI, a Deezer track id).</param>
/// <param name="Artist">The artist credit as the source writes it, or <see langword="null"/>.</param>
/// <param name="Title">The track title, or <see langword="null"/>.</param>
/// <param name="Album">The album title, or <see langword="null"/>.</param>
/// <param name="DurationMs">The length in milliseconds, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id, or <see langword="null"/>.</param>
public sealed record ImportListEntry(
    string ExternalId,
    string? Artist,
    string? Title,
    string? Album = null,
    int? DurationMs = null,
    string? Isrc = null,
    string? MbRecordingId = null,
    long? DeezerId = null)
{
    /// <summary>Gets the line the review screen quotes: <c>Artist - Title</c>, or whichever half is known.</summary>
    public string Line => (Artist, Title) switch
    {
        ({ Length: > 0 } artist, { Length: > 0 } title) => $"{artist} - {title}",
        (_, { Length: > 0 } title) => title,
        ({ Length: > 0 } artist, _) => artist,
        _ => Isrc ?? MbRecordingId ?? ExternalId,
    };
}

/// <summary>What one read of a source produced.</summary>
/// <param name="Entries">The items in the source's order; empty when the read failed.</param>
/// <param name="Error">Why the source could not be read, or <see langword="null"/> on success.</param>
public sealed record ImportListFetchResult(IReadOnlyList<ImportListEntry> Entries, string? Error = null)
{
    /// <summary>Gets a value indicating whether the source was read.</summary>
    public bool Success => Error is null;

    /// <summary>A failed read.</summary>
    /// <param name="error">Why the source could not be read.</param>
    public static ImportListFetchResult Failed(string error) => new([], error);
}
