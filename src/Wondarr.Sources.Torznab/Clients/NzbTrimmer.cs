using Wondarr.Sources.Torznab.Parsing;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// Opportunistic trimming of a usenet job (DECISIONS build session 8 #10): when every file of the
/// post has a plain name and is audio, a Par2 volume or a small sidecar, the audio files no wanted
/// song needs are dropped before the job starts. Anything else — an archive, an obfuscated name —
/// downloads whole, because a trimmed RAR set cannot be repaired.
/// </summary>
public static class NzbTrimmer
{
    /// <summary>The ids of the files to delete; empty when the post must download whole.</summary>
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

        return
        [
            .. classified
                .Where(pair => pair.Entry.IsAudio && !pair.Entry.IsPar2 && !isWanted(pair.File.FileName))
                .Select(pair => pair.File.FileId),
        ];
    }
}
