using Wondarr.Core.Searching;
using Wondarr.Sources.Torznab.Clients;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// A grab was turned away, with no request sent, because the indexer its NZB would come from is cooling down
/// after refusing NZB downloads (<see cref="NzbCooldowns"/>). It is the indexer's bad moment, not the post's:
/// the search service does not blocklist the candidate, and since every other candidate of that indexer would
/// be turned away the same way, it ends the run instead of burning them one by one.
/// </summary>
public sealed class NzbCoolingException : DownloadClientException, ISourceGrabFailure
{
    /// <summary>Initialises a new instance of the <see cref="NzbCoolingException"/> class.</summary>
    /// <param name="message">The sentence to show the user: who is cooling down, and until when.</param>
    /// <param name="until">The instant the cool-down ends.</param>
    public NzbCoolingException(string message, DateTimeOffset until)
        : base(message)
    {
        Until = until;
    }

    /// <summary>Gets the instant the cool-down ends.</summary>
    public DateTimeOffset Until { get; }

    /// <inheritdoc />
    bool ISourceGrabFailure.BlocklistCandidate => false;
}
