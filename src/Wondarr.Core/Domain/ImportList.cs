using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One import list: a named source of songs Wondarr resolves and adds. Phase 1 only knows the
/// <c>paste</c> type — a block of text the user pasted — but the shape is the *arr one so the CSV
/// and Spotify exports of Phase 6 are the same table with a different <see cref="Type"/>.
/// </summary>
public sealed class ImportList : EntityBase
{
    /// <summary>The <see cref="Type"/> of a pasted list: the only one Phase 1 writes.</summary>
    public const string PasteType = "paste";

    /// <summary>The <see cref="Policy"/> of a list that only ever adds, never removes.</summary>
    public const string AddOnlyPolicy = "AddOnly";

    /// <summary>Gets or sets the list type. Phase 1 only ever writes <c>paste</c>.</summary>
    public string Type { get; set; } = PasteType;

    /// <summary>Gets or sets the display name, for example <c>Pasted list 2026-09-28 10:31 UTC</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type-specific settings, as a JSON object.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Gets or sets what a sync does with items that are no longer in the source.</summary>
    public string Policy { get; set; } = AddOnlyPolicy;

    /// <summary>Gets or sets the quality profile every song from this list is monitored against.</summary>
    public long QualityProfileId { get; set; }

    /// <summary>Gets or sets the library every song from this list is filed in.</summary>
    public long LibraryId { get; set; }

    /// <summary>Gets or sets the UTC instant the list was last processed, or <see langword="null"/>.</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Gets or sets the lines of the list, in the order they were pasted.</summary>
    public List<ImportListItem> Items { get; set; } = [];
}

/// <summary>What became of one line of an import list.</summary>
public enum ImportListItemState
{
    /// <summary>Not looked up yet.</summary>
    Pending,

    /// <summary>Looked up and added to the library, or already there.</summary>
    Added,

    /// <summary>Looked up, but neither provider has the song; the user picks a candidate or skips it.</summary>
    Unresolved,

    /// <summary>Deliberately not added: a duplicate, an unsupported input, or the user skipped it.</summary>
    Skipped,
}

/// <summary>
/// One line of an import list, and everything that happened to it. The line text stays in
/// <see cref="Raw"/> whatever its state, because the review UI quotes it back.
/// </summary>
public sealed class ImportListItem : EntityBase
{
    /// <summary>Gets or sets the list this line belongs to.</summary>
    public long ImportListId { get; set; }

    /// <summary>
    /// Gets or sets the source's own identity for the line: the file's line number for a paste, the
    /// source's row id for the CSV and streaming imports of Phase 6.
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the song the line became, or <see langword="null"/> while it has none.</summary>
    public long? SongId { get; set; }

    /// <summary>Gets or sets the line as pasted, as a JSON object: <c>line</c>, <c>artist</c>, <c>title</c>.</summary>
    public string Raw { get; set; } = "{}";

    /// <summary>Gets or sets what became of the line.</summary>
    public ImportListItemState State { get; set; } = ImportListItemState.Pending;

    /// <summary>Gets or sets why the line ended up in its state, or <see langword="null"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the best candidates of an unresolved line, as a JSON array.</summary>
    public string Candidates { get; set; } = "[]";

    /// <summary>Gets or sets the list this line belongs to.</summary>
    public ImportList ImportList { get; set; } = null!;

    /// <summary>Gets or sets the song the line became, or <see langword="null"/>.</summary>
    public Song? Song { get; set; }
}

/// <summary>
/// One line as it was pasted: the text, and the artist and title a lookup parsed out of it. The
/// artist and title are <see langword="null"/> for a line that is not <c>Artist - Title</c>.
/// </summary>
/// <param name="Line">The trimmed line.</param>
/// <param name="Artist">The parsed artist, or <see langword="null"/>.</param>
/// <param name="Title">The parsed title, or <see langword="null"/>.</param>
public sealed record ImportListLine(string Line, string? Artist, string? Title);

/// <summary>
/// One candidate an unresolved line can be resolved to, as stored on the item. It is the part of a
/// <c>SongCandidate</c> the review screen shows, kept small because a list stores up to five per line.
/// </summary>
/// <param name="Source">The provider the candidate came from: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="MbRecordingId">The recording MBID, for a MusicBrainz candidate.</param>
/// <param name="DeezerId">The Deezer track id, for a Deezer candidate.</param>
/// <param name="Title">The title, including any version suffix.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="DurationMs">The duration in milliseconds, when the provider knows it.</param>
/// <param name="Score">The score against the query, from 0 to 100.</param>
public sealed record ImportListCandidate(
    string Source,
    string? MbRecordingId,
    long? DeezerId,
    string Title,
    string ArtistCredit,
    int? DurationMs,
    double Score);
