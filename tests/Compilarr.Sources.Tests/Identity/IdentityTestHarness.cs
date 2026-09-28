using System.Net;
using System.Text;
using Compilarr.Core.Identity;
using Compilarr.Core.Metadata;
using Compilarr.Core.Metadata.Deezer;
using Compilarr.Core.Metadata.MusicBrainz;
using Compilarr.Sources.Tests.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Compilarr.Sources.Tests.Identity;

/// <summary>One canned response: a request path, and optionally a fragment its query must contain.</summary>
/// <param name="Path">The request path, matched case-insensitively.</param>
/// <param name="QueryContains">A fragment of the URL-decoded query, matched case-insensitively; null matches any query.</param>
/// <param name="Body">The body to answer with.</param>
/// <param name="Status">The status to answer with.</param>
internal sealed record FixtureRoute(string Path, string? QueryContains, string Body, HttpStatusCode Status);

/// <summary>
/// Serves recorded responses to the real clients. Routes are matched on the path and, when given, on a
/// case-insensitive fragment of the URL-decoded query; anything unmapped gets the provider's own "I do
/// not know this" answer, so a test can prove a request was never made by leaving it unrouted.
/// </summary>
internal sealed class FixtureRouteHandler : HttpMessageHandler
{
    private readonly List<FixtureRoute> _routes = [];
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _unmapped;

    internal FixtureRouteHandler(Func<HttpRequestMessage, HttpResponseMessage> unmapped) => _unmapped = unmapped;

    /// <summary>Every request URI the handler was given, in order.</summary>
    internal List<Uri> Requests { get; } = [];

    /// <summary>Routes one path, with an optional query fragment, to a body.</summary>
    internal FixtureRouteHandler Map(
        string path,
        string body,
        string? queryContains = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add(new FixtureRoute(path, queryContains, body, status));

        return this;
    }

    /// <summary>MusicBrainz' answer for a recording, release, recording search or ISRC it does not know.</summary>
    internal static HttpResponseMessage MusicBrainzNotFound() =>
        Json(HttpStatusCode.NotFound, """{"error":"Not Found"}""");

    /// <summary>Deezer's answer for anything it does not know: empty search results, or the code-800 error.</summary>
    internal static HttpResponseMessage DeezerUnmapped(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("search", StringComparison.OrdinalIgnoreCase)
            ? Json(HttpStatusCode.OK, """{"data":[],"total":0}""")
            : Json(HttpStatusCode.OK, """{"error":{"type":"DataException","message":"no data","code":800}}""");

    /// <summary>A 200 carrying <paramref name="body"/>.</summary>
    internal static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);

        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        foreach (var route in _routes)
        {
            if (string.Equals(route.Path, path, StringComparison.OrdinalIgnoreCase)
                && (route.QueryContains is null || query.Contains(route.QueryContains, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(Json(route.Status, route.Body));
            }
        }

        return Task.FromResult(_unmapped(request));
    }
}

/// <summary>The real MusicBrainz and Deezer clients over stub handlers, and the resolver on top of them.</summary>
internal sealed class IdentityHarness
{
    private IdentityHarness(FixtureRouteHandler musicBrainz, FixtureRouteHandler deezer, IIdentityResolver resolver)
    {
        MusicBrainz = musicBrainz;
        Deezer = deezer;
        Resolver = resolver;
    }

    /// <summary>The MusicBrainz routes: unmapped requests answer 404.</summary>
    internal FixtureRouteHandler MusicBrainz { get; }

    /// <summary>The Deezer routes: unmapped searches answer empty, everything else answers code 800.</summary>
    internal FixtureRouteHandler Deezer { get; }

    /// <summary>The resolver under test.</summary>
    internal IIdentityResolver Resolver { get; }

    /// <summary>Builds a harness with fresh clients, caches and route tables.</summary>
    internal static IdentityHarness Create()
    {
        var musicBrainz = new FixtureRouteHandler(_ => FixtureRouteHandler.MusicBrainzNotFound());
        var deezer = new FixtureRouteHandler(FixtureRouteHandler.DeezerUnmapped);

        var musicBrainzClient = new MusicBrainzClient(
            new HttpClient(musicBrainz, disposeHandler: false) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            new InMemoryMetadataCache());

        var deezerClient = new DeezerClient(
            new HttpClient(deezer, disposeHandler: false) { BaseAddress = new Uri("https://api.deezer.com/") },
            new InMemoryMetadataCache());

        var resolver = new IdentityResolver(
            musicBrainzClient,
            deezerClient,
            Options.Create(new MetadataOptions()),
            NullLogger<IdentityResolver>.Instance);

        return new IdentityHarness(musicBrainz, deezer, resolver);
    }

    /// <summary>Every MusicBrainz request path the handler saw.</summary>
    internal IEnumerable<string> MusicBrainzPaths => MusicBrainz.Requests.Select(uri => uri.AbsolutePath);

    /// <summary>Every request the Deezer handler saw, URL-decoded so assertions can read the query.</summary>
    internal IEnumerable<string> DeezerRequests => Deezer.Requests.Select(Decode);

    /// <summary>Every request the MusicBrainz handler saw, URL-decoded so assertions can read the query.</summary>
    internal IEnumerable<string> MusicBrainzRequests => MusicBrainz.Requests.Select(Decode);

    /// <summary>The request URI, with its escaping undone.</summary>
    private static string Decode(Uri uri) => Uri.UnescapeDataString(uri.ToString());
}
