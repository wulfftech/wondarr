using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// The Soulseek source behind <see cref="ISourceProvider"/>: it runs the query strategy of
/// <see cref="SoulseekQueryBuilder"/> through the budget-enforcing <see cref="ISlskdSearchRunner"/>,
/// maps every answer into <see cref="Candidate"/>s, and grabs, follows and cancels a chosen candidate
/// through <see cref="ISlskdDownloads"/>.
/// </summary>
/// <remarks>
/// It makes no accept/reject decisions — that is the decision engine's job — and it enforces no
/// download timeouts, which the queue tracker in Core owns. Searches never bypass
/// <see cref="ISlskdSearchRunner"/>: the network budget lives there.
/// </remarks>
public sealed partial class SoulseekSourceProvider : ISourceProvider
{
    /// <summary>The grab handle is Wondarr's own JSON, in the web casing slskd's payloads use.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISlskdSearchRunner _runner;
    private readonly ISlskdDownloads _downloads;
    private readonly SlskdStatus _status;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly ILogger<SoulseekSourceProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="SoulseekSourceProvider"/> class.</summary>
    /// <param name="runner">The only way a search is submitted, and the only place the budget is taken.</param>
    /// <param name="downloads">The only way a file is enqueued, followed and cancelled.</param>
    /// <param name="status">The supervisor's last snapshot of the bundled slskd.</param>
    /// <param name="options">Soulseek settings: the mode, the credentials and nothing else here.</param>
    /// <param name="logger">One Warning per query that failed.</param>
    public SoulseekSourceProvider(
        ISlskdSearchRunner runner,
        ISlskdDownloads downloads,
        SlskdStatus status,
        IOptionsMonitor<SoulseekOptions> options,
        ILogger<SoulseekSourceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _downloads = downloads;
        _status = status;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceType => SourceTypes.Soulseek;

    /// <inheritdoc />
    public Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (options.Mode == SoulseekMode.External)
        {
            return Task.FromResult<(bool, string?)>((false, "External slskd mode arrives in Phase 5"));
        }

        if (!options.HasCredentials)
        {
            return Task.FromResult<(bool, string?)>((false, "Soulseek is not configured"));
        }

        var snapshot = _status.Current;

        if (snapshot.State != SlskdState.Running || !snapshot.IsReachable)
        {
            return Task.FromResult<(bool, string?)>((false, "slskd is not running"));
        }

        if (!snapshot.IsLoggedIn)
        {
            return Task.FromResult<(bool, string?)>((false, "Soulseek: not logged in"));
        }

        return Task.FromResult<(bool, string?)>((true, null));
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (available, reason) = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        if (!available)
        {
            return new SourceSearchResult([], [], reason);
        }

        var queries = SoulseekQueryBuilder.Build(request);
        var submitted = new List<string>();
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? firstError = null;

        foreach (var query in queries)
        {
            try
            {
                var result = await _runner.RunAsync(query, cancellationToken).ConfigureAwait(false);
                submitted.Add(query);

                foreach (var candidate in SoulseekCandidateMapper.Map(result.Responses, query))
                {
                    if (seen.Add(candidate.BlocklistKey))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
            catch (SlskdSearchRejectedException)
            {
                // slskd's own 429: another client is searching the same account, so the rest of the
                // sequence would be refused too. Keep what the earlier queries found.
                return new SourceSearchResult(candidates, submitted, "slskd refused a search (too many in flight)");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstError ??= exception.Message;
                LogQueryFailed(_logger, query, exception);
                continue;
            }

            if (request.IsPoolGoodEnough?.Invoke(candidates) == true)
            {
                break;
            }
        }

        if (submitted.Count == 0 && firstError is not null)
        {
            return new SourceSearchResult([], [], $"Soulseek search failed: {firstError}");
        }

        return new SourceSearchResult(candidates, submitted);
    }

    /// <inheritdoc />
    public async Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.SourceType, SourceTypes.Soulseek, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a Soulseek candidate (was {candidate.SourceType})", nameof(candidate));
        }

        if (string.IsNullOrWhiteSpace(candidate.Provider))
        {
            throw new ArgumentException("A Soulseek candidate needs the peer it came from", nameof(candidate));
        }

        if (candidate.SizeBytes is null)
        {
            throw new ArgumentException("A Soulseek candidate needs the size it was advertised with", nameof(candidate));
        }

        var grab = await _downloads
            .EnqueueAsync(
                candidate.Provider,
                candidate.RemotePath,
                candidate.SizeBytes.Value,
                destination,
                externalId: destination,
                cancellationToken)
            .ConfigureAwait(false);

        return new GrabHandle(SourceTypes.Soulseek, JsonSerializer.Serialize(grab, SerializerOptions));
    }

