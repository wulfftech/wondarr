using Wondarr.Core.Domain;
using Wondarr.Core.Identity;

namespace Wondarr.Core.Songs;

/// <summary>What the caller wants the added songs to look like.</summary>
public sealed record SongAddOptions
{
    /// <summary>Gets the quality profile every new song is monitored against, or <see langword="null"/> for <see cref="SeedData.StandardProfileId"/>.</summary>
    public long? QualityProfileId { get; init; }

    /// <summary>Gets the library the songs are filed in, or <see langword="null"/> for the default library.</summary>
    public long? LibraryId { get; init; }

    /// <summary>Gets a value indicating whether the new songs are wanted.</summary>
    public bool Monitored { get; init; } = true;

    /// <summary>
    /// Gets the release the songs are pinned to, when the caller chose an album (an album add). Every
    /// identity that carries this release option is planned against it alone, with the one-track
    /// minimum an explicit choice implies, and its album context is saved pinned.
    /// </summary>
    public string? AlbumReleaseId { get; init; }

    /// <summary>Gets what added the songs: <c>ui</c>, <c>api</c> or <c>list:{id}</c>.</summary>
    public string AddedBy { get; init; } = "ui";

    /// <summary>
    /// Gets a value indicating whether a monitored new song is searched for at once
    /// (<c>search.search_on_add</c>). A song that arrives together with its file, such as one identified
    /// from a reference library, sets this to <see langword="false"/>: it is owned already and must never
    /// be searched for. When <see langword="false"/> nothing is enqueued, whatever the global setting says.
    /// </summary>
    public bool SearchOnAdd { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the add leaves its database transaction open. The songs are saved
    /// inside a transaction that the caller commits (or rolls back) once it has saved what belongs with
    /// them, so no other connection sees the new songs without it, for example a song without its file.
    /// Only a call that adds at least one song opens one. A caller that sets this must also set
    /// <see cref="SearchOnAdd"/> to <see langword="false"/>: nothing is enqueued while the transaction
    /// is open, because the queue writes through a connection of its own.
    /// </summary>
    public bool KeepTransactionOpen { get; init; }
}

/// <summary>What happened to one identity handed to the add pipeline.</summary>
public enum SongAddOutcome
{
    /// <summary>The identity became a new song row.</summary>
    Added,

    /// <summary>The library already held the song, or the batch named it twice.</summary>
    AlreadyExists,
}

/// <summary>One identity's fate in an add, paired with the song it produced or matched.</summary>
/// <param name="Identity">The identity the caller handed in.</param>
/// <param name="Outcome">Whether the identity was added or was already there.</param>
/// <param name="Song">The new song, or the row the identity already belonged to.</param>
public sealed record SongAddResult(SongIdentity Identity, SongAddOutcome Outcome, Song Song);

/// <summary>No provider knows the id the caller asked to add.</summary>
public sealed class SongNotFoundException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SongNotFoundException"/> class.</summary>
    public SongNotFoundException()
        : this("The identity could not be resolved.")
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SongNotFoundException"/> class.</summary>
    /// <param name="message">What could not be found.</param>
    public SongNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SongNotFoundException"/> class.</summary>
    /// <param name="message">What could not be found.</param>
    /// <param name="innerException">The failure behind this one.</param>
    public SongNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
