using System.Text.Json;
using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The settings a Torznab or Newznab indexer row stores, parsed out of its JSON column: where the
/// API lives, the key it wants, and which categories to search. Both indexer types share the shape;
/// only Torznab also has <c>minimumSeeders</c>.
/// </summary>
/// <param name="Url">The indexer's base URL, for example Prowlarr's per-indexer endpoint.</param>
/// <param name="ApiPath">The path the API lives under, <c>/api</c> unless the indexer says otherwise.</param>
/// <param name="ApiKey">The key the indexer expects in the <c>apikey</c> parameter, or <see langword="null"/>.</param>
/// <param name="Categories">The newznab category ids to search.</param>
/// <param name="MinimumSeeders">Releases with fewer seeders are skipped; Torznab only.</param>
public sealed record IndexerEndpoint(
    string Url,
    string ApiPath,
    string? ApiKey,
    IReadOnlyList<int> Categories,
    int? MinimumSeeders)
{
    /// <summary>The API path an indexer row has when it does not say otherwise.</summary>
    public const string DefaultApiPath = "/api";

    /// <summary>The categories a row has when it does not say otherwise: 3000, music.</summary>
    public const string DefaultCategories = "3000";

    /// <summary>The minimum seeders a Torznab row has when it does not say otherwise.</summary>
    public const int DefaultMinimumSeeders = 1;

    /// <summary>Gets the API's base address: the URL with the API path appended, without a query.</summary>
    public Uri BaseUri =>
        new(Url.TrimEnd('/') + (ApiPath.Trim('/').Length == 0 ? string.Empty : "/" + ApiPath.Trim('/')),
            UriKind.Absolute);

    /// <summary>Reads the settings out of a settings object, filling in every default.</summary>
    /// <param name="settings">The row's settings, or a draft's.</param>
    public static IndexerEndpoint Read(JsonElement settings)
    {
        var url = String(settings, "url") ?? string.Empty;
        var apiPath = String(settings, "apiPath") is { Length: > 0 } path ? path : DefaultApiPath;
        var apiKey = String(settings, "apiKey");
        var categories = ParseCategories(String(settings, "categories") is { Length: > 0 } raw ? raw : DefaultCategories);
        var minimumSeeders = Int(settings, "minimumSeeders");

        return new IndexerEndpoint(url, apiPath, apiKey, categories, minimumSeeders);
    }

    /// <summary>
    /// Checks a settings object. Returns the human messages to show the user, empty when the settings
    /// are usable: the URL must be an absolute http(s) address and every category an integer.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    public static IReadOnlyList<string> Validate(JsonElement settings)
    {
        var endpoint = Read(settings);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(endpoint.Url) ||
            !Uri.TryCreate(endpoint.Url.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("The URL must be an absolute http or https address.");
        }

        var raw = String(settings, "categories");
        if (raw is { Length: > 0 })
        {
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _)))
            {
                failures.Add("The categories must be comma-separated numbers, for example 3000.");
            }
        }

        return failures;
    }

    /// <summary>Splits a comma-separated category list into the integers it holds.</summary>
    /// <param name="raw">The settings text, for example <c>3000,3040</c>.</param>
    private static List<int> ParseCategories(string raw)
    {
        var categories = new List<int>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var category))
            {
                categories.Add(category);
            }
        }

        return categories;
    }

    private static string? String(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String) &&
        int.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;
}
