using System.Net;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Importing;

/// <summary>Downloads the cover an album context points at, for embedding at import.</summary>
public interface ICoverFetcher
{
    /// <summary>Fetches one cover image.</summary>
    /// <param name="url">The image URL, or <see langword="null"/> when the context has no cover.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The image bytes, or <see langword="null"/> when there is no usable cover.</returns>
    Task<byte[]?> FetchAsync(string? url, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the front cover with the <c>wondarr-cover</c> HTTP client. A cover is optional: anything
/// that is not a JPEG or PNG, is too large, or simply fails is reported as "no cover" rather than
/// failing the import (LIBRARY_OUTPUT §7.4).
/// </summary>
public sealed partial class CoverFetcher(IHttpClientFactory httpClientFactory, ILogger<CoverFetcher> logger)
    : ICoverFetcher
{
    /// <summary>The name the cover client is registered under.</summary>
    public const string ClientName = "wondarr-cover";

    /// <summary>How long one image download may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The largest image Wondarr embeds, in bytes.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The PNG signature: <c>\x89PNG\r\n\x1a\n</c>.</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The JPEG signature: <c>FF D8 FF</c>.</summary>
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    /// <inheritdoc />
    public async Task<byte[]?> FetchAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogStatusFailed(uri, response.StatusCode);

                return null;
            }

            if (!IsImageContentType(response.Content.Headers.ContentType?.MediaType))
            {
                LogFailed(uri, "the response is not a JPEG or PNG");

                return null;
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                LogFailed(uri, "the image is larger than the limit");

                return null;
            }

            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);

            if (bytes is null)
            {
                LogFailed(uri, "the image is larger than the limit");

                return null;
            }

            // The declared type is only a claim: what gets embedded is what the bytes say.
            if (!IsJpeg(bytes) && !IsPng(bytes))
            {
                LogFailed(uri, "the bytes are not a JPEG or PNG");

                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A cover is a nicety: an unreachable host must not stop the file being imported.
            LogFetchFailed(uri, exception);

            return null;
        }
    }

    /// <summary>Reads at most <see cref="MaxBytes"/>, or <see langword="null"/> when the body is longer.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>Whether the declared media type is one of the two formats an embed accepts.</summary>
    private static bool IsImageContentType(string? mediaType) =>
        string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the bytes start with the JPEG signature.</summary>
    private static bool IsJpeg(ReadOnlySpan<byte> bytes) => bytes.StartsWith(JpegMagic);

    /// <summary>Whether the bytes start with the PNG signature.</summary>
    private static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngMagic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No cover art from {Url}: {Reason}")]
    private partial void LogFailed(Uri url, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No cover art from {Url}: the server answered {StatusCode}")]
    private partial void LogStatusFailed(Uri url, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No cover art from {Url}")]
    private partial void LogFetchFailed(Uri url, Exception exception);
}
