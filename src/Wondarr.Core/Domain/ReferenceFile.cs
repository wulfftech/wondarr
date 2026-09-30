using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>What the scan (and, later, identification) concluded about one file of a reference library.</summary>
public enum ReferenceFileState
{
    /// <summary>Scanned, not yet identified.</summary>
    Pending,

    /// <summary>Identified as a song.</summary>
    Identified,

    /// <summary>Several candidates fit equally well; the user has to pick.</summary>
    Ambiguous,

    /// <summary>Identified as nothing Wondarr knows.</summary>
    Unmatched,

    /// <summary>Moved into a managed library.</summary>
    Adopted,

    /// <summary>The file is not decodable audio.</summary>
    Unreadable,

    /// <summary>The file was not found by the last scan.</summary>
    Missing,

    /// <summary>The user asked Wondarr to leave this file alone.</summary>
    Skipped,
}

/// <summary>
/// One audio file of a <see cref="ReferenceLibrary"/>, keyed by its path relative to the library root.
/// The row is what makes the scan incremental: an unchanged size and modification time mean the file
/// is not probed or re-read again.
/// </summary>
public sealed class ReferenceFile : EntityBase
{
    /// <summary>Gets or sets the library this file belongs to.</summary>
    public long ReferenceLibraryId { get; set; }

    /// <summary>Gets or sets the path relative to the library root, separated by <c>/</c>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the file's last write time, in UTC.</summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>Gets or sets the JSON of the measured <c>MediaInfo</c>, or <see langword="null"/> when it is not decodable.</summary>
    public string? Probe { get; set; }

    /// <summary>Gets or sets the JSON of the file's <c>FileTags</c>, or <see langword="null"/> when none were readable.</summary>
    public string? Tags { get; set; }

    /// <summary>Gets or sets the acoustic fingerprint, or <see langword="null"/>.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Gets or sets the AcoustID the fingerprint resolved to, or <see langword="null"/>.</summary>
    public string? AcoustId { get; set; }

    /// <summary>Gets or sets the song this file is, or <see langword="null"/> while unidentified.</summary>
    public long? SongId { get; set; }

    /// <summary>Gets or sets how sure the identification was, from 0 to 1.</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets what the last scan or identification concluded.</summary>
    public ReferenceFileState State { get; set; }

    /// <summary>Gets or sets how the file was identified, for example <c>tag_mbid</c>, or <see langword="null"/>.</summary>
    public string? IdentifiedBy { get; set; }

    /// <summary>Gets or sets the reason for the state, for example the probe error, or <see langword="null"/>.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the UTC instant the file was last seen on disk.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Gets or sets the UTC instant the file went missing, or <see langword="null"/>.</summary>
    public DateTime? MissingSince { get; set; }

    /// <summary>Gets or sets the library this file belongs to.</summary>
    public ReferenceLibrary ReferenceLibrary { get; set; } = null!;

    /// <summary>Gets or sets the song this file is, or <see langword="null"/> while unidentified.</summary>
    public Song? Song { get; set; }

    /// <summary>Gets or sets the ranked identification candidates the last run found.</summary>
    public List<MatchCandidate> Candidates { get; set; } = [];
}