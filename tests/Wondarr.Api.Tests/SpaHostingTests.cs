using System.Net;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The API host serving the built SPA: static files, the <c>__URL_BASE__</c> replacement, and the
/// fallback that turns client-side routes into <c>index.html</c>. Tests build their own web root so
/// they never depend on a real frontend build.
/// </summary>
public sealed class SpaHostingTests : IDisposable
{
    private const string LocalAddress = "192.168.1.20";
    private const string RemoteAddress = "8.8.8.8";

    /// <summary>The app.js the tests' web root serves; only its cache header is under test.</summary>
    private const string AppJs = "console.log('wondarr');\n";

    private readonly List<WondarrAppFactory> _factories = [];
    private readonly List<string> _webRoots = [];

    [Fact]
    public async Task Root_serves_the_spa_from_the_site_root()
    {
        using var factory = Create(WithSpa());
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("""<base href="/">""");
        response.Headers.CacheControl!.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task A_client_side_route_serves_the_spa_from_the_site_root()
    {
        using var factory = Create(WithSpa());
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/system/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("""<base href="/">""");
    }

    [Fact]
    public async Task A_client_side_route_under_the_url_base_serves_the_spa_with_that_base()
    {
        using var factory = Create([.. WithSpa(), ("Server:UrlBase", "/wondarr")]);
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/wondarr/wanted", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("""<base href="/wondarr/">""");
    }

    [Fact]
    public async Task Assets_under_the_url_base_are_served_to_a_remote_client_and_cached_immutably()
    {
        using var factory = Create([.. WithSpa(), ("Server:UrlBase", "/wondarr")]);
        using var client = factory.CreateClient(RemoteAddress);

        using var response = await client.GetAsync(new Uri("/wondarr/assets/app.js", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(AppJs);
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
    }

    [Fact]
    public async Task A_remote_client_asking_for_a_page_is_redirected_to_the_login_page()
    {
        using var factory = Create([.. WithSpa(), ("Server:UrlBase", "/wondarr")]);
        using var client = factory.CreateClient(RemoteAddress);

        using var response = await client.GetAsync(new Uri("/wondarr/wanted", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("/login");
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_404_and_not_the_spa()
    {
        using var factory = Create(WithSpa());
        using var client = factory.CreateClient(LocalAddress);
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task Index_html_is_never_served_with_the_placeholder_still_in_it()
    {
        using var factory = Create([.. WithSpa(), ("Server:UrlBase", "/wondarr")]);
        using var client = factory.CreateClient(LocalAddress);

        using var response = await client.GetAsync(new Uri("/wondarr/index.html", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        // index.html is hidden from the static files and file-like paths skip the SPA fallback,
        // so the raw template is simply not reachable; pages are served through the fallback.
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        body.Should().NotContain("__URL_BASE__");
    }

    [Fact]
    public async Task A_missing_index_html_reports_that_the_ui_is_not_built()
    {
        // The web root exists — the SPA has not been built into it yet.
        var empty = CreateWebRoot(new Dictionary<string, string>());
        using var factory = new WondarrAppFactory(TestConfiguration.Of(("webroot", empty)));
        _factories.Add(factory);

        using var client = factory.CreateClient(LocalAddress);
        using var response = await client.GetAsync(new Uri("/wanted", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Be("UI not built");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        foreach (var webRoot in _webRoots)
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, recursive: true);
            }
        }
    }

    /// <summary>Creates a temporary web root holding the SPA files the tests serve.</summary>
    private static string CreateWebRoot(IReadOnlyDictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "wondarr-spa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // A UTF-8 BOM would end up inside the served HTML; the real build has none either.
            File.WriteAllText(fullPath, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return root;
    }

    /// <summary>A web root with the built SPA: index.html and its hashed assets.</summary>
    private (string Key, string Value)[] WithSpa()
    {
        var root = CreateWebRoot(new Dictionary<string, string>
        {
            ["index.html"] = """
                <!doctype html>
                <html lang="en">
                  <head>
                    <base href="__URL_BASE__/">
                    <script type="module" src="./assets/app.js"></script>
                  </head>
                  <body><div id="root"></div></body>
                </html>
                """,
            [Path.Combine("assets", "app.js")] = AppJs,
        });

        _webRoots.Add(root);

        return [("webroot", root)];
    }

    private WondarrAppFactory Create(params (string Key, string Value)[] settings)
    {
        var factory = new WondarrAppFactory(TestConfiguration.Of(settings));
        _factories.Add(factory);

        return factory;
    }
}
