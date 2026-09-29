using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>A download Wondarr asked slskd for and can follow by id afterwards.</summary>
/// <param name="Username">The peer the file is coming from.</param>
/// <param name="TransferId">slskd's id for this transfer.</param>
/// <param name="RemoteFilename">The peer's own path for the file.</param>
/// <param name="Destination">The per-grab folder, relative to slskd's download directory.</param>
public sealed record SlskdGrab(string Username, Guid TransferId, string RemoteFilename, string Destination);

/// <summary>
/// Where one grab stands. <paramref name="Error"/> and <paramref name="LocalPath"/> are only ever
/// set on a transfer that has stopped, and <paramref name="LocalPath"/> only on one that succeeded.
/// </summary>
/// <param name="State">The parsed flags slskd reports.</param>
/// <param name="PercentComplete">How complete slskd says the transfer is.</param>
/// <param name="BytesTransferred">How many bytes slskd has written.</param>
/// <param name="Size">The size the peer advertised.</param>
/// <param name="PlaceInQueue">Our place in the peer's queue, when the peer has told us.</param>
/// <param name="Error">Why the transfer has no usable file, when it has not.</param>
/// <param name="LocalPath">The finished file on disk, when it is there.</param>
public sealed record SlskdDownloadStatus(
    SlskdTransferState State,
    double PercentComplete,
    long BytesTransferred,
    long Size,
    int? PlaceInQueue,
    string? Error,
    string? LocalPath);

/// <summary>
/// The one way Wondarr starts a Soulseek download and follows it. Everything goes through a
/// per-grab destination, so the import pipeline never has to guess slskd's own layout, and the
/// finished file is found on disk rather than inferred.
/// </summary>
public interface ISlskdDownloads
{
    /// <summary>Enqueues one file into its own folder and returns the transfer to track.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not a safe relative path.</exception>
    /// <exception cref="SlskdEnqueueException">slskd refused the file.</exception>
    Task<SlskdGrab> EnqueueAsync(
        string username,
        string remoteFilename,
        long size,
        string destination,
        string externalId,
        CancellationToken cancellationToken);

    /// <summary>Reads where a grab stands, and where its file is once it has succeeded.</summary>
    Task<SlskdDownloadStatus> GetStatusAsync(SlskdGrab grab, CancellationToken cancellationToken);

    /// <summary>Cancels a grab and removes it from slskd's records.</summary>
    Task CancelAsync(SlskdGrab grab, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class SlskdDownloads : ISlskdDownloads
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly ILogger<SlskdDownloads> _logger;

    /// <summary>Initialises a new instance of the <see cref="SlskdDownloads"/> class.</summary>
    /// <param name="scopeFactory">
    /// This service is a singleton and the transfer API is a typed client built from scoped secrets,
    /// so each call resolves its own client from a scope of its own.
    /// </param>
    /// <param name="options">Soulseek settings, for the download directory the file lands in.</param>
    /// <param name="logger">Debug lines for the best-effort lookups.</param>
    public SlskdDownloads(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SoulseekOptions> options,
        ILogger<SlskdDownloads> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SlskdGrab> EnqueueAsync(
        string username,
        string remoteFilename,
        long size,
        string destination,
        string externalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFilename);
        ArgumentNullException.ThrowIfNull(externalId);

        ValidateDestination(destination);

        using var scope = _scopeFactory.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ISlskdTransferApi>();

        // The id is Wondarr's, so the batch can be recognised even when slskd echoes nothing back.
        var batchId = Guid.NewGuid();
        var request = new SlskdEnqueueBatchRequest(
            username,
            [new SlskdEnqueueFile(remoteFilename, size)],
            new SlskdBatchOptions(destination, externalId),
            batchId);

        var response = await api.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);

        var failure = response.Failures.FirstOrDefault(
            entry => string.Equals(entry.Filename, remoteFilename, StringComparison.Ordinal));

        if (failure is not null)
        {
            throw new SlskdEnqueueException(HttpStatusCode.OK, failure.Message);
        }

        var id = await FindTransferIdAsync(api, response.Batch?.Id ?? batchId, username, remoteFilename, cancellationToken)
            .ConfigureAwait(false);

        return new SlskdGrab(username, id, remoteFilename, destination);
    }

    /// <inheritdoc />
    public async Task<SlskdDownloadStatus> GetStatusAsync(SlskdGrab grab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grab);

        using var scope = _scopeFactory.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ISlskdTransferApi>();

        var transfer = await api.GetAsync(grab.Username, grab.TransferId, cancellationToken).ConfigureAwait(false);

        if (transfer is null)
        {
            return new SlskdDownloadStatus(
                SlskdTransferState.Completed | SlskdTransferState.Errored,
                0,
                0,
                0,
                null,
                "Transfer no longer exists in slskd",
                null);
        }

        var state = SlskdTransferStates.Parse(transfer.State);
        var placeInQueue = transfer.PlaceInQueue;

