using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Plex;

/// <summary>Tells Plex to scan the album folders files have just landed in.</summary>
public interface IPlexLibraryUpdater
{
    /// <summary>
    /// Gets the message of the last partial scan that failed, or <see langword="null"/> when the last
    /// one worked. Never carries a token; see <see cref="PlexException"/>.
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// Asks for a scan of one folder. For adoption (P3-04) and the compact library task (P3-09b),
    /// which know the folder without an import event.
    /// </summary>
    /// <param name="libraryId">The library the folder belongs to.</param>
    /// <param name="localFolder">The folder as Wondarr sees it, mapped for Plex when the scan runs.</param>
    void RequestFolder(long libraryId, string localFolder);
}

/// <summary>
/// Asks the Plex Media Server to scan just the album folder an import landed in, rather than the whole
/// section.
/// </summary>
/// <remarks>
/// Plex does not watch the file system: a file Wondarr places is invisible until a scan finds it, and a
/// full-section scan of a large library is expensive. Requests are therefore debounced — a scan runs
/// <see cref="QuietPeriod"/> after the last import and no later than <see cref="MaxDelay"/> after the
/// first one waiting — and coalesced per album folder, so an album's worth of imports is one request. A
/// folder that could not be scanned is retried after <see cref="RetryDelay"/>, a newer import never
/// brings that retry forward, and after <see cref="MaxAttemptsPerFolder"/> failures in a row the folder
/// is dropped and reported through <see cref="LastError"/>. Nothing here can fail an import: the
/// publisher only hands over a file id, and every failure is a log line and a health warning.
/// </remarks>
public sealed partial class PlexLibraryUpdater : BackgroundService, IHandle<SongImportedEvent>, IPlexLibraryUpdater
{
    /// <summary>How long the import burst has to be quiet before Plex is asked to scan.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>The longest a request waits for the imports to quieten down.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>The wait before a folder that could not be scanned is tried again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>How many folders in one library are worth asking for individually before the root is scanned once.</summary>
    public const int MaxFoldersPerLibrary = 25;

    /// <summary>How many times in a row one folder may fail before it is dropped.</summary>
    public const int MaxAttemptsPerFolder = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PlexLibraryUpdater> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    // The imports waiting for a scan. Song file ids are resolved to folders when the scan runs, so an
    // import event costs no database work on the publisher's thread.
    private readonly HashSet<long> _fileIds = [];
    private readonly HashSet<(long LibraryId, string Folder)> _folders = [];

    // How many times in a row each folder has failed, so a folder that is never going to work is
    // dropped rather than retried until the end of time.
    private readonly Dictionary<(long LibraryId, string Folder), int> _failures = [];

    // Timestamps from _time.GetTimestamp(); _firstPending is null when nothing is waiting, and
    // _notBefore holds a retry back however many imports arrive in the meantime.
    private long? _firstPending;
    private long _dueAt;
    private long _notBefore;

    // Only the first failure in a row is a warning: a server that keeps refusing would otherwise log
    // one per retry.
    private bool _failing;
    private string? _lastError;

