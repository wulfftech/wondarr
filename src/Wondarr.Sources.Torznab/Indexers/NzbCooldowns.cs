using System.Diagnostics.CodeAnalysis;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Where NZBs come from, as far as a quota goes: one indexer row, and the name the release carries. A
/// Newznab row has one source (its own name); a Prowlarr row has one per upstream indexer, which is the
/// level an NZB quota is kept at (<see cref="IndexerRelease.IndexerName"/> reads "Row (Upstream)").
/// </summary>
/// <param name="IndexerId">The indexer row the release came from.</param>
/// <param name="Name">The release's indexer name, compared exactly.</param>
public readonly record struct NzbSource(long IndexerId, string Name)
{
    /// <summary>The source a release's NZB is downloaded from.</summary>
    /// <param name="release">The release.</param>
    public static NzbSource Of(IndexerRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new NzbSource(release.IndexerId, release.IndexerName);
    }
}

/// <summary>
/// The indexers that refused NZB downloads, and until when, so a search does not make a doomed download
/// from one that is out of its daily quota (the 2026-10-16 live run spent DOGnzb's 10 downloads and every
/// later search tried again). Memory only: a restart forgets.
/// </summary>
/// <remarks>The store does no I/O; one short lock covers every operation.</remarks>
public sealed class NzbCooldowns
{
    /// <summary>How long a refusal that came with no <c>Retry-After</c> cools the indexer down.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(1);

    /// <summary>The longest a cool-down lasts, however long the indexer asked to be left alone.</summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<NzbSource, DateTimeOffset> _until = [];
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="NzbCooldowns"/> class.</summary>
    /// <param name="time">The clock a cool-down is measured with.</param>
    public NzbCooldowns(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
    }

    /// <summary>
    /// Records that a source refused an NZB download. A source already cooling down keeps whichever end is
    /// later.
    /// </summary>
    /// <param name="source">The refusing source.</param>
    /// <param name="retryAfter">
    /// How long the indexer asked to be left alone, when its answer said so: the cool-down lasts that long
    /// (at most <see cref="MaxCooldown"/>), else <see cref="DefaultCooldown"/>. Zero or less counts as absent.
    /// </param>
    /// <returns>The instant the cool-down ends.</returns>
    public DateTimeOffset Start(NzbSource source, TimeSpan? retryAfter)
    {
        var length = retryAfter is { } asked && asked > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Min(asked.Ticks, MaxCooldown.Ticks))
            : DefaultCooldown;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var until = now + length;

            DropEnded(now);

            if (_until.TryGetValue(source, out var existing) && existing > until)
            {
                until = existing;
            }

            _until[source] = until;

            return until;
        }
    }

    /// <summary>Gets the end of a source's cool-down, when it is cooling down now.</summary>
    /// <param name="source">The source.</param>
    /// <param name="until">The instant the cool-down ends.</param>
    public bool TryGetUntil(NzbSource source, [NotNullWhen(true)] out DateTimeOffset? until)
    {
        lock (_gate)
        {
            DropEnded(_time.GetUtcNow());

            if (_until.TryGetValue(source, out var end))
            {
                until = end;

                return true;
            }
        }

        until = null;

        return false;
    }

    private void DropEnded(DateTimeOffset now)
    {
        foreach (var source in _until.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
        {
            _until.Remove(source);
        }
    }
}
