// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Kept from the original: the `{base}/api?mode=…&apikey=…&output=json` request shape, the addfile
// upload with `cat` and `priority`, queue/history delete with `del_files` (and `archive=0` for a
// permanent history delete), the version and get_config calls, and the error reading (a JSON
// `status: false` with `error`, or a plain-text "error: …" body). Dropped: the username/password
// login, fullstatus, retry and Lidarr's HTTP abstraction; added: addurl, nzbname, get_files,
// delete_nzf, resume, the queue and history lookup by nzo_id, and Wondarr's typed errors that never
// carry the URL (it holds the API key).

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>One job in SABnzbd's queue.</summary>
/// <param name="JobId">The <c>nzo_id</c>.</param>
/// <param name="Name">The job's name (<c>filename</c>).</param>
/// <param name="Status">SABnzbd's status word.</param>
/// <param name="Percentage">How much is downloaded, 0 to 100.</param>
/// <param name="SizeBytes">The job's size.</param>
public sealed record SabnzbdQueueSlot(string JobId, string Name, string Status, double Percentage, long? SizeBytes);

/// <summary>One job in SABnzbd's history.</summary>
/// <param name="JobId">The <c>nzo_id</c>.</param>
/// <param name="Name">The job's name.</param>
/// <param name="Status">SABnzbd's status word.</param>
/// <param name="FailMessage">Why it failed, when it did.</param>
/// <param name="Storage">Where the finished files are, as SABnzbd sees it.</param>
/// <param name="SizeBytes">The job's size.</param>
public sealed record SabnzbdHistorySlot(string JobId, string Name, string Status, string? FailMessage, string? Storage, long? SizeBytes);

/// <summary>
/// The SABnzbd API's HTTP layer. Every call is a <c>GET</c> (or the <c>addfile</c> multipart
/// <c>POST</c>) to <c>{base}/api</c> with the key in the query, so only the path is ever logged and
/// no error message names the URL.
/// </summary>
public sealed partial class SabnzbdProxy
{
    /// <summary>The name the HTTP client is registered under.</summary>
    public const string HttpClientName = "wondarr-sabnzbd";

    /// <summary>SABnzbd's "paused" priority.</summary>
    private const int PausedPriority = -2;

    /// <summary>SABnzbd's "the category's default" priority.</summary>
    private const int DefaultPriority = -100;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<SabnzbdProxy> _logger;

