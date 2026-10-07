using System.Globalization;
using System.Text.Json.Nodes;
using Wondarr.Api.Notifications;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Notifications;

namespace Wondarr.Api.ImportLists;

/// <summary>What a caller bulk-adds: the pasted text, and where its songs land.</summary>
/// <param name="Text">The pasted lines: one <c>Artist - Title</c>, id or link per line.</param>
/// <param name="QualityProfileId">The profile the list's songs are monitored against, or <see langword="null"/> for the default.</param>
/// <param name="LibraryId">The library the list's songs are filed in, or <see langword="null"/> for the default library.</param>
public sealed record BulkAddResource(string? Text, long? QualityProfileId, long? LibraryId);

/// <summary>
/// What a bulk add answered with: the stored list, the command that will process it, and how many
/// lines it holds. The caller polls <c>GET /api/v1/command/{commandId}</c> for progress.
/// </summary>
/// <param name="ImportListId">The stored list.</param>
/// <param name="CommandId">The queued <c>BulkAddSongs</c> command.</param>
/// <param name="LineCount">How many lines the list holds.</param>
public sealed record BulkAddAcceptedResource(long ImportListId, long CommandId, int LineCount);

/// <summary>How many lines of one list are in each state.</summary>
/// <param name="Pending">Not looked up yet.</param>
/// <param name="Added">Became songs, including ones the library already held.</param>
/// <param name="Unresolved">Neither provider could identify them.</param>
/// <param name="Skipped">Deliberately not added.</param>
/// <param name="Removed">Gone from the list's source at the last sync (counted here, not in the other states).</param>
public sealed record ImportListCountsResource(int Pending, int Added, int Unresolved, int Skipped, int Removed = 0);

/// <summary>One import list, as the review screen's header shows it.</summary>
/// <param name="Id">The list's id.</param>
/// <param name="Type">The list type, <c>paste</c> in Phase 1.</param>
/// <param name="Name">The display name.</param>
/// <param name="Created">The UTC instant the list was stored.</param>
/// <param name="LastSyncedAt">The UTC instant it was last processed, or <see langword="null"/>.</param>
/// <param name="Counts">How many lines are in each state.</param>
/// <param name="Enabled">Whether the scheduled sync reads the list.</param>
/// <param name="SyncIntervalHours">Hours between scheduled syncs; 0 = only when asked.</param>
/// <param name="Policy">What a sync does with items that left the source.</param>
/// <param name="QualityProfileId">The profile new songs are monitored against.</param>
/// <param name="LibraryId">The library new songs are filed in.</param>
/// <param name="LastSyncMessage">The last sync's one-line result, or why it failed.</param>
/// <param name="Settings">The provider's settings, secrets masked.</param>
/// <param name="HasFile">Whether an uploaded file is stored (a CSV list).</param>
public sealed record ImportListResource(
    long Id,
    string Type,
    string Name,
    DateTime Created,
    DateTime? LastSyncedAt,
    ImportListCountsResource Counts,
    bool Enabled = false,
    int SyncIntervalHours = 0,
    string Policy = ImportList.AddOnlyPolicy,
    long QualityProfileId = 0,
    long LibraryId = 0,
    string? LastSyncMessage = null,
    JsonNode? Settings = null,
    bool HasFile = false);

/// <summary>A synced import list as the caller creates or changes it.</summary>
/// <param name="Type">The provider type, for example <c>csv</c> (see <c>GET /api/v1/importlist/schema</c>).</param>
/// <param name="Name">The display name.</param>
/// <param name="Settings">The provider's settings; a secret sent back masked keeps its stored value.</param>
/// <param name="SourceText">An uploaded file's text (a CSV), or <see langword="null"/> to keep the stored one.</param>
/// <param name="Policy"><c>AddOnly</c> (default), <c>AddAndUnmonitor</c> or <c>Mirror</c>.</param>
/// <param name="QualityProfileId">The profile new songs are monitored against, or <see langword="null"/> for the default.</param>
/// <param name="LibraryId">The library new songs are filed in, or <see langword="null"/> for the default.</param>
/// <param name="Enabled">Whether the scheduled sync reads the list (default true).</param>
/// <param name="SyncIntervalHours">Hours between scheduled syncs, 0–720; 0 = only when asked (default 24).</param>
public sealed record ImportListInputResource(
    string? Type,
    string? Name,
    JsonNode? Settings,
    string? SourceText,
    string? Policy,
    long? QualityProfileId,
    long? LibraryId,
    bool? Enabled,
    int? SyncIntervalHours);

/// <summary>One provider the caller can create a list of, and its settings form.</summary>
/// <param name="Type">The type to send when creating a list.</param>
/// <param name="DisplayName">What the UI calls it.</param>
/// <param name="Fields">The settings form.</param>
public sealed record ImportListSchemaResource(
    string Type,
    string DisplayName,
    IReadOnlyList<NotificationFieldResource> Fields);

/// <summary>A CSV file to preview before it becomes a list.</summary>
/// <param name="SourceText">The file's text.</param>
/// <param name="Settings">The column mapping (the CSV provider's settings), or <see langword="null"/> to detect it.</param>
public sealed record CsvPreviewInputResource(string? SourceText, JsonNode? Settings);