        // A transfer sitting in the peer's queue has a place only the peer can report; asking is
        // best effort, because the answer is cosmetic and the peer may never give one.
        if (state.HasFlag(SlskdTransferState.Queued) && state.HasFlag(SlskdTransferState.Remotely))
        {
            try
            {
                placeInQueue = await api
                    .GetPlaceInQueueAsync(grab.Username, grab.TransferId, cancellationToken)
                    .ConfigureAwait(false) ?? placeInQueue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogPlaceInQueueFailed(_logger, grab.TransferId, exception);
            }
        }

        string? localPath = null;
        var error = transfer.Exception;

        if (state.HasFlag(SlskdTransferState.Completed) && state.HasFlag(SlskdTransferState.Succeeded))
        {
            // A persisted grab is re-checked: a rooted destination would make Path.Combine drop the
            // downloads directory altogether.
            ValidateDestination(grab.Destination);
            var folder = Path.Combine(_options.CurrentValue.DownloadsDir, grab.Destination);
            localPath = FindCompletedFile(folder, grab.RemoteFilename);

            error = localPath is null ? $"Completed file not found in {folder}" : null;
        }

        return new SlskdDownloadStatus(
            state,
            transfer.PercentComplete,
            transfer.BytesTransferred,
            transfer.Size,
            placeInQueue,
            error,
            localPath);
    }

    /// <inheritdoc />
    public async Task CancelAsync(SlskdGrab grab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grab);

        using var scope = _scopeFactory.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ISlskdTransferApi>();

        await api.CancelAsync(grab.Username, grab.TransferId, remove: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the transfer slskd created for the batch Wondarr just sent. The batch id is the reliable
    /// link; when slskd did not record it, the newest transfer for the same file is the one that was
    /// just enqueued.
    /// </summary>
    private static async Task<Guid> FindTransferIdAsync(
        ISlskdTransferApi api,
        Guid batchId,
        string username,
        string remoteFilename,
        CancellationToken cancellationToken)
    {
        var users = await api.ListAsync(cancellationToken).ConfigureAwait(false);

        var matches = users
            .Where(user => string.Equals(user.Username, username, StringComparison.Ordinal))
            .SelectMany(user => user.Directories)
            .SelectMany(directory => directory.Files)
            .Where(transfer => string.Equals(transfer.Filename, remoteFilename, StringComparison.Ordinal))
            .ToList();

        var match = matches.Find(transfer => transfer.BatchId == batchId)
            ?? matches.OrderByDescending(transfer => transfer.RequestedAt).FirstOrDefault();

        return match?.Id
            ?? throw new SlskdEnqueueException(
                HttpStatusCode.OK,
                "slskd accepted the download but reported no transfer for it");
    }

    /// <summary>
    /// The finished file inside the grab's folder. Its name is normally the remote file's bare name,
    /// but slskd renames a file rather than overwrite one (<c>name_1638345.mp3</c>), so the folder is
    /// searched for the newest file sharing that stem before giving up.
    /// </summary>
    private static string? FindCompletedFile(string folder, string remoteFilename)
    {
        var bareName = BareFileName(remoteFilename);

        // The name is the peer's: never let it name the folder itself or its parent.
        if (bareName.Trim() is "" or "." or "..")
        {
            return null;
        }

        var exact = Path.Combine(folder, bareName);

        if (File.Exists(exact))
        {
            return exact;
        }

        if (!Directory.Exists(folder))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(bareName);
        var extension = Path.GetExtension(bareName);

        if (stem.Length == 0)
        {
            return null;
        }

        // Only slskd's own rename shape counts: "<stem>_<digits><extension>". A mere prefix match
        // would take "abc.mp3" for "a.mp3".
        return Directory
            .EnumerateFiles(folder)
            .Where(path => IsRenamedCopy(Path.GetFileName(path), stem, extension))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsRenamedCopy(string name, string stem, string extension)
    {
        if (!name.StartsWith(stem + "_", StringComparison.Ordinal) ||
            !name.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        var middle = name.AsSpan(stem.Length + 1, name.Length - stem.Length - 1 - extension.Length);

        return middle.Length > 0 && middle.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>The file's name without the peer's directory path, however the peer separated it.</summary>
    private static string BareFileName(string remoteFilename)
    {
        var separator = remoteFilename.LastIndexOfAny(['\\', '/']);

        return separator >= 0 ? remoteFilename[(separator + 1)..] : remoteFilename;
    }

    /// <summary>
    /// A destination must stay inside slskd's download directory: anything absolute, anything with a
    /// drive or a colon, and anything that walks back up with <c>..</c> is refused here rather than
    /// left to slskd.
    /// </summary>
    private static void ValidateDestination(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (destination[0] is '/' or '\\' || destination.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The destination must be a relative path inside slskd's download directory",
                nameof(destination));
        }

        var segments = destination.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        // Trimmed first: Windows drops trailing dots and spaces, so ".. " would walk up there too.
        if (segments.Any(segment => segment.Trim().TrimEnd('.').Length == 0))
        {
            throw new ArgumentException(
                "The destination must not walk out of slskd's download directory",
                nameof(destination));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read the queue position of transfer {TransferId}")]
    private static partial void LogPlaceInQueueFailed(ILogger logger, Guid transferId, Exception exception);
}