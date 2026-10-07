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

    /// <summary>
    /// Builds the album search query from what the user typed. <c>Queen - A Night at the Opera</c>
    /// becomes a "this album, by this artist" query; anything else is searched as a release-group
    /// phrase, with the plain text as the fallback so a single word still matches.
    /// </summary>
    /// <param name="term">The raw search term.</param>
    /// <returns>The Lucene query.</returns>
    public static string ReleaseGroupByTerm(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var separator = term.IndexOf(" - ", StringComparison.Ordinal);

        if (separator > 0 && separator < term.Length - 3)
        {
            var artist = term[..separator].Trim();
            var title = term[(separator + 3)..].Trim();

            return $"releasegroup:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\"";
        }

        return $"releasegroup:\"{Escape(term)}\" OR {Escape(term)}";
    }
}
