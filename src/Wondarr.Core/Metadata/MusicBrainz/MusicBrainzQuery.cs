using System.Text;

namespace Wondarr.Core.Metadata.MusicBrainz;

/// <summary>Builds the Lucene queries the MusicBrainz search endpoint understands.</summary>
public static class MusicBrainzQuery
{
    /// <summary>Every character Lucene treats as syntax, including the backslash.</summary>
    private const string LuceneSpecials = "+-&&||!(){}[]^\"~*?:\\/";

    /// <summary>
    /// Backslash-escapes every Lucene special character in <paramref name="term"/>.
    /// </summary>
    /// <param name="term">A title, an artist name, or any other user-supplied term.</param>
    /// <returns>The term, safe to drop inside a quoted phrase. <c>AC/DC</c> becomes <c>AC\/DC</c>.</returns>
    public static string Escape(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var builder = new StringBuilder(term.Length);

        foreach (var character in term)
        {
            if (LuceneSpecials.Contains(character, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Builds the "this song, by this artist" recording query.</summary>
    /// <param name="artist">The artist name, unescaped.</param>
    /// <param name="title">The song title, unescaped.</param>
    /// <returns>For example <c>recording:"Back in Black" AND artist:"AC\/DC"</c>.</returns>
    public static string RecordingByArtistAndTitle(string artist, string title)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(title);

        return $"recording:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\"";
    }
}
