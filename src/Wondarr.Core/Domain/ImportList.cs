using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One import list: a named source of songs Wondarr resolves and adds. A <c>paste</c> list is a block
/// of text the user pasted, resolved once; every other type is a provider (a CSV export, a playlist,
/// a scrobble list) that a sync reads again on <see cref="SyncIntervalHours"/>.
/// </summary>
public sealed class ImportList : EntityBase
{
    /// <summary>The <see cref="Type"/> of a pasted list, resolved once and never synced.</summary>
    public const string PasteType = "paste";

    /// <summary>The <see cref="Type"/> of an uploaded CSV file (Exportify's export, or any mapped CSV).</summary>
    public const string CsvType = "csv";

    /// <summary>The <see cref="Policy"/> of a list that only ever adds, never removes.</summary>
    public const string AddOnlyPolicy = "AddOnly";

    /// <summary>Gets or sets the list type: <c>paste</c>, or a provider's type such as <c>csv</c>.</summary>
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

    /// <summary>Gets or sets the one-line result of the last sync, or why it failed.</summary>
    public string? LastSyncMessage { get; set; }

    /// <summary>Gets or sets a value indicating whether the scheduled sync reads this list.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how many hours pass between scheduled syncs; <c>0</c> means "only when asked".
    /// </summary>
    public int SyncIntervalHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the content the list was read from when the source is a file the user uploaded
    /// (a CSV), so the list lives in the database — and in its backups — rather than in a loose file.
    /// </summary>
    public string? SourceText { get; set; }

    /// <summary>Gets or sets a value indicating whether the list is kept as a Plex playlist of the same name.</summary>
    public bool PlexPlaylist { get; set; }

    /// <summary>Gets or sets the rating key of the Plex playlist the list writes, or <see langword="null"/> before the first write.</summary>
    public string? PlexPlaylistKey { get; set; }

    /// <summary>Gets or sets a value indicating whether the list is written as an <c>.m3u8</c> file in its library's <c>Playlists</c> folder.</summary>
    public bool M3uExport { get; set; }

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

    /// <summary>Gets or sets where the item sits in the source, from 0: the playlist order.</summary>
    public int Position { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant a sync found the item gone from its source, or
    /// <see langword="null"/> while it is still there.
    /// </summary>
    public DateTime? RemovedAt { get; set; }

    /// <summary>
    /// Gets or sets the Plex rating key of the track the item's file is, as last found, or
    /// <see langword="null"/>; valid only while the file is still at <see cref="PlexRatingKeyPath"/>.
    /// </summary>
    public string? PlexRatingKey { get; set; }

    /// <summary>Gets or sets the file path <see cref="PlexRatingKey"/> was found for.</summary>
    public string? PlexRatingKeyPath { get; set; }

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
