namespace Wondarr.Core.Metadata.Http;

/// <summary>Reads <c>Retry-After</c> in both of its spellings: delta-seconds and an HTTP-date.</summary>
public static class RetryAfterHeader
{
    /// <summary>The longest wait a <c>Retry-After</c> is believed for; a host asking for more is capped here.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(300);

    /// <summary>The wait the response asks for, or <see langword="null"/> when it asks for none.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="now">The current time, for the HTTP-date form.</param>
    public static TimeSpan? Read(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);

        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        var wait = header.Delta ?? (header.Date is { } date ? date - now : (TimeSpan?)null);

        return wait is { } value && value > TimeSpan.Zero
            ? (value > MaxWait ? MaxWait : value)
            : null;
    }
}

/// <summary>
/// Marks the requests made while a person is waiting on the page (the Add songs and Album lookups), as
/// against a background sync. An interactive request fails fast instead of queueing behind a long
/// <c>Retry-After</c>, and retries less. Ambient, so it reaches the handlers under the typed clients
/// without every client method growing a parameter.
/// </summary>
public static class InteractiveRequests
{
    /// <summary>The longest cooldown an interactive request waits out; a longer one fails at once.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    /// <summary>How many times an interactive request is retried; background requests are retried three times.</summary>
    public const int MaxRetries = 2;

    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>Gets a value indicating whether the current flow is waited on by a person.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Marks the current flow (and what it starts) as interactive until the scope is disposed.</summary>
    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;

        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
