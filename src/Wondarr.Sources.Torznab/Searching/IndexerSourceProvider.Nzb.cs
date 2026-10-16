using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Sources.Torznab.Indexers;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// The usenet half of reading a release's file list: one NZB download at a time per post, shared between
/// searches, and the memory of which indexers refused (<see cref="NzbCooldowns"/>). The search itself is in
/// <c>IndexerSourceProvider.cs</c>.
/// </summary>
public sealed partial class IndexerSourceProvider
{
    // The NZB downloads under way, by cache key, so two searches that need the same post share one
    // download. An entry goes when its download is done (the NZB is in the cache by then). The lock is
    // taken before a search's own (see ReadNzbAsync) and held for memory operations only.
    private readonly Lock _flightsGate = new();
    private readonly Dictionary<string, Lazy<Task<NzbFetch>>> _flights = new(StringComparer.Ordinal);

    /// <summary>
    /// The file list of a usenet release. In one step, under the lock of the downloads under way, the
    /// search state looks at the cache again, at the indexer's refusals, at its cool-down, at a download of
    /// this very post that is already under way, and at the cap; only a read that really downloads takes a
    /// slot, and only that one registers a download for others to share.
    /// </summary>
    private async Task<ContainerListing?> ReadNzbAsync(Indexer indexer, IndexerRelease release, string? hash, SearchState state, CancellationToken token)
    {
        var key = NzbKey(release);
        var source = NzbSource.Of(release);

        while (true)
        {
            NzbTurn turn;
            byte[]? kept;
            Lazy<Task<NzbFetch>>? flight = null;

            lock (_flightsGate)
            {
                turn = state.BeginNzbRead(source, key, _flights.ContainsKey(key), _nzbCache, _nzbCooldowns, out kept);

                if (turn == NzbTurn.Shared)
                {
                    flight = _flights[key];
                }
                else if (turn == NzbTurn.Download)
                {
                    flight = new Lazy<Task<NzbFetch>>(() => FetchNzbAsync(indexer, release, key, token));
                    _flights[key] = flight;
                }
            }

            if (turn == NzbTurn.Kept)
            {
                // An earlier search or a push already paid for this NZB, and it parsed then: no download.
                return ListNzb(release, kept!);
            }

            if (turn is NzbTurn.Skipped or NzbTurn.Cooling)
            {
                // Dropped, as a refusal in this search drops it: a post listed without a file list would be
                // grabbable, and its grab downloads the NZB from an indexer that is sure to refuse.
                return null;
            }

            if (turn == NzbTurn.Shared)
            {
                LogNzbShared(_logger, source.Name);
            }

            NzbFetch fetch;

            try
            {
                fetch = await flight!.Value.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (turn == NzbTurn.Shared && !token.IsCancellationRequested)
            {
                // The search that was downloading it was cancelled (its budget ran out), not this one:
                // ask again, and the download may be this search's now.
                continue;
            }

            if (fetch.Refusal is { } words)
            {
                Refuse(source, state, words, fetch.CooldownUntil);

                return null;
            }

            if (fetch.Listing is { } listing)
            {
                return listing with { Release = release };
            }

            var magnet = release with { MagnetUrl = fetch.Magnet ?? release.MagnetUrl };

            return new ContainerListing(magnet, null, hash ?? InfoHashOf(fetch.Magnet), null);
        }
    }

    /// <summary>
    /// Downloads one NZB and keeps it in the cache once it parsed, then lets go of the entry the readers
    /// share. A refusal starts the indexer's cool-down first, so no search can find the entry gone and the
    /// indexer not yet cooling. What it throws is every sharing reader's failure.
    /// </summary>
    private async Task<NzbFetch> FetchNzbAsync(Indexer indexer, IndexerRelease release, string key, CancellationToken token)
    {
        try
        {
            IndexerDownload download;

            try
            {
                download = await _clients.GetClient(indexer).DownloadAsync(indexer, release, token).ConfigureAwait(false);
            }
            catch (IndexerException exception) when (exception.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                // 429 and 403 are how some indexers say "no more downloads today". A dropped connection or
                // a 5xx carries no status like these and does not cut the indexer off.
                return NzbFetch.Refused(NzbAnswer.Clean(exception.Message, Redact), CoolDown(release, exception.RetryAfter));
            }

            if (download.Content is not { } content)
            {
                return NzbFetch.Linked(download.MagnetUrl);
            }

            var verdict = NzbAnswer.Check(content, Redact);

            if (verdict.Kind == NzbAnswerKind.AccountRefusal)
            {
                // Not an NZB, so never parsed as one; the indexer's own words are what the user is shown.
                return NzbFetch.Refused(verdict.Text, CoolDown(release, null));
            }

            if (verdict.Kind == NzbAnswerKind.ReleaseFailure)
            {
                // An error about this one post (removed, bad parameters): the indexer is fine, so it is
                // counted unreadable like a broken NZB and the other posts are still read.
                throw new IndexerException(verdict.Text);
            }

            var listing = ListNzb(release, content);

            // The grab of this post, and the next search, reuse the bytes instead of spending another
            // download. Kept only once they parsed, so a broken document is not handed to the download
            // client later; and before the entry below goes, so no read finds neither.
            _nzbCache.Put(key, content);

            return NzbFetch.Listed(listing);
        }
        finally
        {
            lock (_flightsGate)
            {
                _flights.Remove(key);
            }
        }
    }

    /// <summary>Starts the cool-down of the source a release comes from, after its refusal.</summary>
    /// <returns>The instant the cool-down ends.</returns>
    private DateTimeOffset CoolDown(IndexerRelease release, RetryConditionHeaderValue? retryAfter) =>
        _nzbCooldowns.Start(NzbSource.Of(release), WaitOf(retryAfter));

    /// <summary>
    /// Stops the search reading NZBs from a source that said it will not hand out more; the cool-down that
    /// keeps later searches away was started by the download that was refused.
    /// </summary>
    private void Refuse(NzbSource source, SearchState state, string text, DateTimeOffset until)
    {
        if (state.Refuse(source, text))
        {
            LogIndexerRefused(_logger, source.Name, until);
        }
    }

    /// <summary>Tells the log, once per search, of each source whose posts were dropped because it is cooling down.</summary>
    private void LogCoolings(SearchState state)
    {
        foreach (var (source, until) in state.Coolings)
        {
            LogIndexerCooling(_logger, source.Name, until);
        }
    }

    /// <summary>How long a <c>Retry-After</c> header asks to wait: its seconds, or the time left to its date.</summary>
    private TimeSpan? WaitOf(RetryConditionHeaderValue? retryAfter) => retryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - _time.GetUtcNow(),
        _ => null,
    };