/// <summary>One item of a source, as a provider read it.</summary>
/// <param name="ExternalId">The source's id for the item.</param>
/// <param name="Artist">The artist, or <see langword="null"/>.</param>
/// <param name="Title">The title, or <see langword="null"/>.</param>
/// <param name="Album">The album, or <see langword="null"/>.</param>
/// <param name="DurationMs">The length in milliseconds, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
public sealed record ImportListEntryResource(
    string ExternalId,
    string? Artist,
    string? Title,
    string? Album,
    int? DurationMs,
    string? Isrc,
    string? MbRecordingId);

/// <summary>What a CSV file looks like to the CSV provider.</summary>
/// <param name="Format"><c>exportify</c> or <c>generic</c>.</param>
/// <param name="Headers">The header row.</param>
/// <param name="RowCount">How many rows would become items.</param>
/// <param name="Sample">The first rows, as items.</param>
/// <param name="Problems">Why the file cannot be imported as mapped; empty when it can.</param>
public sealed record CsvPreviewResource(
    string Format,
    IReadOnlyList<string> Headers,
    int RowCount,
    IReadOnlyList<ImportListEntryResource> Sample,
    IReadOnlyList<string> Problems);

/// <summary>A queued sync.</summary>
/// <param name="CommandId">The <c>ImportListSync</c> command; poll <c>GET /api/v1/command/{commandId}</c>.</param>
public sealed record ImportListSyncAcceptedResource(long CommandId);

/// <summary>
/// One line of an import list, with the candidates an unresolved line can be resolved to. Deleting
/// the song a line became leaves the line here with no <c>SongId</c>.
/// </summary>
/// <param name="Id">The item's id.</param>
/// <param name="ImportListId">The list the line belongs to.</param>
/// <param name="Line">The line number in the pasted text, starting at 1.</param>
/// <param name="Text">The line as pasted.</param>
/// <param name="Artist">The artist a lookup parsed out of the line, or <see langword="null"/>.</param>
/// <param name="Title">The title a lookup parsed out of the line, or <see langword="null"/>.</param>
/// <param name="State">What became of the line.</param>
/// <param name="Reason">Why the line ended up in its state, or <see langword="null"/>.</param>
/// <param name="SongId">The song the line became, or <see langword="null"/>.</param>
/// <param name="Candidates">The best candidates, for a line that is unresolved.</param>
/// <param name="Removed">Whether the item was gone from the list's source at the last sync.</param>
public sealed record ImportListItemResource(
    long Id,
    long ImportListId,
    int Line,
    string Text,
    string? Artist,
    string? Title,
    ImportListItemState State,
    string? Reason,
    long? SongId,
    IReadOnlyList<ImportListCandidate> Candidates,
    bool Removed = false);

/// <summary>Which candidate the user picked for an unresolved line.</summary>
/// <param name="MbRecordingId">The recording MBID, when the picked candidate is a MusicBrainz one.</param>
/// <param name="DeezerId">The Deezer track id, when the picked candidate is a Deezer one.</param>
public sealed record ImportListItemResolveResource(string? MbRecordingId, long? DeezerId);

/// <summary>Maps the import-list services' results onto the resources above.</summary>
public static class ImportListResourceExtensions
{
    /// <summary>Maps a list with its counts; the provider's secret settings are masked.</summary>
    /// <param name="view">The list and its counts.</param>
    /// <param name="provider">The list's provider, or <see langword="null"/> for a pasted list.</param>
    public static ImportListResource ToResource(this ImportListView view, IImportListProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(view);

        var list = view.List;
        var settings = NotificationSecrets.Read(list.Settings);

        return new ImportListResource(
            list.Id,
            list.Type,
            list.Name,
            list.CreatedAt,
            list.LastSyncedAt,
            new ImportListCountsResource(view.Pending, view.Added, view.Unresolved, view.Skipped, view.Removed),
            list.Enabled,
            list.SyncIntervalHours,
            list.Policy,
            list.QualityProfileId,
            list.LibraryId,
            list.LastSyncMessage,
            provider is null ? JsonNode.Parse(settings.GetRawText()) : NotificationSecrets.Masked(settings, provider.Fields),
            !string.IsNullOrEmpty(list.SourceText));
    }

    /// <summary>Maps one entry a provider read.</summary>
    /// <param name="entry">The entry.</param>
    public static ImportListEntryResource ToResource(this ImportListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ImportListEntryResource(
            entry.ExternalId,
            entry.Artist,
            entry.Title,
            entry.Album,
            entry.DurationMs,
            entry.Isrc,
            entry.MbRecordingId);
    }

    /// <summary>Maps one line, reading its text and candidates out of the stored JSON columns.</summary>
    /// <param name="item">The item to map.</param>
    public static ImportListItemResource ToResource(this ImportListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var line = ImportListItemJson.ReadLine(item);

        return new ImportListItemResource(
            item.Id,
            item.ImportListId,
            LineNumber(item),
            line.Line,
            line.Artist,
            line.Title,
            item.State,
            item.Reason,
            item.SongId,
            ImportListItemJson.ReadCandidates(item),
            item.RemovedAt is not null);
    }

    /// <summary>
    /// The line number of an item: a pasted line's external id is its number; a synced item's is the
    /// source's own id, so its place in the source is used instead.
    /// </summary>
    private static int LineNumber(ImportListItem item) =>
        int.TryParse(item.ExternalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : item.Position + 1;
}
