using System.Net;
using System.Net.Http.Headers;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// An indexer could not be reached or its answer could not be used. The message is safe to show the
/// user: it never carries the request URL, whose query holds the API key.
/// </summary>
public sealed class IndexerException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="IndexerException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    public IndexerException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="IndexerException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    /// <param name="inner">The failure that caused this one.</param>
    public IndexerException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="IndexerException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    /// <param name="statusCode">The status the indexer answered with.</param>
    /// <param name="retryAfter">The answer's <c>Retry-After</c> header, when it had one.</param>
    public IndexerException(string message, HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the status the indexer answered with, when the failure is an answer rather than a
    /// transport error. A 429 or 403 to an NZB download is how an indexer says its quota is spent.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets the answer's <c>Retry-After</c> header: how long the indexer asked to be left alone.</summary>
    public RetryConditionHeaderValue? RetryAfter { get; }
}
