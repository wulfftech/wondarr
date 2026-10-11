using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Wondarr.Core.HealthCheck;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Metadata.AcoustId;

/// <summary>
/// What the process last learned about the configured AcoustID client key, and how many times each
/// import has been put off because of it. A rejected key is a configuration problem, not the file's
/// fault: it raises a Health error, and an import that keeps waiting on it eventually fails with a
/// message that names the key instead of looping for ever.
/// </summary>
/// <remarks>
/// Held in memory on purpose. A restart forgets a rejection and the deferral counts, and the next
/// lookup learns them again; nothing about the key itself is stored (only a hash, to tell "the same
/// key" from "the user has since changed it").
/// </remarks>
public sealed class AcoustIdKeyState
{
    /// <summary>
    /// How many times an import is deferred because the key was rejected before it fails. Six
    /// deferrals at the import's 15-minute delay is about an hour and a half: long enough for the
    /// Health error to be seen and the key fixed (a fix needs no restart), short enough that a file
    /// does not wait for ever on a key that will never work.
    /// </summary>
    public const int MaxKeyDeferrals = 6;

    private readonly ConcurrentDictionary<long, int> _deferrals = new();
    private string? _rejectedKeyHash;

    /// <summary>Remembers that AcoustID refused <paramref name="clientKey"/>.</summary>
    /// <param name="clientKey">The key that was refused.</param>
    public void Rejected(string clientKey)
    {
        var hash = Hash(clientKey);
        var previous = Interlocked.Exchange(ref _rejectedKeyHash, hash);

        // A different key was refused than before: the counts belong to the old key's wait.
        if (previous is not null && !string.Equals(previous, hash, StringComparison.Ordinal))
        {
            _deferrals.Clear();
        }
    }

    /// <summary>How many items have a deferral count; for the tests, which prove the counts are pruned.</summary>
    internal int TrackedItemCount => _deferrals.Count;

    /// <summary>Remembers that AcoustID accepted a lookup, which clears the error and the deferral counts.</summary>
    public void Accepted()
    {
        Volatile.Write(ref _rejectedKeyHash, null);
        _deferrals.Clear();
    }

    /// <summary>
    /// Whether the key now configured is the one AcoustID refused. A key the user has changed since
    /// is not: it has not been tried yet.
    /// </summary>
    /// <param name="currentKey">The configured key, or <see langword="null"/> when there is none.</param>
    /// <returns><see langword="true"/> when the configured key is the refused one.</returns>
    public bool IsRejected(string? currentKey)
    {
        var rejected = Volatile.Read(ref _rejectedKeyHash);

        return rejected is not null
            && !string.IsNullOrWhiteSpace(currentKey)
            && string.Equals(rejected, Hash(currentKey), StringComparison.Ordinal);
    }

    /// <summary>Counts one more deferral of a queue item because of the key.</summary>
    /// <param name="queueItemId">The deferred item.</param>
    /// <returns>How many times this item has now been deferred for it.</returns>
    public int CountDeferral(long queueItemId) => _deferrals.AddOrUpdate(queueItemId, 1, (_, count) => count + 1);

    /// <summary>Forgets an item's deferral count, once the item has been decided.</summary>
    /// <param name="queueItemId">The item.</param>
    public void ForgetDeferrals(long queueItemId) => _deferrals.TryRemove(queueItemId, out _);

    private static string Hash(string clientKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientKey.Trim())));
}

/// <summary>
/// Reports a rejected AcoustID client key as an error: without a working key no download can be
/// confirmed by its fingerprint, so imports wait and then fail. The result clears itself once a lookup
/// succeeds or the key is changed.
/// </summary>
public sealed class AcoustIdKeyHealthCheck : IHealthCheck
{
    private readonly AcoustIdKeyState _state;
    private readonly IOptionsMonitor<AcoustIdOptions> _options;

    /// <summary>Initialises a new instance of the <see cref="AcoustIdKeyHealthCheck"/> class.</summary>
    /// <param name="state">What the process last learned about the key.</param>
    /// <param name="options">The configured key.</param>
    public AcoustIdKeyHealthCheck(AcoustIdKeyState state, IOptionsMonitor<AcoustIdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        _state = state;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(AcoustIdKeyHealthCheck);

    /// <inheritdoc />
    public Task<HealthReport> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(
            _state.IsRejected(_options.CurrentValue.ClientKey)
                ? new HealthReport(
                    Name,
                    HealthCheckResult.Error,
                    "AcoustID rejected the client key, so downloads cannot be confirmed by fingerprint and imports will fail. "
                    + "Fix it in Settings → General → Metadata services.",
                    null)
                : new HealthReport(Name, HealthCheckResult.Ok, "AcoustID client key accepted or not in use", null));
}
