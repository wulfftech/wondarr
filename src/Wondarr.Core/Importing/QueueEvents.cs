using Wondarr.Core.Domain;
using Wondarr.Core.Messaging;

namespace Wondarr.Core.Importing;

/// <summary>
/// A queue item moved to another state. Published by the import pipeline so the UI can follow a grab
/// from "downloading" to "imported" without polling the table.
/// </summary>
/// <param name="QueueItemId">The queue item that changed.</param>
/// <param name="SongId">The song the grab is for.</param>
/// <param name="State">The item's state after the change.</param>
public sealed record QueueItemChangedEvent(long QueueItemId, long SongId, QueueItemState State) : IEvent
{
    /// <summary>How far the download has got, 0–1.</summary>
    public double Progress { get; init; }

    /// <summary>What the source or the poll last said about the item, or <see langword="null"/>.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// A song gained a library file: either its first one or a better one that replaced it.
/// </summary>
/// <param name="SongId">The song that is now satisfied.</param>
/// <param name="SongFileId">The <c>song_file</c> row holding the imported file.</param>
/// <param name="Upgraded">Whether the file replaced one the song already had.</param>
public sealed record SongImportedEvent(long SongId, long SongFileId, bool Upgraded) : IEvent;
