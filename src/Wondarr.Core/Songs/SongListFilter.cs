namespace Wondarr.Core.Songs;

/// <summary>
/// The optional, combinable (AND) filters of the song list. Everything but <see cref="CutoffMet"/> is
/// applied in SQL.
/// </summary>
public sealed record SongListFilter
{
    /// <summary>Gets only songs an artist is credited on, in any role.</summary>
    public long? ArtistId { get; init; }

    /// <summary>Gets only songs with this monitored flag.</summary>
    public bool? Monitored { get; init; }

    /// <summary>Gets a case-insensitive substring of the title or the artist credit; <c>%</c> and <c>_</c> match literally.</summary>
    public string? Term { get; init; }

    /// <summary>Gets only songs that do (or do not) hold a file.</summary>
    public bool? HasFile { get; init; }

    /// <summary>Gets only songs filed in this library.</summary>
    public long? LibraryId { get; init; }

    /// <summary>Gets only songs on this quality profile.</summary>
    public long? QualityProfileId { get; init; }

    /// <summary>Gets only songs whose file has this quality.</summary>
    public long? QualityId { get; init; }

    /// <summary>Gets only songs carrying this tag (matched case-insensitively).</summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Gets only songs whose file meets (<see langword="true"/>) or misses (<see langword="false"/>) the profile's
    /// cutoff. A song without a file is excluded from both.
    /// </summary>
    public bool? CutoffMet { get; init; }
}
