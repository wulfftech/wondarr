using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>What the scan is allowed to do with the files of a reference library.</summary>
public enum ReferenceLibraryMode
{
    /// <summary>Read only: the files stay where they are and are only counted as owned.</summary>
    Reference,

    /// <summary>Adopt: identified files are re-tagged and moved into a managed library.</summary>
    Adopt,
}

/// <summary>
/// A folder the user already has (flat or layered) that Wondarr scans for songs it does not have to
/// download (LIBRARY_OUTPUT §7.6). The scan is strictly read-only towards the folder.
/// </summary>
public sealed class ReferenceLibrary : EntityBase
{
    /// <summary>Gets or sets the name the user gave the library. Unique, case-insensitively.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute path of the folder to walk.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets what the scan may do with the files it finds.</summary>
    public ReferenceLibraryMode Mode { get; set; } = ReferenceLibraryMode.Reference;

    /// <summary>Gets or sets the managed library adopted songs are filed under, or <see langword="null"/>.</summary>
    public long? LibraryId { get; set; }

    /// <summary>Gets or sets a value indicating whether the daily scan covers this library.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the UTC instant of the last completed scan, or <see langword="null"/>.</summary>
    public DateTime? LastScannedAt { get; set; }

    /// <summary>Gets or sets the one-line summary of the last scan, or why it did not run.</summary>
    public string? LastScanMessage { get; set; }

    /// <summary>Gets or sets the managed library adopted songs are filed under, or <see langword="null"/>.</summary>
    public Library? Library { get; set; }
}