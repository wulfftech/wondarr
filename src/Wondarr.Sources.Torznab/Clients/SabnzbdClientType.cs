using System.Text.Json;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// The SABnzbd download client type: the settings form, the rules those settings are stored under,
/// and the connection test that also checks the category exists and that Wondarr can see where it
/// finishes.
/// </summary>
public sealed class SabnzbdClientType : IDownloadClientType
{
    private readonly SabnzbdProxy _proxy;

    /// <summary>Initialises a new instance of the <see cref="SabnzbdClientType"/> class.</summary>
    /// <param name="proxy">The SABnzbd HTTP layer.</param>
    public SabnzbdClientType(SabnzbdProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        _proxy = proxy;
    }

    /// <inheritdoc />
    public string Type => "sabnzbd";

    /// <inheritdoc />
    public DownloadProtocol Protocol => DownloadProtocol.Usenet;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("host", "Host", "text", true, "The host SABnzbd listens on, for example 192.168.1.2", Target: true),
        new("port", "Port", "number", false, $"The port SABnzbd listens on; the default is {SabnzbdSettings.DefaultPort}", Target: true),
        new("useSsl", "Use SSL", "checkbox", false, "Whether SABnzbd is served over HTTPS", Target: true),
        new("urlBase", "URL base", "text", false, "SABnzbd's URL base, for example /sabnzbd", Advanced: true),
        new("apiKey", "API key", "password", true, "SABnzbd's API key (Config → General)", Secret: true),
        new("category", "Category", "text", false, $"The category Wondarr's jobs are filed under; create it in SABnzbd first. The default is {SabnzbdSettings.DefaultCategory}"),
        new("remotePathMappings", "Remote path mappings", "keyValueList", false, "The client's path on the left, the same path as Wondarr sees it on the right"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = SabnzbdSettings.FromJson(settings);
        var messages = new List<string>();

        if (parsed.Host.Length == 0)
        {
            messages.Add("A host is required.");
        }
        else if (parsed.Host.IndexOfAny(['/', '?', '#', '@', ' ']) >= 0)
        {
            // The key goes in the query: a host that carries a path, a query or credentials would move it.
            messages.Add("The host must be a name or an address only, without a scheme, path or port.");
        }

        if (parsed.Port is < 1 or > 65535)
        {
            messages.Add("The port must be between 1 and 65535.");
        }

        if (parsed.ApiKey.Length == 0)
        {
            messages.Add("An API key is required.");
        }

        if (parsed.Category.Contains('/') || parsed.Category.Contains('\\'))
        {
            messages.Add("The category must not contain '/' or '\\'.");
        }

        foreach (var mapping in parsed.RemotePathMappings)
        {
            if (!IsAbsolute(mapping.Remote))
            {
                messages.Add($"The remote path '{mapping.Remote}' of a remote path mapping must be absolute.");
            }

            if (!IsAbsolute(mapping.Local))
            {
                messages.Add($"The local path '{mapping.Local}' of a remote path mapping must be absolute.");
            }
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var parsed = SabnzbdSettings.FromJson(settings);

        try
        {
            await _proxy.GetVersionAsync(parsed, cancellationToken).ConfigureAwait(false);

            // The version needs no key; the settings do, so this is the key's test.
            var categories = await _proxy.GetConfigAsync(parsed, "categories", cancellationToken).ConfigureAwait(false);

            var category = FindCategory(categories, parsed.Category);

            if (category is not { } found)
            {
                return new ProviderTestResult(
                    false,
                    $"Create the category '{parsed.Category}' in SABnzbd's settings (Config → Categories); SABnzbd's API cannot create one.");
            }

            // The status gives the absolute folder SABnzbd really uses; a stock install's misc.complete_dir is
            // the relative "Downloads/complete", which is only the fallback.
            var completeDir = await _proxy.GetCompletedDownloadsFolderAsync(parsed, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(completeDir))
            {
                return new ProviderTestResult(false, "SABnzbd has no completed-downloads folder set (Config → Folders).");
            }

            var remote = CategoryFolder(completeDir, Text(found, "dir"));

            if (!IsAbsolute(remote))
            {
                return new ProviderTestResult(
                    false,
                    $"SABnzbd finishes '{parsed.Category}' jobs in {remote}, a relative path, and its status does not give the full one. Set an absolute completed-downloads folder in SABnzbd (Config → Folders).");
            }

            var local = RemotePathMapper.Map(remote, parsed.RemotePathMappings);

            if (!Directory.Exists(local))
            {
                return new ProviderTestResult(
                    false,
                    $"SABnzbd finishes '{parsed.Category}' jobs in {remote}, which Wondarr sees as {local} — that folder does not exist here; add a remote path mapping.");
            }

            return new ProviderTestResult(true, null);
        }
        catch (DownloadClientException exception)
        {
            return new ProviderTestResult(false, exception.Message);
        }
    }

    /// <summary>
    /// The folder SABnzbd finishes a category's jobs in, as SABnzbd sees it: the category's own folder when
    /// absolute, else that folder inside the completed-downloads folder, else the completed-downloads folder
    /// itself. <see langword="null"/> when the category is not there, when SABnzbd names no completed folder,
    /// when the result is relative (it cannot be told from here), or when the category's folder contains
    /// <c>*</c>: SABnzbd then puts a job's files straight into the category folder with no folder of their own,
    /// so the folder is shared by every job and is never a job's to delete.
    /// </summary>
    /// <param name="completeDir">
    /// The completed-downloads folder: <c>status.completedir</c> of SABnzbd's <c>fullstatus</c>, which is
    /// always absolute, or <c>misc.complete_dir</c> as the last resort, which may be relative.
    /// </param>
    /// <param name="categories">SABnzbd's <c>categories</c> settings.</param>
    /// <param name="categoryName">The category the client row files its jobs under.</param>
    public static string? CompletedFolder(string? completeDir, JsonElement categories, string categoryName)
    {
        if (FindCategory(categories, categoryName) is not { } category || string.IsNullOrWhiteSpace(completeDir))
        {
            return null;
        }

        var categoryDir = Text(category, "dir");

        if (categoryDir is not null && categoryDir.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        var folder = CategoryFolder(completeDir, categoryDir);

        return IsAbsolute(folder) ? folder : null;
    }

    /// <summary>
    /// The folder a category's jobs finish in: its own folder when absolute, else that folder inside
    /// the completed-downloads folder, else the completed-downloads folder itself. Joined with the
    /// separator SABnzbd's own paths use, since the path is on SABnzbd's machine.
    /// </summary>
    public static string CategoryFolder(string completeDir, string? categoryDir)
    {
        ArgumentNullException.ThrowIfNull(completeDir);

        if (string.IsNullOrWhiteSpace(categoryDir))
        {
            return completeDir;
        }

        if (IsAbsolute(categoryDir))
        {
            return categoryDir;
        }

        var separator = completeDir.Contains('\\') && !completeDir.Contains('/') ? '\\' : '/';

        return string.Concat(completeDir.TrimEnd('/', '\\'), separator.ToString(), categoryDir.Trim('/', '\\'));
    }

    private static JsonElement? FindCategory(JsonElement categories, string name)
    {
        if (categories.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var category in categories.EnumerateArray())
        {
            if (string.Equals(Text(category, "name"), name, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Absolute on either world: <c>/downloads</c>, <c>C:\downloads</c> or a UNC <c>\\server\share</c>.</summary>
    private static bool IsAbsolute(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (path.StartsWith('/') || path.StartsWith('\\'))
        {
            return true;
        }

        return path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '/' || path[2] == '\\');
    }
}
