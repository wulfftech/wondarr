using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// The usenet half of reading a release's file list and of grabbing it: one NZB download at a time per post,
/// shared between searches and grabs, and the memory of which indexers refused (<see cref="NzbCooldowns"/>).
/// The search itself is in <c>IndexerSourceProvider.cs</c>, the rest of the grab in
/// <c>IndexerSourceProvider.Grab.cs</c>.
/// </summary>
public sealed partial class IndexerSourceProvider
{
    // The NZB downloads under way, by cache key, so two reads (a search's or a grab's) that need the same
    // post share one download. An entry goes when its download is done (the NZB is in the cache by then).
    // The lock is taken before a search's own (see ReadNzbAsync) and held for memory operations only.
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
                    flight = new Lazy<Task<NzbFetch>>(() => FetchNzbAsync(indexer, release, key, source, token));
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

            if (fetch.Failure is { } failure)
            {
                throw new IndexerException(failure);
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
    /// The NZB of a release for a grab, from the cache, from a download of the same post that is already under
    /// way (a search's, or another grab's), or from one this call starts. As in a search's read, one step under
    /// the lock of the downloads under way looks at the cache again, at the source's cool-down and at the
    /// downloads under way, so the cool-down is honoured and the request made once: a source that is cooling
    /// down fails the grab at once, with no request sent. An indexer whose quota is spent answers 200 with a
    /// sentence, an error document, a 429 or a 403: that fails the grab with the indexer's own words
    /// (redacted) and starts the cool-down, so the download client is never handed something that is not an
    /// NZB and the user sees why. None of these marks the post bad, and they are thrown as plain
    /// <see cref="DownloadClientException"/>s, which the caller does not blocklist.
    /// </summary>
    /// <param name="indexer">The indexer row to download through.</param>
    /// <param name="release">The release, as the grab knows it.</param>
    /// <param name="key">The release's cache key.</param>
    /// <param name="token">Cancels the wait, and the download when this call started it.</param>
    private async Task<byte[]> DownloadNzbAsync(Indexer indexer, ContainerRelease release, string key, CancellationToken token)
    {
        // Asked and keyed as a search asks: the name a release carries is the upstream's behind a Prowlarr row,
        // and that is what a refusal is kept under. A pushed release names no indexer row, so none is keyed.
        var asked = ToIndexerRelease(release, indexer) with { IndexerName = release.IndexerName };
        NzbSource? source = release.IndexerId == PushedReleaseClient.IndexerId ? null : NzbSource.Of(asked);

        while (true)
        {
            byte[]? kept;
            DateTimeOffset? cooling = null;
            Lazy<Task<NzbFetch>>? flight = null;
            var shared = false;

            lock (_flightsGate)
            {
                if (!_nzbCache.TryGet(key, out kept))
                {
                    if (source is { } known && _nzbCooldowns.TryGetUntil(known, out var until))
                    {
                        cooling = until;
                    }
                    else
                    {
                        shared = _flights.TryGetValue(key, out flight);

                        if (!shared)
                        {
                            flight = new Lazy<Task<NzbFetch>>(() => FetchNzbAsync(indexer, asked, key, source, token));
                            _flights[key] = flight;
                        }
                    }
                }
            }

            if (kept is not null)
            {
                return kept;
            }

            if (cooling is { } end)
            {
                throw new DownloadClientException(CoolingText(source!.Value, end));
            }

            if (shared)
            {
                LogNzbShared(_logger, release.IndexerName);
            }

            NzbFetch fetch;

            try
            {
                fetch = await flight!.Value.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shared && !token.IsCancellationRequested)
            {
                // The search or grab that was downloading it was cancelled, not this one: ask again, and
                // the download may be this grab's now.
                continue;
            }
            catch (XmlException exception)
            {
                throw new DownloadClientException(string.Concat(release.IndexerName, ": The NZB could not be read."), exception);
            }

            if (fetch.Refusal is { } words)
            {
                if (source is { } refusing)
                {
                    LogIndexerRefused(_logger, refusing.Name, fetch.CooldownUntil);
                }

                throw new DownloadClientException(string.Concat(release.IndexerName, ": ", words));
            }

            if (fetch.Failure is { } failure)
            {
                throw new DownloadClientException(string.Concat(release.IndexerName, ": ", failure));
            }

            return fetch.Content ?? throw new DownloadClientException($"The indexer returned no NZB for '{release.Title}'.");
        }
    }

    /// <summary>
    /// Downloads one NZB and keeps it in the cache once it parsed, then lets go of the entry the readers
    /// share. A refusal starts the source's cool-down first (none for a null source), so no read can find the
    /// entry gone and the source not yet cooling. What it throws is every sharing reader's failure.
    /// </summary>
    private async Task<NzbFetch> FetchNzbAsync(Indexer indexer, IndexerRelease release, string key, NzbSource? source, CancellationToken token)
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
                return NzbFetch.Refused(NzbAnswer.Clean(exception.Message, Redact), CoolDown(source, exception.RetryAfter));
            }

