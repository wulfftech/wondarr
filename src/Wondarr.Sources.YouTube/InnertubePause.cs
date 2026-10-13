namespace Wondarr.Sources.YouTube;

/// <summary>
/// The shared "do not ask YouTube before" time (R-025). When InnerTube answers 429 or 503 the pause is
/// set from <c>Retry-After</c>, and every YouTube search waits for it, whichever song asked. A
/// singleton: the typed client is transient, so the state cannot live in it.
/// </summary>
public sealed class InnertubePause
{
    /// <summary>How long the pause lasts when the answer carries no usable <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(60);

    /// <summary>The longest pause honoured, so an absurd <c>Retry-After</c> cannot park the source for days.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    /// <summary>Pauses until <paramref name="duration"/> after <paramref name="now"/>, never shortening a pause already set.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="duration">How long YouTube asked to be left alone; <see langword="null"/> or non-positive for the default.</param>
    /// <returns>How long the pause now lasts from <paramref name="now"/>.</returns>
    public TimeSpan Set(DateTimeOffset now, TimeSpan? duration)
    {
        var length = duration is { } asked && asked > TimeSpan.Zero ? asked : DefaultPause;

        if (length > MaxPause)
        {
            length = MaxPause;
        }

        lock (_lock)
        {
            var until = now + length;

            if (until > _notBefore)
            {
                _notBefore = until;
            }

            return _notBefore - now;
        }
    }

    /// <summary>How much longer searches must wait, or <see langword="null"/> when they may go ahead.</summary>
    /// <param name="now">The current time.</param>
    public TimeSpan? Remaining(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _notBefore > now ? _notBefore - now : null;
        }
    }
}
