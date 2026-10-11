namespace Wondarr.Core.Songs;

/// <summary>
/// A change was refused because a Compact run has the song's file in flight: it is parked in the hidden
/// staging folder, and deleting the song or choosing its album now would leave the file with nothing
/// pointing at it (or have the run overwrite the choice).
/// </summary>
public sealed class SongsCompactingException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SongsCompactingException"/> class.</summary>
    /// <param name="songIds">The songs a compaction is moving.</param>
    public SongsCompactingException(IReadOnlyList<long> songIds)
        : base(MessageFor(songIds))
    {
        SongIds = songIds;
    }

    /// <summary>Gets every song a compaction is moving.</summary>
    public IReadOnlyList<long> SongIds { get; }

    private static string MessageFor(IReadOnlyList<long> songIds)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        return songIds.Count <= 1
            ? "Compaction is moving this song; try again when it finishes"
            : "Compaction is moving these songs; try again when it finishes: "
                + string.Join(", ", songIds.Take(10))
                + (songIds.Count > 10 ? ", ..." : string.Empty);
    }
}