            if (download.Content is not { } content)
            {
                return NzbFetch.Linked(download.MagnetUrl);
            }

            var verdict = NzbAnswer.Check(content, Redact);

            if (verdict.Kind == NzbAnswerKind.AccountRefusal)
            {
                // Not an NZB, so never parsed as one; the indexer's own words are what the user is shown.
                return NzbFetch.Refused(verdict.Text, CoolDown(source, null));
            }

            if (verdict.Kind == NzbAnswerKind.ReleaseFailure)
            {
                // An error about this one post (removed, bad parameters): the indexer is fine, so it is
                // counted unreadable like a broken NZB and the other posts are still read.
                return NzbFetch.Failed(verdict.Text);
            }

            var listing = ListNzb(release, content);

            // The grab of this post, and the next search, reuse the bytes instead of spending another
            // download. Kept only once they parsed, so a broken document is not handed to the download
            // client later; and before the entry below goes, so no read finds neither.
            _nzbCache.Put(key, content);

            return NzbFetch.Listed(listing, content);
        }
        finally
        {
            lock (_flightsGate)
            {
                _flights.Remove(key);
            }
        }
    }

    /// <summary>
    /// Starts the cool-down of the source a release comes from, after its refusal. A release with no source
    /// (a pushed one, which names no indexer row) cools nothing down.
    /// </summary>
    /// <returns>The instant the cool-down ends; now, when there is none.</returns>
    private DateTimeOffset CoolDown(NzbSource? source, RetryConditionHeaderValue? retryAfter) =>
        source is { } cooling ? _nzbCooldowns.Start(cooling, WaitOf(retryAfter)) : _time.GetUtcNow();

    /// <summary>What is said of a source that is cooling down, in a search's message and in a grab's failure.</summary>
    private string CoolingText(NzbSource source, DateTimeOffset until) =>
        string.Concat(source.Name, " is cooling down until ", LocalTime(until), " after refusing NZB downloads");

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
    private sealed record NzbFetch(
        ContainerListing? Listing,
        string? Magnet,
        string? Refusal,
        DateTimeOffset CooldownUntil,
        string? Failure = null,
        byte[]? Content = null)
    {
        /// <summary>An NZB that parsed (and is in the cache): its file list, and the bytes a grab hands to the client.</summary>
        public static NzbFetch Listed(ContainerListing listing, byte[] content) => new(listing, null, null, default, null, content);

        /// <summary>A link that led to a magnet instead.</summary>
        public static NzbFetch Linked(string? magnet) => new(null, magnet, null, default);

        /// <summary>The indexer will not hand out NZBs: its words, and when the cool-down it started ends.</summary>
        public static NzbFetch Refused(string words, DateTimeOffset until) => new(null, null, words, until);

        /// <summary>The indexer cannot give this one post: its words. The indexer itself is fine.</summary>
        public static NzbFetch Failed(string words) => new(null, null, null, default, words);
    }
}