    /// <summary>Initialises a new instance of the <see cref="SabnzbdProxy"/> class.</summary>
    /// <param name="httpClientFactory">Creates the HTTP client the calls go through.</param>
    /// <param name="secrets">Every API key used is registered, so a stray log line is redacted.</param>
    /// <param name="logger">The log sink; never sees a key.</param>
    public SabnzbdProxy(IHttpClientFactory httpClientFactory, ISecretRegistry secrets, ILogger<SabnzbdProxy> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Adds an NZB file; returns its <c>nzo_id</c>.</summary>
    public async Task<string> AddFileAsync(SabnzbdSettings settings, byte[] nzb, string title, bool paused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(nzb);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-nzb");
        content.Add(file, "name", FileName(title));

        using var document = await SendAsync(
            settings,
            "addfile",
            [("cat", settings.Category), ("priority", Priority(paused)), ("nzbname", title)],
            content,
            cancellationToken).ConfigureAwait(false);

        return JobIdOf(document.RootElement);
    }

    /// <summary>Has SABnzbd fetch an NZB from a URL; returns its <c>nzo_id</c>.</summary>
    public async Task<string> AddUrlAsync(SabnzbdSettings settings, string url, string title, bool paused, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        using var document = await SendAsync(
            settings,
            "addurl",
            [("name", url), ("cat", settings.Category), ("priority", Priority(paused)), ("nzbname", title)],
            null,
            cancellationToken).ConfigureAwait(false);

        return JobIdOf(document.RootElement);
    }

    /// <summary>A queued job's files: <c>mode=get_files</c>.</summary>
    public async Task<IReadOnlyList<UsenetFileInfo>> GetFilesAsync(SabnzbdSettings settings, string jobId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "get_files", [("value", jobId)], null, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<UsenetFileInfo>();

        foreach (var file in files.EnumerateArray())
        {
            var id = Text(file, "nzf_id");
            var name = Text(file, "filename");

            if (id is not null && name is not null)
            {
                list.Add(new UsenetFileInfo(id, name, Number(file, "bytes") ?? 0));
            }
        }

        return list;
    }

    /// <summary>Removes one file from a queued job: <c>mode=queue&amp;name=delete_nzf</c>.</summary>
    public async Task DeleteFileAsync(SabnzbdSettings settings, string jobId, string fileId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            settings,
            "queue",
            [("name", "delete_nzf"), ("value", jobId), ("value2", fileId)],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes a paused job: <c>mode=queue&amp;name=resume</c>.</summary>
    public async Task ResumeAsync(SabnzbdSettings settings, string jobId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "queue", [("name", "resume"), ("value", jobId)], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The job in the queue, or <see langword="null"/>.</summary>
    public async Task<SabnzbdQueueSlot?> FindInQueueAsync(SabnzbdSettings settings, string jobId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "queue", [("nzo_ids", jobId)], null, cancellationToken).ConfigureAwait(false);

        foreach (var slot in Slots(document.RootElement, "queue"))
        {
            if (Text(slot, "nzo_id") == jobId)
            {
                var megabytes = Decimal(slot, "mb");

                return new SabnzbdQueueSlot(
                    jobId,
                    Text(slot, "filename") ?? string.Empty,
                    Text(slot, "status") ?? string.Empty,
                    Decimal(slot, "percentage") ?? 0,
                    megabytes is { } mb ? (long)(mb * 1024 * 1024) : null);
            }
        }

        return null;
    }

    /// <summary>The job in the history, or <see langword="null"/>.</summary>
    public async Task<SabnzbdHistorySlot?> FindInHistoryAsync(SabnzbdSettings settings, string jobId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "history", [("nzo_ids", jobId)], null, cancellationToken).ConfigureAwait(false);

        foreach (var slot in Slots(document.RootElement, "history"))
        {
            if (Text(slot, "nzo_id") == jobId)
            {
                return new SabnzbdHistorySlot(
                    jobId,
                    Text(slot, "name") ?? string.Empty,
                    Text(slot, "status") ?? string.Empty,
                    Text(slot, "fail_message") is { Length: > 0 } message ? message : null,
                    Text(slot, "storage") is { Length: > 0 } storage ? storage : null,
                    Number(slot, "bytes"));
            }
        }

        return null;
    }

    /// <summary>Deletes a queued job: <c>mode=queue&amp;name=delete</c>.</summary>
    public async Task RemoveFromQueueAsync(SabnzbdSettings settings, string jobId, bool deleteFiles, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            settings,
            "queue",
            [("name", "delete"), ("value", jobId), ("del_files", deleteFiles ? "1" : "0")],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a finished job for good (<c>archive=0</c>): <c>mode=history&amp;name=delete</c>.</summary>
    public async Task RemoveFromHistoryAsync(SabnzbdSettings settings, string jobId, bool deleteFiles, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            settings,
            "history",
            [("name", "delete"), ("value", jobId), ("del_files", deleteFiles ? "1" : "0"), ("archive", "0")],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>SABnzbd's version; <c>mode=version</c> needs no key.</summary>
    public async Task<string> GetVersionAsync(SabnzbdSettings settings, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "version", [], null, cancellationToken).ConfigureAwait(false);

        return Text(document.RootElement, "version") ?? string.Empty;
    }

    /// <summary>One section of SABnzbd's settings (<c>misc</c>, <c>categories</c>); proves the key.</summary>
    public async Task<JsonElement> GetConfigAsync(SabnzbdSettings settings, string section, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(settings, "get_config", [("section", section)], null, cancellationToken).ConfigureAwait(false);

        return document.RootElement.TryGetProperty("config", out var config) && config.TryGetProperty(section, out var value)
            ? value.Clone()
            : default;
    }

    /// <summary>Sends one API call and returns its JSON answer, or throws a <see cref="DownloadClientException"/>.</summary>
    private async Task<JsonDocument> SendAsync(
        SabnzbdSettings settings,
        string mode,
        IReadOnlyList<(string Name, string Value)> parameters,
        HttpContent? body,
        CancellationToken cancellationToken)
    {
        _secrets.Register(settings.ApiKey);

        var query = new List<string> { "mode=" + mode };
        query.AddRange(parameters.Select(parameter => string.Concat(parameter.Name, "=", Uri.EscapeDataString(parameter.Value))));
        query.Add("apikey=" + Uri.EscapeDataString(settings.ApiKey));
        query.Add("output=json");

        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, string.Concat(settings.ApiUrl, "?", string.Join("&", query)))
        {
            Content = body,
        };

        LogRequest(_logger, mode);

        HttpResponseMessage response;

        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadClientException("Failed to connect to SABnzbd, check the host and port.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadClientException("SABnzbd did not answer in time.", exception);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadClientException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"SABnzbd answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            // SABnzbd answers some errors in plain text: "error: API Key Incorrect".
            if (text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                throw Refused(text.Replace("error:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim());
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException exception)
            {
                throw new DownloadClientException("SABnzbd's answer was not JSON; is this SABnzbd?", exception);
            }

            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    || root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False))
            {
                var message = Text(root, "error") ?? "the call failed";
                document.Dispose();
                throw Refused(message);
            }

            return document;
        }
    }

    private static DownloadClientException Refused(string error) =>
        error.Contains("API Key", StringComparison.OrdinalIgnoreCase)
            ? new DownloadClientException("SABnzbd refused the API key.")
            : new DownloadClientException(string.Concat("SABnzbd: ", error));

    private static string JobIdOf(JsonElement root)
    {
        if (root.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        throw new DownloadClientException("SABnzbd accepted the NZB but returned no job id.");
    }

    private static string Priority(bool paused) =>
        (paused ? PausedPriority : DefaultPriority).ToString(CultureInfo.InvariantCulture);

    /// <summary>The upload's file name: the title with characters a file name cannot carry replaced.</summary>
    private static string FileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. title.Select(character => invalid.Contains(character) || character is '/' or '\\' or ':' ? '_' : character)]);

        return name + ".nzb";
    }

    private static List<JsonElement> Slots(JsonElement root, string section) =>
        root.TryGetProperty(section, out var container)
        && container.TryGetProperty("slots", out var slots)
        && slots.ValueKind == JsonValueKind.Array
            ? [.. slots.EnumerateArray()]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>A number SABnzbd may send as a JSON number or as a string ("123.4").</summary>
    private static double? Decimal(JsonElement element, string name) =>
        Text(element, name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? Number(JsonElement element, string name) =>
        Decimal(element, name) is { } value ? (long)value : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "SABnzbd api mode={Mode}")]
    private static partial void LogRequest(ILogger logger, string mode);
}
