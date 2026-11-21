using Wondarr.Sources.YouTube;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// An oEmbed client that answers from a function, and keeps count: how many lookups were made, which videos, and the
/// most that ran at the same time. No network.
/// </summary>
internal sealed class StubOEmbedClient(Func<string, YouTubeOEmbed?> answer) : IYouTubeOEmbedClient
{
    private readonly object _gate = new();
    private int _running;

    /// <summary>Gets the video ids looked up, in the order the lookups began.</summary>
    public List<string> Lookups { get; } = [];

    /// <summary>Gets the most lookups that ran at once.</summary>
    public int MaxConcurrent { get; private set; }

    /// <summary>Gets or sets how long each lookup takes, so that concurrent ones overlap.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Gets or sets an exception the lookup throws instead of answering.</summary>
    public Exception? Failure { get; set; }

    public async Task<YouTubeOEmbed?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Lookups.Add(videoId);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return answer(videoId);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }
}
