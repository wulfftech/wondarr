using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>A Prowlarr row's settings.</summary>
/// <param name="Url">Prowlarr's base URL, for example <c>http://prowlarr:9696</c>.</param>
/// <param name="ApiKey">Prowlarr's API key; a secret, sent only in the <c>X-Api-Key</c> header.</param>
/// <param name="IndexerIds">The Prowlarr indexers to ask; empty asks them all.</param>
/// <param name="Categories">The newznab categories to search.</param>
public sealed record ProwlarrSettings(string Url, string ApiKey, IReadOnlyList<int> IndexerIds, IReadOnlyList<int> Categories)
{
    /// <summary>Reads a row's (or a draft's) settings.</summary>
    /// <param name="settings">The settings JSON.</param>
    public static ProwlarrSettings Read(JsonElement settings) => new(
        Text(settings, "url").Trim().TrimEnd('/'),
        Text(settings, "apiKey").Trim(),
        Numbers(Text(settings, "indexerIds")),
        Numbers(Text(settings, "categories")) is { Count: > 0 } categories ? categories : [3000]);

    private static string Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    private static List<int> Numbers(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : (int?)null)
            .OfType<int>()];
}

/// <summary>
/// The Prowlarr indexer type (DECISIONS build session 8 #2): one row that searches every indexer
/// Prowlarr has, through <c>/api/v1/search</c>. The row chooses torrent or usenet.
/// </summary>
public sealed class ProwlarrIndexerType : IIndexerType
{
    private readonly IHttpClientFactory _clients;
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="ProwlarrIndexerType"/> class.</summary>
    /// <param name="clients">Creates the indexer HTTP client.</param>
    /// <param name="secrets">The API key is registered here.</param>
    public ProwlarrIndexerType(IHttpClientFactory clients, ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(secrets);

        _clients = clients;
        _secrets = secrets;
    }

    /// <inheritdoc />
    public string Type => "prowlarr";

    /// <inheritdoc />
    public DownloadProtocol? Protocol => null;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "text", Required: true, HelpText: "Prowlarr's address, for example http://prowlarr:9696"),
        new("apiKey", "API key", "password", Required: true, Secret: true, HelpText: "Prowlarr's API key (Settings → General)."),
        new("indexerIds", "Indexer ids", "text", Required: false, HelpText: "The Prowlarr indexers to ask, comma-separated; empty asks them all."),
        new("categories", "Categories", "text", Required: false, HelpText: "The newznab category ids to search, comma-separated; 3000 is music."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = ProwlarrSettings.Read(settings);
        var messages = new List<string>();

        if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            messages.Add("The URL must be an http(s) address, for example http://prowlarr:9696.");
        }

        if (parsed.ApiKey.Length == 0)
        {
            messages.Add("An API key is required.");
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var parsed = ProwlarrSettings.Read(settings);
        _secrets.Register(parsed.ApiKey);

        if (Validate(settings) is { Count: > 0 } problems)
        {
            return new ProviderTestResult(false, problems[0]);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{parsed.Url}/api/v1/system/status"));
            request.Headers.Add("X-Api-Key", parsed.ApiKey);
            using var response = await _clients.CreateClient(IndexerHttp.ClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => new ProviderTestResult(false, "Prowlarr refused the API key."),
                _ when response.IsSuccessStatusCode => new ProviderTestResult(true, null),
                _ => new ProviderTestResult(false, $"Prowlarr answered {(int)response.StatusCode}."),
            };
        }
        catch (HttpRequestException)
        {
            return new ProviderTestResult(false, "Prowlarr could not be reached.");
        }
    }
}

/// <summary>
/// Searches a Prowlarr row: <c>GET /api/v1/search?query=&amp;type=search&amp;categories=&amp;indexerIds=</c>
/// with the key in <c>X-Api-Key</c>, keeping only the releases of the row's protocol; downloads go
/// through the links Prowlarr proxies.
/// </summary>
public sealed partial class ProwlarrSearchClient : IIndexerClient
{
    private readonly IHttpClientFactory _clients;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<ProwlarrSearchClient> _logger;

