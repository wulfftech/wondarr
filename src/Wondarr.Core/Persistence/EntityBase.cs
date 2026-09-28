namespace Wondarr.Core.Persistence;

/// <summary>
/// Base type for every persisted entity: a surrogate key plus UTC audit timestamps.
/// </summary>
public abstract class EntityBase
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the UTC instant the row was inserted.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC instant the row was last modified.</summary>
    public DateTime UpdatedAt { get; set; }
}
