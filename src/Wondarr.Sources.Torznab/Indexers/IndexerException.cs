namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// An indexer could not be reached or its answer could not be used. The message is safe to show the
/// user: it never carries the request URL, whose query holds the API key.
/// </summary>
public sealed class IndexerException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="IndexerException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    public IndexerException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="IndexerException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    /// <param name="inner">The failure that caused this one.</param>
    public IndexerException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