    /// <summary>Initialises a new instance of the <see cref="ProwlarrSearchClient"/> class.</summary>
    /// <param name="clients">Creates the indexer HTTP client.</param>
    /// <param name="secrets">The API key is registered here.</param>
    /// <param name="logger">Logs the search's path, never the key.</param>
    public ProwlarrSearchClient(IHttpClientFactory clients, ISecretRegistry secrets, ILogger<ProwlarrSearchClient> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(query);

        var settings = ProwlarrSettings.Read(NotificationSecrets.Read(indexer.Settings));
        _secrets.Register(settings.ApiKey);

        // Prowlarr binds arrays from repeated parameters: categories=3000&categories=3040.
        var parameters = new List<string>
        {
            "query=" + Uri.EscapeDataString(string.Concat(query.Artist, " ", query.Album)),
            "type=search",
            "limit=100",
        };
        parameters.AddRange(settings.Categories.Select(category => "categories=" + category.ToString(CultureInfo.InvariantCulture)));
        parameters.AddRange(settings.IndexerIds.Select(id => "indexerIds=" + id.ToString(CultureInfo.InvariantCulture)));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{settings.Url}/api/v1/search?{string.Join('&', parameters)}"));
        request.Headers.Add("X-Api-Key", settings.ApiKey);

        HttpResponseMessage response;

        try
        {
            response = await _clients.CreateClient(IndexerHttp.ClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new IndexerException("Prowlarr could not be reached.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new IndexerException($"Prowlarr answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releases = Parse(body, indexer);
            LogFound(_logger, releases.Count, indexer.Name);

            return releases;
        }
    }

    /// <inheritdoc />
    public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        var target = release.DownloadUrl ?? release.MagnetUrl
            ?? throw new IndexerException("The release has no URL to download.");

        if (target.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new IndexerDownload(null, target);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var url))
        {
            throw new IndexerException("The release's download URL is not an absolute address.");
        }

        using var response = await IndexerHttp.GetAsync(_clients.CreateClient(IndexerHttp.ClientName), url, cancellationToken).ConfigureAwait(false);

        if (IndexerHttp.TryGetMagnetRedirect(response, out var magnet))
        {
            return new IndexerDownload(null, magnet);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"Prowlarr answered {(int)response.StatusCode} to the download.");
        }

        return new IndexerDownload(await IndexerHttp.ReadCappedAsync(response, cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>Prowlarr's release array, the row's protocol only.</summary>
    /// <param name="body">The JSON answer.</param>
    /// <param name="indexer">The Prowlarr row.</param>
    public static IReadOnlyList<IndexerRelease> Parse(string body, Indexer indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);

        using var document = ParseJson(body);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new IndexerException("Prowlarr's answer is not a release list.");
        }

        var protocol = indexer.Protocol == DownloadProtocol.Usenet ? "usenet" : "torrent";
        var releases = new List<IndexerRelease>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!string.Equals(Text(item, "protocol"), protocol, StringComparison.OrdinalIgnoreCase)
                || Text(item, "title") is not { Length: > 0 } title
                || Text(item, "guid") is not { Length: > 0 } guid)
            {
                continue;
            }

            var seeders = Number(item, "seeders");
            var leechers = Number(item, "leechers");
            var source = Text(item, "indexer");

            releases.Add(new IndexerRelease(
                title,
                guid,
                Text(item, "downloadUrl"),
                Text(item, "magnetUrl"),
                Text(item, "infoHash")?.ToLowerInvariant(),
                LongNumber(item, "size"),
                DateTimeOffset.TryParse(Text(item, "publishDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published : null,
                seeders,
                seeders is { } s && leechers is { } l ? s + l : null,
                Number(item, "grabs"),
                Categories(item),
                null,
                Text(item, "infoUrl"),
                indexer.Protocol,
                indexer.Id,
                source is null ? indexer.Name : $"{indexer.Name} ({source})",
                null));
        }

        return releases;
    }

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new IndexerException("Prowlarr's answer was not JSON.", exception);
        }
    }

    private static List<int> Categories(JsonElement item) =>
        item.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
            ? [.. categories.EnumerateArray().Select(category => Number(category, "id")).OfType<int>()]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? LongNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Prowlarr returned {Count} releases for {Indexer}")]
    private static partial void LogFound(ILogger logger, int count, string indexer);
}
