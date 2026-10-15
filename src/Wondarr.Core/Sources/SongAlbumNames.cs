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
    /// blank, or when the context is a pseudo-album (<see cref="AlbumContextKind.PseudoSingles"/>).
    /// </summary>
    /// <param name="context">The song's album context, when it has one.</param>
    public static string? Of(AlbumContext? context) =>
        context is null || IsPseudo(context.Kind) || string.IsNullOrWhiteSpace(context.AlbumTitle) ? null : context.AlbumTitle;

    /// <summary>Whether the album context kind is a synthetic album that gathers songs, not a release.</summary>
    /// <param name="kind">The album context's kind.</param>
    public static bool IsPseudo(AlbumContextKind kind) => kind == AlbumContextKind.PseudoSingles;
}
