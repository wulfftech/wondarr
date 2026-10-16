using Wondarr.Core.Domain;

namespace Wondarr.Core.Sources;

/// <summary>
/// The album names a release may be named after when it is judged against a song. The album context's
/// title counts, unless the context is a pseudo-album: a lone song in the Plexamp preset is filed under
/// "Singles", which no release is ever named.
/// </summary>
public static class SongAlbumNames
{
    /// <summary>
    /// The title of the song's album context, or <see langword="null"/> when the song has none, when it is
    /// blank, or when the context is a pseudo-album (<see cref="AlbumContextKind.PseudoSingles"/>, or the
    /// library-wide <see cref="AlbumContextKind.Compilation"/> the compilation policy titles after the library).
    /// </summary>
    /// <param name="context">The song's album context, when it has one.</param>
    public static string? Of(AlbumContext? context) => context is null ? null : Of(context.Kind, context.AlbumTitle);

    /// <summary>
    /// The same decision for an album context that travels as its kind and title (a search request, a decision
    /// context). A kind that is not known counts as a release: only a context known to be a pseudo-album does not.
    /// </summary>
    /// <param name="kind">The album context's kind, when it is known.</param>
    /// <param name="title">The album context's title, when the song has one.</param>
    public static string? Of(AlbumContextKind? kind, string? title) =>
        (kind is { } known && IsPseudo(known)) || string.IsNullOrWhiteSpace(title) ? null : title;

    /// <summary>Whether the album context kind is a synthetic album that gathers songs, not a release.</summary>
    /// <param name="kind">The album context's kind.</param>
    public static bool IsPseudo(AlbumContextKind kind) => kind is AlbumContextKind.PseudoSingles or AlbumContextKind.Compilation;
}
