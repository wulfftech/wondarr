// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Frontend/Mappers/UrlBaseReplacementResourceMapperBase.cs, GPL-3.0.
// Adapted for Compilarr: reads index.html through an IFileProvider, caches per URL base value and
// re-reads whenever the file's last-write time changes, so a rebuilt SPA is picked up without a restart.

using Compilarr.Core.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Compilarr.Api.Frontend;

/// <summary>
/// Serves the built SPA's <c>index.html</c> with every <c>__URL_BASE__</c> replaced by the
/// configured URL base. The replacement happens here rather than in the Vite build so one build
/// works behind any URL base; relative asset URLs then resolve under that base on every route.
/// </summary>
public sealed partial class IndexHtmlProvider
{
    /// <summary>The placeholder Vite leaves in <c>index.html</c>.</summary>
    public const string UrlBaseToken = "__URL_BASE__";

    private const string IndexHtmlPath = "index.html";

    private readonly IFileProvider _webRoot;
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;
    private readonly ILogger<IndexHtmlProvider> _logger;
    private readonly Lock _gate = new();

    private string? _cachedUrlBase;
    private DateTimeOffset _cachedLastModified;
    private string? _cachedHtml;

    /// <summary>Initialises a new instance of the <see cref="IndexHtmlProvider"/> class.</summary>
    public IndexHtmlProvider(
        IFileProvider webRoot,
        IOptionsMonitor<ServerOptions> serverOptions,
        ILogger<IndexHtmlProvider> logger)
    {
        _webRoot = webRoot;
        _serverOptions = serverOptions;
        _logger = logger;
    }

    /// <summary>
    /// Returns <c>index.html</c> with the URL base substituted, or <see langword="null"/> when the
    /// file does not exist (the SPA has not been built).
    /// </summary>
    public string? GetIndexHtml()
    {
        var urlBase = _serverOptions.CurrentValue.UrlBase;
        var file = _webRoot.GetFileInfo(IndexHtmlPath);

        if (!file.Exists)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cachedHtml is not null &&
                _cachedLastModified == file.LastModified &&
                string.Equals(_cachedUrlBase, urlBase, StringComparison.Ordinal))
            {
                return _cachedHtml;
            }
        }

        string content;

        try
        {
            using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file can disappear between the existence check and the read.
            LogIndexHtmlReadFailed(_logger, exception);
            return null;
        }

        var html = content.Replace(UrlBaseToken, urlBase, StringComparison.Ordinal);

        lock (_gate)
        {
            _cachedUrlBase = urlBase;
            _cachedLastModified = file.LastModified;
            _cachedHtml = html;
        }

        return html;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the web UI's index.html")]
    private static partial void LogIndexHtmlReadFailed(ILogger logger, Exception exception);
}
