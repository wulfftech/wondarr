using System.Globalization;
using System.Net.Http.Headers;

namespace Wondarr.Core.Metadata.LastFm;

/// <summary>
/// The one place Wondarr remembers that Last.fm asked to be left alone. The song page's client and
/// the import lists' page reads share it, because Last.fm limits the caller, not the feature: a 429
/// or error 29 seen by either holds every Last.fm request back until the wait is over.
/// </summary>
public interface ILastFmBackOff
{
    /// <summary>Gets how long Last.fm still wants to be left alone, or <see langword="null"/> when requests may go out.</summary>
    TimeSpan? Remaining { get; }

    /// <summary>Records a rate limit, honouring the response's <c>Retry-After</c> (default one minute, at most one hour).</summary>
    /// <param name="response">The response that said so, or <see langword="null"/> for the default wait.</param>
    void Trip(HttpResponseMessage? response);
}

/// <summary>The default <see cref="ILastFmBackOff"/>; register it as a singleton.</summary>
public sealed class LastFmBackOff : ILastFmBackOff
{
    /// <summary>How long requests are held back after a rate limit that named no wait.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait a <c>Retry-After</c> can ask for.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>Initialises a new instance of the <see cref="LastFmBackOff"/> class.</summary>
    /// <param name="timeProvider">The clock the wait runs on.</param>
    public LastFmBackOff(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public TimeSpan? Remaining
    {
        get
        {
            var now = _timeProvider.GetUtcNow();

            lock (_sync)
            {
                return _blockedUntil > now ? _blockedUntil - now : null;
            }
        }
    }

    /// <summary>Says a wait in words for a message: "about 3 minutes", "about a minute".</summary>
    /// <param name="wait">The wait to describe.</param>
    public static string Describe(TimeSpan wait)
    {
        var minutes = (int)Math.Ceiling(wait.TotalMinutes);

        return minutes <= 1
            ? "about a minute"
            : $"about {minutes.ToString(CultureInfo.InvariantCulture)} minutes";
    }

    /// <inheritdoc />
    public void Trip(HttpResponseMessage? response)
    {
        var now = _timeProvider.GetUtcNow();
        var wait = DefaultWait;
        RetryConditionHeaderValue? retryAfter = response?.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            wait = delta;
        }
        else if (retryAfter?.Date is { } date && date - now > TimeSpan.Zero)
        {
            wait = date - now;
        }

        wait = wait > MaxWait ? MaxWait : wait;

        lock (_sync)
        {
            var until = now + wait;

            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
    }
}