    /// <summary>An instant as the host's local time of day with its UTC offset, and the date when it is not today.</summary>
    private string LocalTime(DateTimeOffset instant)
    {
        var zone = _time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Date;

        return local.ToString(local.Date == today ? "HH:mm 'UTC'zzz" : "d MMM HH:mm 'UTC'zzz", CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Indexer} refused an NZB download; no more NZBs are read from it until {Until}")]
    private static partial void LogIndexerRefused(ILogger logger, string indexer, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Indexer} is cooling down until {Until} after refusing NZB downloads; its uncached posts are not read")]
    private static partial void LogIndexerCooling(ILogger logger, string indexer, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Indexer}: this NZB is already being downloaded for another read; sharing that download")]
    private static partial void LogNzbShared(ILogger logger, string indexer);

    /// <summary>What one NZB download came to, shared by every read that waited for it.</summary>
    private sealed record NzbFetch(ContainerListing? Listing, string? Magnet, string? Refusal, DateTimeOffset CooldownUntil)
    {
        /// <summary>An NZB that parsed (and is in the cache).</summary>
        public static NzbFetch Listed(ContainerListing listing) => new(listing, null, null, default);

        /// <summary>A link that led to a magnet instead.</summary>
        public static NzbFetch Linked(string? magnet) => new(null, magnet, null, default);

        /// <summary>The indexer will not hand out NZBs: its words, and when the cool-down it started ends.</summary>
        public static NzbFetch Refused(string words, DateTimeOffset until) => new(null, null, words, until);
    }
}
