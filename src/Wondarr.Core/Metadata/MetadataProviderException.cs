using System.Net;

namespace Wondarr.Core.Metadata;

/// <summary>
/// A metadata provider answered with something the client cannot recover from, after every retry.
/// </summary>
public sealed class MetadataProviderException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MetadataProviderException"/> class.</summary>
    /// <param name="provider">The provider key, for example <c>musicbrainz</c>.</param>
    /// <param name="statusCode">The status the provider answered with.</param>
    /// <param name="message">
    /// A description that names no header or key value: response headers can carry rate-limit bucket
    /// identifiers, and this message ends up in logs.
    /// </param>
    /// <param name="retryAfter">How long the provider asked to be left alone, when it said.</param>
    public MetadataProviderException(
        string provider,
        HttpStatusCode statusCode,
        string message,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        Provider = provider;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets how long the provider asked to be left alone, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether the provider is busy or down (a 5xx, 408 or 429), as against refusing the request (any other 4xx).</summary>
    public bool IsUnavailable =>
        (int)StatusCode >= 500 || StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Gets the provider key the request was made to.</summary>
    public string Provider { get; }

    /// <summary>Gets the status the provider answered with.</summary>
    public HttpStatusCode StatusCode { get; }
}
