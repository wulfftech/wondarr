using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Sources;

/// <summary>
/// What the decision engine knows about Soulseek peers: their delivery record and the ignore list.
/// </summary>
public interface ISoulseekUserService
{
    /// <summary>Records that a peer delivered a file that verified.</summary>
    /// <param name="username">The Soulseek username, in any case.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RecordSuccessAsync(string username, CancellationToken cancellationToken);

    /// <summary>Records that a peer failed us (offline, stalled, a file that did not verify).</summary>
    /// <param name="username">The Soulseek username, in any case.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RecordFailureAsync(string username, CancellationToken cancellationToken);

    /// <summary>Gets the reputation of the named peers. Unknown peers are absent from the result.</summary>
    /// <param name="usernames">The usernames to look up.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyDictionary<string, UserReputation>> GetReputationAsync(
        IEnumerable<string> usernames,
        CancellationToken cancellationToken);

    /// <summary>Gets the ignored usernames.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlySet<string>> GetIgnoredAsync(CancellationToken cancellationToken);

    /// <summary>Puts a peer on the ignore list, or takes it off.</summary>
    /// <param name="username">The Soulseek username, in any case.</param>
    /// <param name="ignored">Whether the peer is ignored.</param>
    /// <param name="reason">Why the peer is ignored, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetIgnoredAsync(string username, bool ignored, string? reason, CancellationToken cancellationToken);
}

/// <summary>
/// Backs the reputation term of the score (MATCHING_ENGINE §6.3) and the ignore list. Usernames are
/// unique case-insensitively — the column carries the SQLite <c>NOCASE</c> collation, so every lookup
/// here is a case-insensitive one.
/// </summary>
public sealed class SoulseekUserService : ISoulseekUserService
{
    /// <summary>How many failures are kept per peer. Older ones fall off the end.</summary>
    private const int MaxRecentFailures = 10;

    /// <summary>The window "failed twice this day" is measured over.</summary>
    private static readonly TimeSpan FailureWindow = TimeSpan.FromHours(24);

    private readonly WondarrDbContext _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="SoulseekUserService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="timeProvider">The clock used to date failures.</param>
    public SoulseekUserService(WondarrDbContext database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task RecordSuccessAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);

        var user = await FindAsync(username, cancellationToken).ConfigureAwait(false);
        user.Successes++;
        user.LastSuccessAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);

        var user = await FindAsync(username, cancellationToken).ConfigureAwait(false);
        user.Failures++;
        user.RecentFailures.Add(_timeProvider.GetUtcNow().UtcDateTime);

        // Only the newest ten are kept: that is all the 24-hour rule needs, and the list is a JSON column.
        if (user.RecentFailures.Count > MaxRecentFailures)
        {
            user.RecentFailures.RemoveRange(0, user.RecentFailures.Count - MaxRecentFailures);
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, UserReputation>> GetReputationAsync(
        IEnumerable<string> usernames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usernames);

        var wanted = usernames
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reputation = new Dictionary<string, UserReputation>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return reputation;
        }

        var stored = await _database.SoulseekUsers
            .AsNoTracking()
            .Where(user => wanted.Contains(user.Username))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var windowStart = _timeProvider.GetUtcNow().UtcDateTime - FailureWindow;

        foreach (var user in stored)
        {
            var recent = user.RecentFailures.Count(instant => instant >= windowStart);
            reputation[user.Username] = new UserReputation(user.Successes, user.Failures, recent);
        }

        return reputation;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetIgnoredAsync(CancellationToken cancellationToken)
    {
        var names = await _database.SoulseekUsers
            .AsNoTracking()
            .Where(user => user.Ignored)
            .Select(user => user.Username)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task SetIgnoredAsync(
        string username,
        bool ignored,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);

        var user = await FindAsync(username, cancellationToken).ConfigureAwait(false);
        user.Ignored = ignored;
        user.IgnoredReason = ignored ? reason : null;

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the tracked row for <paramref name="username"/>, creating and adding it when the peer is new.
    /// </summary>
    private async Task<SoulseekUser> FindAsync(string username, CancellationToken cancellationToken)
    {
        var user = await _database.SoulseekUsers
            .FirstOrDefaultAsync(candidate => candidate.Username == username, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            return user;
        }

        user = new SoulseekUser { Username = username };
        _database.SoulseekUsers.Add(user);

        return user;
    }
}
