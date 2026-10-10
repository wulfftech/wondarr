using System.Text.Json;

namespace Wondarr.Core.Metadata;

/// <summary>
/// What a search found, and which providers failed to answer while it ran. A search that has results
/// from one provider is still worth showing when the other was busy.
/// </summary>
/// <typeparam name="T">The kind of hit.</typeparam>
/// <param name="Items">The hits, best first.</param>
/// <param name="FailedProviders">The provider keys that did not answer, for example <c>musicbrainz</c>; empty when all did.</param>
public sealed record PartialSearch<T>(IReadOnlyList<T> Items, IReadOnlyList<string> FailedProviders)
{
    /// <summary>Gets a value indicating whether any provider failed to answer.</summary>
    public bool IsPartial => FailedProviders.Count > 0;
}

/// <summary>
/// Every provider a search needed failed to answer, so there is nothing to show. The API reports it
/// as a 503, not a 500: the providers are busy, Wondarr is fine.
/// </summary>
public sealed class ProvidersUnavailableException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ProvidersUnavailableException"/> class.</summary>
    /// <param name="providers">The provider keys that did not answer.</param>
    /// <param name="inner">The failure of the first provider.</param>
    public ProvidersUnavailableException(IReadOnlyList<string> providers, Exception? inner)
        : base($"No metadata provider answered ({string.Join(", ", providers ?? [])}).", inner)
    {
        Providers = providers ?? [];
        RetryAfter = (inner as MetadataProviderException)?.RetryAfter;
    }

    /// <summary>Gets how long the first provider asked to be left alone, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets the provider keys that did not answer.</summary>
    public IReadOnlyList<string> Providers { get; }
}

/// <summary>Provider keys, and the test for "this exception means the provider did not answer".</summary>
public static class ProviderKeys
{
    /// <summary>The provider key MusicBrainz is reported under.</summary>
    public const string MusicBrainz = "musicbrainz";

    /// <summary>The provider key Deezer is reported under.</summary>
    public const string Deezer = "deezer";

    /// <summary>
    /// Whether an exception is a provider that did not answer (an error status after every retry, no
    /// connection, a timeout) as opposed to a bug or the caller giving up.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    /// <param name="cancellationToken">The caller's token: a cancelled caller is not a failed provider.</param>
    public static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            MetadataProviderException => true,
            HttpRequestException => true,

            // A provider that answers with an HTML error page is as unavailable as one that does not answer.
            JsonException => true,
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };
    }
}
