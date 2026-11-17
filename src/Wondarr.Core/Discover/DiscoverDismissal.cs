using Wondarr.Core.Persistence;

namespace Wondarr.Core.Discover;

/// <summary>
/// A suggestion the user turned down, stored in the <c>discover_dismissal</c> table. It matches a
/// candidate by recording MBID when both have one, and by normalised artist and title otherwise. Deleting
/// the row is the undo.
/// </summary>
public sealed class DiscoverDismissal : EntityBase
{
    /// <summary>Gets or sets the lower-case recording MBID, or <see langword="null"/> when the suggestion had none.</summary>
    public string? MbRecordingId { get; set; }

    /// <summary>Gets or sets the artist as the suggestion spelled it, for the undo list.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the title as the suggestion spelled it, for the undo list.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalised artist name (<c>TextMatching.NormalizeArtist</c>).</summary>
    public string ArtistKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalised title (<c>TextMatching.Normalize</c>).</summary>
    public string TitleKey { get; set; } = string.Empty;
}
