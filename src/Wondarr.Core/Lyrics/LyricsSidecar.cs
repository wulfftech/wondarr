namespace Wondarr.Core.Lyrics;

/// <summary>
/// The lyrics sidecar a placed file may get beside it (LIBRARY_OUTPUT.md §7.4): <c>.lrc</c> when LRCLIB
/// has synced lyrics, <c>.txt</c> when it only has plain ones.
/// </summary>
internal static class LyricsSidecar
{
    /// <summary>The extension of the synced-lyrics sidecar.</summary>
    internal const string SyncedExtension = ".lrc";

    /// <summary>The extension of the plain-lyrics sidecar.</summary>
    internal const string PlainExtension = ".txt";

    /// <summary>
    /// Where the sidecar for a placed audio file goes, or <see langword="null"/> when the lookup holds
    /// no text to write. The name is the audio file's own, with the extension swapped, which is what
    /// Plex reads.
    /// </summary>
    /// <param name="audioPath">The audio file as it was placed.</param>
    /// <param name="lookup">What LRCLIB answered.</param>
    /// <returns>The sidecar's path, or <see langword="null"/>.</returns>
    public static string? PathFor(string audioPath, LyricsLookup lookup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentNullException.ThrowIfNull(lookup);

        if (!string.IsNullOrWhiteSpace(lookup.SyncedLyrics))
        {
            return Path.ChangeExtension(audioPath, SyncedExtension);
        }

        return string.IsNullOrWhiteSpace(lookup.PlainLyrics)
            ? null
            : Path.ChangeExtension(audioPath, PlainExtension);
    }

    /// <summary>
    /// The text the sidecar holds: the synced lines for an <c>.lrc</c>, the plain text otherwise. Line
    /// endings are normalised to <c>\n</c> and the text ends with exactly one of them.
    /// </summary>
    /// <param name="lookup">What LRCLIB answered, with at least one of the two texts.</param>
    /// <returns>The file's content.</returns>
    public static string ContentFor(LyricsLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var text = string.IsNullOrWhiteSpace(lookup.SyncedLyrics)
            ? lookup.PlainLyrics
            : lookup.SyncedLyrics;

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        return normalised.TrimEnd('\n') + "\n";
    }
}
