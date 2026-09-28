namespace Compilarr.Core.Persistence;

/// <summary>
/// A single key/value application setting. The value is stored as JSON text.
/// </summary>
public sealed class Setting : EntityBase
{
    /// <summary>Gets or sets the unique setting key, for example <c>ui.theme</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the JSON-encoded value.</summary>
    public string Value { get; set; } = string.Empty;
}
