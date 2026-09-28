using System.Globalization;
using Compilarr.Core.Domain;
using Compilarr.Core.ImportLists;

namespace Compilarr.Api.ImportLists;

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
public sealed record ImportListCountsResource(int Pending, int Added, int Unresolved, int Skipped);

/// <summary>One import list, as the review screen's header shows it.</summary>
/// <param name="Id">The list's id.</param>
/// <param name="Type">The list type, <c>paste</c> in Phase 1.</param>
/// <param name="Name">The display name.</param>
/// <param name="Created">The UTC instant the list was stored.</param>
/// <param name="LastSyncedAt">The UTC instant it was last processed, or <see langword="null"/>.</param>
/// <param name="Counts">How many lines are in each state.</param>
public sealed record ImportListResource(
    long Id,
    string Type,
    string Name,
    DateTime Created,
    DateTime? LastSyncedAt,
    ImportListCountsResource Counts);

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
    IReadOnlyList<ImportListCandidate> Candidates);

/// <summary>Which candidate the user picked for an unresolved line.</summary>
/// <param name="MbRecordingId">The recording MBID, when the picked candidate is a MusicBrainz one.</param>
/// <param name="DeezerId">The Deezer track id, when the picked candidate is a Deezer one.</param>
public sealed record ImportListItemResolveResource(string? MbRecordingId, long? DeezerId);

/// <summary>Maps the import-list services' results onto the resources above.</summary>
public static class ImportListResourceExtensions
{
    /// <summary>Maps a list's summary.</summary>
    /// <param name="summary">The summary to map.</param>
    public static ImportListResource ToResource(this ImportListSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ImportListResource(
            summary.ImportListId,
            summary.Type,
            summary.Name,
            summary.Created,
            summary.LastSyncedAt,
            new ImportListCountsResource(summary.Pending, summary.Added, summary.Unresolved, summary.Skipped));
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
            LineNumber(item.ExternalId),
            line.Line,
            line.Artist,
            line.Title,
            item.State,
            item.Reason,
            item.SongId,
            ImportListItemJson.ReadCandidates(item));
    }

    /// <summary>The line number an item's external id carries; 0 when it is not a number.</summary>
    private static int LineNumber(string externalId) =>
        int.TryParse(externalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
}
