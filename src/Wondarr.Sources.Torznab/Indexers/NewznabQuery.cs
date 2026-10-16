// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Newznab/NewznabRequestGenerator.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: only the music search path remains (Wondarr never searches TV), one request
// per search instead of a pageable chain, and the limit is the lesser of 100 and the caps maximum.

using System.Text;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Builds the URL a music search is sent to: <c>t=music</c> with the artist and the album when the
/// indexer's caps say it supports both, otherwise <c>t=search</c> with the artist and the album as
/// one cleaned query, the way Lidarr's request generator does.
/// </summary>
public static class NewznabQuery
{
    /// <summary>The most results one search asks for, whatever the indexer offers.</summary>
    public const int MaxLimit = 100;

    /// <summary>The term a connection test's search asks for.</summary>
    public const string TestTerm = "test";

    /// <summary>Builds the URL of one search.</summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="capabilities">What the indexer's caps said it supports.</param>
    /// <param name="query">What to search for.</param>
    /// <returns>The URL to send the search to.</returns>
    public static Uri Build(IndexerEndpoint endpoint, NewznabCapabilities capabilities, ReleaseQuery query)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(query);

        return Compose(endpoint, Terms(capabilities, query), Math.Clamp(capabilities.MaxPageSize, 1, MaxLimit));
    }

    /// <summary>
    /// Builds the URL of the one search a connection test sends after the caps read: <c>t=search</c>
    /// for <see cref="TestTerm"/> with a limit of one result, the cheapest answer that still proves
    /// the key works for a search. An indexer whose caps offer no text search gets the request a
    /// normal search would send (the music search, say), so the test never asks for something a real
    /// search would not.
    /// </summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="capabilities">What the indexer's caps said it supports.</param>
    /// <returns>The URL to send the test search to.</returns>
    public static Uri BuildTest(IndexerEndpoint endpoint, NewznabCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(capabilities);

        var terms = capabilities.SupportsSearch
            ? "t=search&q=" + TestTerm
            : Terms(capabilities, new ReleaseQuery(TestTerm, TestTerm, null));

        return Compose(endpoint, terms, limit: 1);
    }

    /// <summary>
    /// Cleans a search term the way Lidarr's <c>NewsnabifyTitle</c> does: a plus becomes a space, and
    /// the rest is URL-escaped.
    /// </summary>
    /// <param name="title">The term to clean.</param>
    public static string Newsnabify(string title) => Uri.EscapeDataString(title.Replace("+", " "));

    /// <summary>The <c>t=…</c> part of a search: the music search when the caps allow it, otherwise a plain <c>q</c>.</summary>
    /// <param name="capabilities">What the indexer's caps said it supports.</param>
    /// <param name="query">What to search for.</param>
    private static string Terms(NewznabCapabilities capabilities, ReleaseQuery query) =>
        capabilities.SupportsMusicSearch
            ? "t=music&artist=" + Newsnabify(query.Artist) + "&album=" + Newsnabify(query.Album)
            : "t=search&q=" + Newsnabify(string.Concat(query.Artist, " ", query.Album));

    /// <summary>Adds the categories, the key and the paging to a search's <c>t=…</c> part.</summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="terms">The search's <c>t=…</c> part.</param>
    /// <param name="limit">The most results to ask for.</param>
    private static Uri Compose(IndexerEndpoint endpoint, string terms, int limit)
    {
        var url = new StringBuilder(endpoint.BaseUri.AbsoluteUri).Append('?').Append(terms);

        url.Append("&cat=").Append(string.Join(",", endpoint.Categories.Distinct()))
            .Append("&extended=1");

        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            url.Append("&apikey=").Append(Uri.EscapeDataString(endpoint.ApiKey));
        }

        url.Append("&offset=0")
            .Append("&limit=").Append(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new Uri(url.ToString(), UriKind.Absolute);
    }
}