    /// <inheritdoc />
    public async Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grab = Read(handle);
        var status = await _downloads.GetStatusAsync(grab, cancellationToken).ConfigureAwait(false);

        return Map(status);
    }

    /// <inheritdoc />
    public async Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grab = Read(handle);

        try
        {
            await _downloads.CancelAsync(grab, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Cancelling is idempotent: a grab slskd has already dropped is a grab that is cancelled.
            LogCancelFailed(_logger, grab.TransferId, exception);
        }
    }

    /// <summary>Reads the grab Wondarr's own handle carries.</summary>
    private static SlskdGrab Read(GrabHandle handle)
    {
        if (!string.Equals(handle.SourceType, SourceTypes.Soulseek, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a Soulseek grab handle (was {handle.SourceType})", nameof(handle));
        }

        return JsonSerializer.Deserialize<SlskdGrab>(handle.Value, SerializerOptions)
            ?? throw new ArgumentException("The grab handle does not carry a Soulseek grab", nameof(handle));
    }

    /// <summary>Maps slskd's transfer state onto the source-independent one.</summary>
    private static DownloadStatus Map(SlskdDownloadStatus status)
    {
        var state = status.State;
        var progress = status.PercentComplete / 100d;

        if (state.HasFlag(SlskdTransferState.Completed))
        {
            if (state.HasFlag(SlskdTransferState.Succeeded))
            {
                // slskd reports the local path only when the file is really there; without it a
                // "succeeded" transfer is a failure of the download folder, not of the peer.
                return status.LocalPath is not null
                    ? new DownloadStatus(DownloadState.Completed, 1, status.BytesTransferred, status.Size, null, null, status.LocalPath)
                    : new DownloadStatus(DownloadState.Failed, progress, status.BytesTransferred, status.Size, null, status.Error, null);
            }

            if (state.HasFlag(SlskdTransferState.Cancelled))
            {
                return new DownloadStatus(DownloadState.Cancelled, progress, status.BytesTransferred, status.Size);
            }

            return new DownloadStatus(
                DownloadState.Failed,
                progress,
                status.BytesTransferred,
                status.Size,
                null,
                FailureMessage(state, status.Error),
                null);
        }

        if (state.HasFlag(SlskdTransferState.InProgress))
        {
            return new DownloadStatus(DownloadState.Downloading, progress, status.BytesTransferred, status.Size);
        }

        if (state.HasFlag(SlskdTransferState.Queued))
        {
            return state.HasFlag(SlskdTransferState.Remotely)
                ? new DownloadStatus(DownloadState.RemotelyQueued, progress, status.BytesTransferred, status.Size, status.PlaceInQueue)
                : new DownloadStatus(DownloadState.Queued, progress, status.BytesTransferred, status.Size);
        }

        // Requested, Initializing — or a flag slskd added that Wondarr does not model yet: the
        // transfer is ours and nothing has gone wrong, so it is queued until told otherwise.
        return new DownloadStatus(DownloadState.Queued, progress, status.BytesTransferred, status.Size);
    }

    /// <summary>The failure message for a stopped transfer: which way it failed, and slskd's own words.</summary>
    private static string? FailureMessage(SlskdTransferState state, string? error)
    {
        string? reason = null;

        if (state.HasFlag(SlskdTransferState.Rejected))
        {
            reason = "rejected";
        }
        else if (state.HasFlag(SlskdTransferState.TimedOut))
        {
            reason = "timed out";
        }
        else if (state.HasFlag(SlskdTransferState.Errored))
        {
            reason = "errored";
        }
        else if (state.HasFlag(SlskdTransferState.Aborted))
        {
            reason = "aborted";
        }

        if (reason is null)
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(error) ? $"Soulseek transfer {reason}" : $"Soulseek transfer {reason}: {error}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Soulseek query {Query} failed; trying the next one")]
    private static partial void LogQueryFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not cancel the Soulseek transfer {TransferId}; it may already be gone")]
    private static partial void LogCancelFailed(ILogger logger, Guid transferId, Exception exception);
}