    /// <summary>Initialises a new instance of the <see cref="PlexLibraryUpdater"/> class.</summary>
    /// <param name="scopes">Creates the scope each scan reads the database and the clients in.</param>
    /// <param name="time">The clock the debounce is measured against.</param>
    /// <param name="logger">The log sink.</param>
    public PlexLibraryUpdater(
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<PlexLibraryUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether a scan is waiting to run.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _firstPending is not null;
            }
        }
    }

    /// <inheritdoc />
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc />
    public Task HandleAsync(SongImportedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Request(fileId: message.SongFileId, folder: null);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void RequestFolder(long libraryId, string localFolder)
    {
        // Nothing here may throw at the caller: adoption and the compact task call this between their
        // own steps, and a scan that cannot be asked for is not worth failing their work over.
        if (libraryId <= 0 || string.IsNullOrWhiteSpace(localFolder))
        {
            LogIgnored(_logger, libraryId);

            return;
        }

        Request(fileId: null, folder: (libraryId, localFolder));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);

                while (NextWait() is { } wait)
                {
                    if (wait > TimeSpan.Zero)
                    {
                        // Re-evaluated afterwards: a newer request may have moved the due time.
                        await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!await TryRefreshAsync(stoppingToken).ConfigureAwait(false))
                    {
                        Retry();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; a pending scan is not worth delaying that for.
        }
    }

    /// <summary>Adds one request to the batch and makes sure the scan loop is awake.</summary>
    private void Request(long? fileId, (long LibraryId, string Folder)? folder)
    {
        lock (_gate)
        {
            if (fileId is { } id)
            {
                _fileIds.Add(id);
            }

            if (folder is { } pair)
            {
                _folders.Add(pair);
            }

            var now = _time.GetTimestamp();

            _firstPending ??= now;
            _dueAt = Math.Max(
                Math.Min(now + Ticks(QuietPeriod), _firstPending.Value + Ticks(MaxDelay)),
                _notBefore);

            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }

    /// <summary>The wait until the pending scan is due, or <see langword="null"/> when none is pending.</summary>
    private TimeSpan? NextWait()
    {
        lock (_gate)
        {
            if (_firstPending is null)
            {
                return null;
            }

            var remaining = _dueAt - _time.GetTimestamp();

            if (remaining <= 0)
            {
                return TimeSpan.Zero;
            }

            // Rounded up to whole milliseconds: Task.Delay truncates, and a sub-millisecond wait would spin.
            return TimeSpan.FromMilliseconds(Math.Ceiling(remaining * 1000.0 / _time.TimestampFrequency));
        }
    }

    /// <summary>Keeps what is waiting pending, due no earlier than <see cref="RetryDelay"/> from now.</summary>
    private void Retry()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();

            _notBefore = now + Ticks(RetryDelay);
            _firstPending ??= now;
            _dueAt = Math.Max(_dueAt, _notBefore);
        }
    }

    /// <summary>
    /// Scans everything that is waiting. Returns <see langword="false"/> when some of it should be
    /// tried again; a request with nothing to do (no section, no server) is dropped.
    /// </summary>
    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var (fileIds, folders) = TakePending();

        if (fileIds.Count == 0 && folders.Count == 0)
        {
            return true;
        }

        using var scope = _scopes.CreateScope();

        try
        {
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            var targets = await ResolveAsync(context, fileIds, folders, cancellationToken).ConfigureAwait(false);

            if (targets.Count == 0)
            {
                return true;
            }

            var connection = scope.ServiceProvider.GetRequiredService<IPlexConnectionService>();
            var selected = await connection.GetServerContextAsync(cancellationToken).ConfigureAwait(false);

            if (selected is not { } server)
            {
                LogNoServer(_logger);

                return true;
            }

            var client = scope.ServiceProvider.GetRequiredService<IPlexServerClient>();

            return await RefreshAsync(client, server.Server, server.Token, targets, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlexUnauthorizedException exception)
        {
            // Not even the connection could be read with this token; retrying cannot help.
            NoteUnauthorized(exception.Message);

            return true;
        }
        catch (Exception exception)
        {
            // The database or the connection settings could not be read: put the batch back and try
            // the whole of it again rather than losing the imports.
            Requeue(fileIds, folders);
            NoteFailure("the pending imports", exception.Message);

            return false;
        }
    }

    /// <summary>Empties the pending batch, so a request arriving during the scan waits for the next one.</summary>
    private (List<long> FileIds, List<(long LibraryId, string Folder)> Folders) TakePending()
    {
        lock (_gate)
        {
            _firstPending = null;

            var fileIds = _fileIds.ToList();
            var folders = _folders.ToList();

            _fileIds.Clear();
            _folders.Clear();

            return (fileIds, folders);
        }
    }

    /// <summary>Puts requests back that were taken but never attempted.</summary>
    private void Requeue(IEnumerable<long> fileIds, IEnumerable<(long LibraryId, string Folder)> folders)
    {
        lock (_gate)
        {
            foreach (var fileId in fileIds)
            {
                _fileIds.Add(fileId);
            }

            foreach (var folder in folders)
            {
                _folders.Add(folder);
            }
        }
    }

    /// <summary>
    /// Reads the libraries and the folders to scan. A song file that is gone is dropped, as is a
    /// library with no section; the folders of one library collapse to its root when there are too
    /// many of them for individual requests to be polite.
    /// </summary>
    private async Task<IReadOnlyList<PlexTarget>> ResolveAsync(
        WondarrDbContext context,
        List<long> fileIds,
        List<(long LibraryId, string Folder)> folders,
        CancellationToken cancellationToken)
    {
        var byLibrary = new Dictionary<long, HashSet<string>>();

        if (fileIds.Count > 0)
        {
            var rows = await (from file in context.SongFiles
                              join song in context.Songs on file.SongId equals song.Id
                              where fileIds.Contains(file.Id)
                              select new { file.Path, song.LibraryId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                AddFolder(byLibrary, row.LibraryId, Path.GetDirectoryName(row.Path));
            }
        }

        foreach (var (libraryId, folder) in folders)
        {
            AddFolder(byLibrary, libraryId, folder);
        }

        if (byLibrary.Count == 0)
        {
            return [];
        }

        var ids = byLibrary.Keys.ToList();
        var libraries = await context.Libraries
            .Where(library => ids.Contains(library.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var targets = new List<PlexTarget>(libraries.Count);

        foreach (var library in libraries)
        {
            var section = library.PlexSectionId?.Trim();

            if (string.IsNullOrEmpty(section))
            {
                LogNoSection(_logger, library.Name, library.Id);

                continue;
            }

            var pending = byLibrary[library.Id];

            if (pending.Count == 0)
            {
                continue;
            }

            IReadOnlyList<ScanWork> work;

            if (pending.Count > MaxFoldersPerLibrary)
            {
                // One request for the root scans more than the album folders needed, but it is one
                // request rather than dozens, and this only happens on a large adoption or a compact.
                LogTooManyFolders(_logger, library.Name, pending.Count);

                work = [new ScanWork(library.RootPath, PlexPathMapper.ToServerPath(library, library.RootPath))];
            }
            else
            {
                work =
                [
                    .. pending.Select(folder => new ScanWork(folder, PlexPathMapper.ToServerPath(library, folder))),
                ];
            }

            targets.Add(new PlexTarget(library.Id, section, work));
        }

        return targets;
    }

    /// <summary>Asks for one scan per folder, tracking what has to be tried again.</summary>
    private async Task<bool> RefreshAsync(
        IPlexServerClient client,
        Uri server,
        string token,
        IReadOnlyList<PlexTarget> targets,
        CancellationToken cancellationToken)
    {
        var retry = new List<(long LibraryId, string Folder)>();

        foreach (var target in targets)
        {
            foreach (var work in target.Work)
            {
                try
                {
                    await client
                        .RefreshPathAsync(server, token, target.SectionId, work.ServerPath, cancellationToken)
                        .ConfigureAwait(false);

                    Succeeded(target.LibraryId, work.LocalFolder);
                    LogRequested(_logger, work.ServerPath, target.SectionId);
                }
                catch (PlexUnauthorizedException exception)
                {
                    // A rejected token is not going to be accepted a minute later; the whole batch goes.
                    NoteUnauthorized(exception.Message);

                    return true;
                }
                catch (Exception exception) when (IsRetryable(exception, cancellationToken))
                {
                    if (RetryFolder(target.LibraryId, work.LocalFolder))
                    {
                        NoteFailure(work.LocalFolder, exception.Message);
                        retry.Add((target.LibraryId, work.LocalFolder));
                    }
                    else
                    {
                        NoteFailure(work.LocalFolder, exception.Message, dropped: true);
                    }
                }
            }
        }

        if (retry.Count == 0)
        {
            return true;
        }

        Requeue([], retry);

        return false;
    }

    /// <summary>
    /// Counts one failure against a folder. Returns <see langword="false"/> once it has failed
    /// <see cref="MaxAttemptsPerFolder"/> times in a row, when it is dropped instead.
    /// </summary>
    private bool RetryFolder(long libraryId, string folder)
    {
        lock (_gate)
        {
            var key = (libraryId, folder);
            var attempts = _failures.TryGetValue(key, out var count) ? count + 1 : 1;

            if (attempts >= MaxAttemptsPerFolder)
            {
                _failures.Remove(key);

                return false;
            }

            _failures[key] = attempts;

            return true;
        }
    }

    /// <summary>Forgets a folder's failures and clears the last error the health check reports.</summary>
    private void Succeeded(long libraryId, string folder)
    {
        lock (_gate)
        {
            _failures.Remove((libraryId, folder));
            _failing = false;
            _lastError = null;
        }
    }

    /// <summary>
    /// Remembers a failure for the health check. Only the first failure in a row is a warning while it
    /// keeps failing; a folder dropped for good always names itself, since that one is final.
    /// </summary>
    private void NoteFailure(string folder, string reason, bool dropped = false)
    {
        bool first;

        lock (_gate)
        {
            _lastError = reason;
            first = !_failing;
            _failing = true;
        }

        if (dropped)
        {
            LogDropped(_logger, folder, reason);
        }
        else if (first)
        {
            LogFailed(_logger, folder, reason);
        }
        else
        {
            LogStillFailing(_logger, folder, reason);
        }
    }

    /// <summary>
    /// Remembers a token the server refused. Nothing is retried, so this warns every time: it is a
    /// distinct, actionable state, not the repeated transport failure the one-warning rule is for.
    /// </summary>
    private void NoteUnauthorized(string reason)
    {
        lock (_gate)
        {
            _lastError = reason;
            _failing = true;
        }

        LogUnauthorized(_logger, reason);
    }

    /// <summary>Whether a failure is worth another attempt, rather than a shutdown in disguise.</summary>
    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        PlexUnauthorizedException => false,
        PlexException => true,
        HttpRequestException => true,
        TimeoutException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    private static void AddFolder(Dictionary<long, HashSet<string>> byLibrary, long libraryId, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (!byLibrary.TryGetValue(libraryId, out var folders))
        {
            folders = [];
            byLibrary[libraryId] = folders;
        }

        folders.Add(folder);
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asked Plex to scan {Folder} in section {Section}")]
    private static partial void LogRequested(ILogger logger, string folder, string section);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Library {Library} ({LibraryId}) has no Plex section; nothing to scan")]
    private static partial void LogNoSection(ILogger logger, string library, long libraryId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No Plex server is connected; nothing to scan")]
    private static partial void LogNoServer(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Count} folders changed in {Library}; scanning its root instead")]
    private static partial void LogTooManyFolders(ILogger logger, string library, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a Plex scan request for library {LibraryId}: no folder")]
    private static partial void LogIgnored(ILogger logger, long libraryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plex did not scan {Folder}: {Reason}; trying again in a minute")]
    private static partial void LogFailed(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plex still did not scan {Folder}: {Reason}")]
    private static partial void LogStillFailing(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Giving up on scanning {Folder} after three attempts: {Reason}")]
    private static partial void LogDropped(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plex refused to scan: {Reason}; not trying again")]
    private static partial void LogUnauthorized(ILogger logger, string reason);

    /// <summary>One folder to scan: what Wondarr knows it as, and what the server is asked for.</summary>
    private sealed record ScanWork(string LocalFolder, string ServerPath);

    /// <summary>One library's scan: the section, and the folders to ask for in it.</summary>
    private sealed record PlexTarget(long LibraryId, string SectionId, IReadOnlyList<ScanWork> Work);
}
