using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The NZBs a search downloaded to read their file lists, kept so the grab of the same post does not
/// download it a second time: an indexer counts every NZB download against a daily quota (a free
/// DOGnzb account gets 10), and a grabbed post used to cost two. Memory only: a restart downloads
/// again. Bounded by entry count, by total size and by age; the oldest entry goes first.
/// </summary>
/// <remarks>
/// The cache does no I/O, so one short lock covers every operation and nothing waits on a download
/// while holding it. The bytes are the downloaded array itself, not a copy: callers must not change it.
/// </remarks>
public sealed class NzbCache
{
    /// <summary>The most NZBs the cache holds.</summary>
    public const int DefaultMaxEntries = 64;

    /// <summary>The most bytes of NZB the cache holds in all.</summary>
    public const long DefaultMaxBytes = 16L * 1024 * 1024;

    /// <summary>How long an NZB stays in the cache.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(6);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);

    // Insertion order, oldest first. Every entry lives equally long, so the oldest is also the first to expire.
    private readonly LinkedList<Entry> _oldestFirst = new();
    private readonly TimeProvider _time;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly TimeSpan _lifetime;
    private long _bytes;

    /// <summary>Initialises a new instance of the <see cref="NzbCache"/> class with the default bounds.</summary>
    /// <param name="time">The clock an entry's age is measured with.</param>
    public NzbCache(TimeProvider time)
        : this(time, DefaultMaxEntries, DefaultMaxBytes, DefaultLifetime)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="NzbCache"/> class.</summary>
    /// <param name="time">The clock an entry's age is measured with.</param>
    /// <param name="maxEntries">The most NZBs held.</param>
    /// <param name="maxBytes">The most bytes held in all.</param>
    /// <param name="lifetime">How long an NZB is kept.</param>
    public NzbCache(TimeProvider time, int maxEntries, long maxBytes, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        _time = time;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _lifetime = lifetime;
    }

    /// <summary>Gets how many NZBs are held (expired ones not yet dropped included).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>Gets how many bytes of NZB are held (expired ones not yet dropped included).</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// The cache key of a release's NZB: the indexer and the download link, or the guid when the
    /// release has no link. The link carries the API key, so only a hash of it is kept; the key is
    /// still never to be logged. The hashed text says which of the two it is, so a guid that happens
    /// to equal some release's link can never collide with it.
    /// </summary>
    /// <param name="indexerId">The indexer row the release came from.</param>
    /// <param name="downloadUrl">The release's download link.</param>
    /// <param name="releaseId">The release's guid.</param>
    public static string KeyOf(long indexerId, string? downloadUrl, string releaseId)
    {
        var identity = string.IsNullOrWhiteSpace(downloadUrl) ? string.Concat("g:", releaseId) : string.Concat("l:", downloadUrl);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        return string.Concat(indexerId.ToString(CultureInfo.InvariantCulture), ":", hash);
    }

    /// <summary>Keeps an NZB, replacing one under the same key and evicting the oldest while over a bound.</summary>
    /// <param name="key">The key from <see cref="KeyOf"/>.</param>
    /// <param name="nzb">The downloaded NZB. An NZB bigger than the whole cache is not kept.</param>
    public void Put(string key, byte[] nzb)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nzb);

        if (nzb.Length > _maxBytes)
        {
            return;
        }

        lock (_gate)
        {
            DropExpired();

            if (_byKey.TryGetValue(key, out var existing))
            {
                Remove(existing);
            }

            _byKey[key] = _oldestFirst.AddLast(new Entry(key, nzb, _time.GetTimestamp()));
            _bytes += nzb.Length;

            while (_byKey.Count > _maxEntries || _bytes > _maxBytes)
            {
                Remove(_oldestFirst.First!);
            }
        }
    }

    /// <summary>Gets the NZB kept under a key, unless it has expired or been evicted.</summary>
    /// <param name="key">The key from <see cref="KeyOf"/>.</param>
    /// <param name="nzb">The kept NZB.</param>
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? nzb)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            DropExpired();

            if (_byKey.TryGetValue(key, out var node))
            {
                nzb = node.Value.Content;

                return true;
            }
        }

        nzb = null;

        return false;
    }

    /// <summary>
    /// Drops the NZB kept under a key, but only if what is kept is still this very array (compared by
    /// reference, not by content): whoever downloaded an NZB can take back its own copy, never one that
    /// another read has put there since.
    /// </summary>
    /// <param name="key">The key from <see cref="KeyOf"/>.</param>
    /// <param name="nzb">The array the caller put, or was handed by the download that put it.</param>
    /// <returns><see langword="true"/> when the entry was dropped.</returns>
    public bool Remove(string key, byte[] nzb)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nzb);

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Content, nzb))
            {
                Remove(node);

                return true;
            }
        }

        return false;
    }

    private void DropExpired()
    {
        var now = _time.GetTimestamp();

        while (_oldestFirst.First is { } oldest && _time.GetElapsedTime(oldest.Value.Added, now) >= _lifetime)
        {
            Remove(oldest);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _oldestFirst.Remove(node);
        _byKey.Remove(node.Value.Key);
        _bytes -= node.Value.Content.Length;
    }

    // A class, not a record: a record's ToString would print its members.
    private sealed class Entry(string key, byte[] content, long added)
    {
        public string Key { get; } = key;

        public byte[] Content { get; } = content;

        public long Added { get; } = added;
    }
}
