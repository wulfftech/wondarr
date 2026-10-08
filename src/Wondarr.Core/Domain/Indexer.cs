using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Domain;

/// <summary>
/// One configured indexer (ARCHITECTURE §5.7): a source of release candidates, its settings and how
/// eagerly it is asked. Stored in the <c>indexer</c> table. The settings are a JSON text column so a
/// new indexer type needs no migration.
/// </summary>
public sealed class Indexer : EntityBase
{
    /// <summary>Gets or sets the display name the user gave this indexer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the indexer type's name, for example <c>torznab</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets how this indexer's releases are downloaded.</summary>
    public DownloadProtocol Protocol { get; set; }

    /// <summary>Gets or sets the indexer type's settings as a JSON object.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Gets or sets a value indicating whether searches ask this indexer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the ask order; a lower number is asked first.</summary>
    public int Priority { get; set; } = 25;

    /// <summary>Gets or sets the download client this indexer's grabs go to, or <see langword="null"/> for the protocol's default.</summary>
    public long? DownloadClientId { get; set; }
}
