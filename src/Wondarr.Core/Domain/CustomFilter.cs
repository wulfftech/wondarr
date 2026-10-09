using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// A saved view of a list page (Lidarr's custom filter): a label and the filters the UI applies.
/// Stored in the <c>custom_filter</c> table; the filters are JSON text so the UI can add filter
/// keys without a migration.
/// </summary>
public sealed class CustomFilter : EntityBase
{
    /// <summary>Gets or sets the page the view belongs to, for example <c>library</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the label the user gave the view; unique within its type.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the filters as a JSON array of <c>{ key, value, type }</c> objects.</summary>
    public string Filters { get; set; } = "[]";
}
