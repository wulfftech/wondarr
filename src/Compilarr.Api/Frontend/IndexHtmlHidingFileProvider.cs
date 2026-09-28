using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Compilarr.Api.Frontend;

/// <summary>
/// Hides <c>index.html</c> from the file provider the static-file middleware uses, so the raw file
/// (with its <c>__URL_BASE__</c> placeholder) can never be served: it only leaves the server through
/// <see cref="IndexHtmlProvider"/> and the SPA fallback.
/// </summary>
internal sealed class IndexHtmlHidingFileProvider : IFileProvider
{
    private const string IndexHtmlPath = "index.html";

    private readonly IFileProvider _inner;

    /// <summary>Initialises a new instance of the <see cref="IndexHtmlHidingFileProvider"/> class.</summary>
    public IndexHtmlHidingFileProvider(IFileProvider inner) => _inner = inner;

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath) => _inner.GetDirectoryContents(subpath);

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath) =>
        IsIndexHtml(subpath) ? new NotFoundFileInfo(IndexHtmlPath) : _inner.GetFileInfo(subpath);

    /// <inheritdoc />
    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    private static bool IsIndexHtml(string subpath) =>
        subpath.AsSpan().TrimStart('/').Equals(IndexHtmlPath, StringComparison.OrdinalIgnoreCase);
}
