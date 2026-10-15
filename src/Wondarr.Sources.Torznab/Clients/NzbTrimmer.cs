using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// Opportunistic trimming of a usenet job (DECISIONS build session 8 #10): when every file of the
/// post has a plain name and is audio, a Par2 volume or a small sidecar, the audio files no wanted
/// song needs are dropped before the job starts. Anything else — an archive, an obfuscated name —
/// downloads whole, because a trimmed RAR set cannot be repaired. A trimmed post goes without its
/// Par2 volumes and its <c>.sfv</c> too: they describe the whole set, so with tracks missing the
/// client's verification tries to rebuild them and fails the job ("not enough repair blocks"). The
/// price is that a damaged article in a wanted file is caught by the import's decode check instead
/// of being repaired. A post that loses nothing keeps its Par2 set and its repair power.
/// </summary>
public static class NzbTrimmer
{
    /// <summary>
    /// The ids of the files to delete, repair data first; empty when the post must download whole or
    /// would lose nothing.
    /// </summary>
    /// <param name="files">The job's files, as the client listed them.</param>
    /// <param name="isWanted">Whether a file name is one a wanted song needs.</param>
    public static IReadOnlyList<string> FilesToDelete(IReadOnlyList<UsenetFileInfo> files, Func<string, bool> isWanted)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isWanted);

        var classified = files
            .Select((file, index) => (File: file, Entry: NzbFileList.FromFileName(index, file.FileName, file.SizeBytes)))
            .ToList();

        if (classified.Count == 0 || !NzbFileList.IsClean([.. classified.Select(pair => pair.Entry)]))
        {
            return [];
        }

        // Nothing wanted is in the post: trimming everything would leave nothing to download and
        // nothing to import, so the post is left whole and the import decides.
        if (!classified.Any(pair => pair.Entry.IsAudio && isWanted(pair.File.FileName)))
        {
            return [];
        }

        bool Unwanted((UsenetFileInfo File, NzbFile Entry) pair) => pair.Entry.IsAudio && !isWanted(pair.File.FileName);

        // Every audio file is wanted: nothing is trimmed, so the Par2 set stays complete and keeps its repair power.
        if (!classified.Any(Unwanted))
        {
            return [];
        }

        // Repair data first: if a delete fails part-way and the job is resumed whole, an untrimmed post
        // without its Par2 set still completes, while Par2 beside missing tracks fails the job.
        return
        [
            .. classified
                .Where(pair => pair.Entry.IsPar2 || IsSfv(pair.File.FileName))
                .Concat(classified.Where(Unwanted))
                .Select(pair => pair.File.FileId),
        ];
    }

    private static bool IsSfv(string fileName) => fileName.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase);
}
