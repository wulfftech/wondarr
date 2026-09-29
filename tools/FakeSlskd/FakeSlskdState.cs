using System.Globalization;
using System.Text.Json.Nodes;

namespace FakeSlskd;

/// <summary>
/// Everything the fake knows: the searches it was asked for, the transfers it queued, the
/// fingerprints it generated, and the counters the gate reads back. All of it lives in memory — the
/// fake is one process inside one smoke-test container.
/// </summary>
public sealed class FakeSlskdState : IDisposable
{
    /// <summary>The version the fake reports, matching the slskd the app is built against.</summary>
    public const string ReportedVersion = "0.26.0";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".ogg", ".opus", ".wav", ".aac", ".alac", ".wma", ".aiff", ".aif", ".ape", ".wv",
    };

    private readonly FakeSlskdOptions _options;
    private readonly WebhookSender _webhooks;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ScheduledSearch> _searches = [];
    private readonly Dictionary<Guid, FakeTransfer> _transfers = [];
    private readonly Dictionary<string, ScenarioIdentity> _identities = new(StringComparer.Ordinal);

    private int _inFlight;
    private int _maxInFlight;
    private int _nextToken = 1;

    // What the last share scan found, per shared directory. Like slskd, the fake scans its shares on
    // its first start and otherwise only when asked (PUT /api/v0/shares); a later start restores the
    // last scan from its cache file. Files added since stay unshared until a rescan, which is exactly
    // what the app's rescanner has to take care of.
    private Dictionary<string, (int Directories, int Files)> _shareScan = new(StringComparer.Ordinal);
    private int _shareScans;

    /// <summary>Initialises a new instance of the <see cref="FakeSlskdState"/> class.</summary>
    /// <param name="options">Configuration and scenario.</param>
    public FakeSlskdState(FakeSlskdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _webhooks = new WebhookSender(options.Configuration.Webhooks);

        if (!RestoreShareCache())
        {
            RescanShares();
        }
    }

    /// <summary>How many share scans ran, the one at start included.</summary>
    public int ShareScans
    {
        get
        {
            lock (_gate)
            {
                return _shareScans;
            }
        }
    }

    /// <summary>Scans the configured shared directories again, as <c>PUT /api/v0/shares</c> does.</summary>
    public void RescanShares()
    {
        var scan = new Dictionary<string, (int Directories, int Files)>(StringComparer.Ordinal);

        foreach (var directory in _options.Configuration.ShareDirectories)
        {
            scan[directory] = CountShare(directory);
        }

        lock (_gate)
        {
            _shareScan = scan;
            _shareScans++;
        }

        if (_options.ShareCachePath is { } path)
        {
            var document = new JsonObject();

            foreach (var (directory, counts) in scan)
            {
                document[directory] = new JsonArray(counts.Directories, counts.Files);
            }

            File.WriteAllText(path, document.ToJsonString());
        }
    }

    /// <summary>Loads the last scan from the cache file, as a restarted slskd does.</summary>
    private bool RestoreShareCache()
    {
        if (_options.ShareCachePath is not { } path || !File.Exists(path))
        {
            return false;
        }

        var scan = new Dictionary<string, (int Directories, int Files)>(StringComparer.Ordinal);

        foreach (var (directory, counts) in JsonNode.Parse(File.ReadAllText(path))!.AsObject())
        {
            scan[directory] = (counts![0]!.GetValue<int>(), counts[1]!.GetValue<int>());
        }

        // slskd only trusts a cache of the folders it is configured to share now (seen live: turning
        // sharing off and restarting shares 0 folders); a different set is scanned afresh.
        if (!scan.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(_options.Configuration.ShareDirectories))
        {
            return false;
        }

        _shareScan = scan;
        FakeSlskdLog.Info($"Share cache loaded from disk successfully. Sharing {scan.Values.Sum(c => c.Directories)} directories and {scan.Values.Sum(c => c.Files)} files");

        return true;
    }

    /// <summary>The most searches that were in flight at any moment.</summary>
    public int MaxInFlightObserved
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    /// <summary>Whether a request's API key is one of the configured keys.</summary>
    /// <param name="apiKey">The <c>X-API-Key</c> header's value, or <see langword="null"/>.</param>
    public bool IsAuthorized(string? apiKey)
    {
        if (_options.Configuration.AuthenticationDisabled)
        {
            return true;
        }

        return apiKey is not null
            && _options.Configuration.ApiKeys.Contains(apiKey, StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates a search and starts its timer. Returns <see langword="null"/> when the configured
    /// number of searches is already in flight, which the endpoint reports as 429.
    /// </summary>
    /// <param name="id">The search's id.</param>
    /// <param name="searchText">The text to search for.</param>
    public SlskdSearchResource? StartSearch(Guid id, string searchText)
    {
        lock (_gate)
        {
            if (_inFlight >= _options.Scenario.MaxInFlight)
            {
                return null;
            }

            var now = DateTime.UtcNow;

            var search = new ScheduledSearch
            {
                Id = id,
                Text = searchText,
                Token = _nextToken++,
                PostedAt = now,
                CompleteAt = now.AddMilliseconds(_options.Scenario.SearchDelayMs),
                Matches = _options.Scenario.Files
                    .Where(file => SearchText.Matches(searchText, file.Path))
                    .ToArray(),
            };

            _searches[id] = search;
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);

            return ToResource(search);
        }
    }

    /// <summary>Every search that has not been deleted.</summary>
    public IReadOnlyList<SlskdSearchResource> ListSearches()
    {
        lock (_gate)
        {
            return _searches.Values
                .Where(search => search.DeletedAt is null)
                .OrderBy(search => search.PostedAt)
                .Select(ToResource)
                .ToArray();
        }
    }

    /// <summary>One search, or <see langword="null"/> when it is unknown or was deleted.</summary>
    /// <param name="id">The search's id.</param>
    public SlskdSearchResource? GetSearch(Guid id)
    {
        lock (_gate)
        {
            return FindSearch(id) is { } search ? ToResource(search) : null;
        }
    }

    /// <summary>
    /// One search's responses. Empty while the search is in progress (like the real endpoint), and
    /// <see langword="null"/> when the search is unknown or was deleted.
    /// </summary>
    /// <param name="id">The search's id.</param>
    public IReadOnlyList<SlskdResponseResource>? GetResponses(Guid id)
    {
        lock (_gate)
        {
            if (FindSearch(id) is not { } search)
            {
                return null;
            }

            return IsComplete(search) ? ToResponses(search) : [];
        }
    }

    /// <summary>Completes a search now, as <c>PUT /searches/{id}</c> does.</summary>
    /// <param name="id">The search's id.</param>
    /// <param name="reason">The reason recorded in the state string.</param>
    public SlskdSearchResource? CompleteSearch(Guid id, string reason)
    {
        lock (_gate)
        {
            if (FindSearch(id) is not { } search)
            {
                return null;
            }

            search.EndedAt ??= DateTime.UtcNow;
            search.EndReason = reason;

            return ToResource(search);
        }
    }

    /// <summary>Deletes a search, freeing an in-flight slot.</summary>
    /// <param name="id">The search's id.</param>
    /// <returns>Whether the search existed.</returns>
    public bool DeleteSearch(Guid id)
    {
        lock (_gate)
        {
            if (FindSearch(id) is not { } search)
            {
                return false;
            }

            search.DeletedAt = DateTime.UtcNow;
            _inFlight--;

            return true;
        }
    }

    /// <summary>
    /// Queues a batch of downloads and starts the background work that finishes them. Files the
    /// scenario's peer does not offer are reported as failures.
    /// </summary>
    /// <param name="body">The request.</param>
    /// <param name="destination">The validated destination passed by the endpoint.</param>
    /// <param name="searchId">The batch's search id.</param>
    public EnqueueBatchResponse EnqueueBatch(EnqueueBatchRequestBody body, string destination, Guid? searchId)
    {
        var batchId = body.Id ?? Guid.NewGuid();
        var now = DateTime.UtcNow;

        var failures = new List<EnqueueFailureResource>();
        var queued = new List<FakeTransfer>();

        lock (_gate)
        {
            foreach (var file in body.Files)
            {
                var scenarioFile = _options.Scenario.Files.FirstOrDefault(candidate =>
                    string.Equals(candidate.Username, body.Username, StringComparison.Ordinal)
                    && string.Equals(candidate.Path, file.Filename, StringComparison.Ordinal));

                if (scenarioFile is null)
                {
                    failures.Add(new EnqueueFailureResource(
                        file.Filename,
                        "File not found in the peer's shared files"));

                    continue;
                }

                var transfer = new FakeTransfer
                {
                    Id = Guid.NewGuid(),
                    BatchId = batchId,
                    SearchId = searchId,
                    Username = body.Username,
                    File = scenarioFile,

                    // slskd's default destination expression is ${SOURCE_DIRECTORY}: the last
                    // segment of the remote path.
                    Destination = destination.Length > 0 ? destination : LastSegment(RemoteDirectory(scenarioFile.Path)),
                    RequestedAt = now,
                    Size = file.Size > 0 ? file.Size : scenarioFile.EffectiveSize,
                };

                _transfers[transfer.Id] = transfer;
                queued.Add(transfer);
            }
        }

        foreach (var transfer in queued)
        {
            _ = RunTransferAsync(transfer);
        }

        return new EnqueueBatchResponse
        {
            Batch = new SlskdBatchResource { Id = batchId, Username = body.Username, SearchId = searchId },
            Failures = failures,
        };
    }

    /// <summary>Every user's downloads, grouped by remote directory.</summary>
    /// <param name="includeRemoved">Whether removed records are included.</param>
    public IReadOnlyList<DownloadUserResource> ListDownloads(bool includeRemoved)
    {
        lock (_gate)
        {
            return _transfers.Values
                .Where(transfer => includeRemoved || !transfer.Removed)
                .GroupBy(transfer => transfer.Username, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new DownloadUserResource
                {
                    Username = group.Key,
                    Directories = group
                        .GroupBy(transfer => RemoteDirectory(transfer.File.Path), StringComparer.Ordinal)
                        .OrderBy(directory => directory.Key, StringComparer.Ordinal)
                        .Select(directory => new DownloadDirectoryResource
                        {
                            Directory = directory.Key,
                            FileCount = directory.Count(),
                            Files = directory.Select(ToResource).ToArray(),
                        })
                        .ToArray(),
                })
                .ToArray();
        }
    }

    /// <summary>One transfer, or <see langword="null"/> when it is unknown.</summary>
    /// <param name="username">The peer the transfer belongs to.</param>
    /// <param name="id">The transfer's id.</param>
    public SlskdTransferResource? GetTransfer(string username, Guid id)
    {
        lock (_gate)
        {
            return FindTransfer(username, id) is { } transfer ? ToResource(transfer) : null;
        }
    }

    /// <summary>How far down the peer's queue a transfer is.</summary>
    /// <param name="username">The peer the transfer belongs to.</param>
    /// <param name="id">The transfer's id.</param>
    public int? GetPosition(string username, Guid id)
    {
        lock (_gate)
        {
            return FindTransfer(username, id)?.PlaceInQueue;
        }
    }

    /// <summary>
    /// Cancels a transfer, marking a pending one <c>Completed, Cancelled</c> exactly as slskd does.
    /// </summary>
    /// <param name="username">The peer the transfer belongs to.</param>
    /// <param name="id">The transfer's id.</param>
    /// <param name="remove">Whether the record is removed from the listing as well.</param>
    /// <returns>Whether the transfer existed.</returns>
    public bool CancelTransfer(string username, Guid id, bool remove)
    {
        lock (_gate)
        {
            if (FindTransfer(username, id) is not { } transfer)
            {
                return false;
            }

            if (!transfer.State.StartsWith("Completed", StringComparison.Ordinal))
            {
                transfer.State = "Completed, Cancelled";
                transfer.EndedAt = DateTime.UtcNow;
                transfer.PlaceInQueue = null;
            }

            transfer.Removed |= remove;
            transfer.Cancellation.Cancel();

            return true;
        }
    }

    /// <summary>Remembers what the AcoustID stub should answer for a fingerprint.</summary>
    /// <param name="fingerprint">The generated file's fingerprint.</param>
    /// <param name="identity">The recording the scenario links to it.</param>
    public void RememberIdentity(string fingerprint, ScenarioIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            _identities[fingerprint] = identity;
        }
    }

    /// <summary>The recording the AcoustID stub knows for <paramref name="fingerprint"/>.</summary>
    /// <param name="fingerprint">The fingerprint to look up.</param>
    public ScenarioIdentity? FindIdentity(string fingerprint)
    {
        lock (_gate)
        {
            return fingerprint is not null && _identities.TryGetValue(fingerprint, out var identity) ? identity : null;
        }
    }

    /// <summary>What the gate reads back: the search budget and the transfers.</summary>
    public GateLogResource GateLog()
    {
        lock (_gate)
        {
            return new GateLogResource(
                _searches.Values
                    .OrderBy(search => search.PostedAt)
                    .Select(search => new GateSearchResource(
                        search.Id,
                        search.Text,
                        Format(search.PostedAt),
                        search.DeletedAt is { } deleted ? Format(deleted) : null))
                    .ToArray(),
                _maxInFlight,
                _transfers.Values
                    .OrderBy(transfer => transfer.RequestedAt)
                    .Select(transfer => new GateTransferResource(
                        transfer.Id,
                        transfer.Username,
                        transfer.File.Path,
                        transfer.Destination,
                        transfer.State))
                    .ToArray());
        }
    }

    /// <summary>The application-state document the app's supervisor reads.</summary>
    public JsonObject ApplicationJson()
    {
        var configuration = _options.Configuration;
        var loggedIn = configuration.SoulseekUsername is not null;
        var shares = ShareTotals();

        return new JsonObject
        {
            ["version"] = new JsonObject
            {
                ["full"] = $"{ReportedVersion}.0 ({ReportedVersion}.0+fakeslskd)",
                ["current"] = ReportedVersion,
                ["isCanary"] = false,
                ["isDevelopment"] = false,
            },
            ["pendingReconnect"] = false,
            ["pendingRestart"] = false,
            ["server"] = ServerJson(),
            ["user"] = new JsonObject
            {
                ["username"] = configuration.SoulseekUsername ?? string.Empty,
                ["privileges"] = new JsonObject { ["isPrivileged"] = false, ["privilegesRemaining"] = 0 },
                ["statistics"] = new JsonObject
                {
                    ["averageSpeed"] = 0,
                    ["directoryCount"] = shares.Directories,
                    ["fileCount"] = shares.Files,
                    ["uploadCount"] = 0,
                },
            },
            ["distributedNetwork"] = new JsonObject
            {
                ["branchLevel"] = 0,
                ["canAcceptChildren"] = false,
                ["childLimit"] = 25,
                ["children"] = new JsonArray(),
                ["hasParent"] = false,
                ["isBranchRoot"] = false,
            },
            ["shares"] = new JsonObject
            {
                ["scanPending"] = false,
                ["scanning"] = false,
                ["ready"] = true,
                ["faulted"] = false,
                ["cancelled"] = false,
                ["scanProgress"] = 1,
                ["hosts"] = new JsonArray("local"),
                ["directories"] = shares.Directories,
                ["files"] = shares.Files,
            },
            ["rooms"] = new JsonArray(),
            ["users"] = new JsonArray(),
            ["isLoggedIn"] = loggedIn,
        };
    }

    /// <summary>The Soulseek server state document <c>GET /api/v0/server</c> returns.</summary>
    public JsonObject ServerJson()
    {
        var loggedIn = _options.Configuration.SoulseekUsername is not null;

        return new JsonObject
        {
            ["address"] = "vps.slsknet.org",
            ["ipEndPoint"] = "127.0.0.1:2271",
            ["state"] = loggedIn ? "Connected, LoggedIn" : "Disconnected",
            ["isConnected"] = loggedIn,
            ["isConnecting"] = false,
            ["isLoggedIn"] = loggedIn,
            ["isLoggingIn"] = false,
            ["isTransitioning"] = false,
        };
    }

    /// <summary>The shared-directory listing <c>GET /api/v0/shares</c> returns.</summary>
    public JsonObject SharesJson()
    {
        var local = new JsonArray();

        foreach (var directory in _options.Configuration.ShareDirectories)
        {
            var counts = ScannedShare(directory);
            var alias = LastSegment(directory);

            local.Add(new JsonObject
            {
                ["id"] = Deterministic.HexId(directory),
                ["alias"] = alias,
                ["isExcluded"] = false,
                ["localPath"] = directory,
                ["raw"] = directory,
                ["remotePath"] = alias,
                ["directories"] = counts.Directories,
                ["files"] = counts.Files,
            });
        }

        return new JsonObject { ["local"] = local };
    }

    /// <inheritdoc />
    public void Dispose() => _webhooks.Dispose();

    private static string Format(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string RemoteDirectory(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');

        return separator <= 0 ? string.Empty : normalized[..separator];
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);

        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private (int Directories, int Files) ShareTotals()
    {
        lock (_gate)
        {
            return (_shareScan.Values.Sum(counts => counts.Directories), _shareScan.Values.Sum(counts => counts.Files));
        }
    }

    private (int Directories, int Files) ScannedShare(string directory)
    {
        lock (_gate)
        {
            return _shareScan.GetValueOrDefault(directory);
        }
    }

    /// <summary>
    /// Counts the audio files under a shared directory, recursively, and the directories that hold
    /// them. A directory that does not exist counts zero, which is what a fresh volume looks like.
    /// </summary>
    private static (int Directories, int Files) CountShare(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (0, 0);
        }

        var holders = new HashSet<string>(StringComparer.Ordinal);
        var files = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!AudioExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                files++;
                holders.Add(Path.GetDirectoryName(file) ?? directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FakeSlskdLog.Error($"Could not count the share at {directory}: {exception.Message}");
        }

        return (holders.Count, files);
    }

    private ScheduledSearch? FindSearch(Guid id) =>
        _searches.TryGetValue(id, out var search) && search.DeletedAt is null ? search : null;

    private FakeTransfer? FindTransfer(string username, Guid id) =>
        _transfers.TryGetValue(id, out var transfer)
            && string.Equals(transfer.Username, username, StringComparison.Ordinal)
                ? transfer
                : null;

    private static bool IsComplete(ScheduledSearch search) =>
        search.EndedAt is not null || DateTime.UtcNow >= search.CompleteAt;

    private SlskdSearchResource ToResource(ScheduledSearch search)
    {
        var complete = IsComplete(search);
        var endedAt = search.EndedAt ?? (complete ? search.CompleteAt : null);
        var files = search.Matches.Sum(file => 1);

        return new SlskdSearchResource
        {
            Id = search.Id,
            SearchText = search.Text,
            Token = search.Token,
            State = complete ? $"Completed, {search.EndReason}" : "InProgress",
            StartedAt = search.PostedAt,
            EndedAt = endedAt,
            FileCount = files,
            LockedFileCount = 0,
            ResponseCount = search.Matches.Select(file => file.Username).Distinct(StringComparer.Ordinal).Count(),
            IsComplete = complete,
        };
    }

    private static SlskdResponseResource[] ToResponses(ScheduledSearch search) =>
        search.Matches
            .GroupBy(file => file.Username, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();

                return new SlskdResponseResource
                {
                    Username = group.Key,
                    Token = search.Token,
                    HasFreeUploadSlot = first.HasFreeUploadSlot,
                    UploadSpeed = first.UploadSpeed,
                    QueueLength = first.QueueLength,
                    FileCount = group.Count(),
                    LockedFileCount = 0,
                    Files = group.Select(ToResource).ToArray(),
                };
            })
            .ToArray();

    private static SlskdFileResource ToResource(ScenarioFile file) => new()
    {
        Filename = file.Path,
        Size = file.EffectiveSize,
        BitRate = file.BitRate,
        SampleRate = file.SampleRate,
        BitDepth = file.BitDepth,
        Length = file.Length,
    };

    private static SlskdTransferResource ToResource(FakeTransfer transfer) => new()
    {
        Id = transfer.Id,
        BatchId = transfer.BatchId,
        Username = transfer.Username,
        Filename = transfer.File.Path,
        Size = transfer.Size,
        State = transfer.State,
        RequestedAt = transfer.RequestedAt,
        EnqueuedAt = transfer.EnqueuedAt,
        StartedAt = transfer.StartedAt,
        EndedAt = transfer.EndedAt,
        BytesTransferred = transfer.BytesTransferred,
        AverageSpeed = transfer.AverageSpeed,
        PlaceInQueue = transfer.PlaceInQueue,
        Removed = transfer.Removed,
    };

    /// <summary>
    /// Plays out one queued download: queue delay, the peer's behaviour, and — on success — a real
    /// audio file, moved from the incomplete directory into the downloads directory, followed by the
    /// <c>DownloadFileComplete</c> webhook.
    /// </summary>
    private async Task RunTransferAsync(FakeTransfer transfer)
    {
        try
        {
            await Task.Delay(_options.Scenario.TransferDelayMs, transfer.Cancellation.Token).ConfigureAwait(false);

            var behaviour = transfer.File.Transfer;

            lock (_gate)
            {
                transfer.EnqueuedAt = DateTime.UtcNow;
                transfer.StartedAt = transfer.EnqueuedAt;

                if (string.Equals(behaviour, TransferBehaviour.Reject, StringComparison.OrdinalIgnoreCase))
                {
                    transfer.State = "Completed, Rejected";
                    transfer.EndedAt = DateTime.UtcNow;
                    transfer.PlaceInQueue = null;

                    return;
                }

                transfer.State = "InProgress";
            }

            if (string.Equals(behaviour, TransferBehaviour.Stall, StringComparison.OrdinalIgnoreCase))
            {
                // In progress for ever, with nothing transferred: the app has to time it out itself.
                return;
            }

            var destination = Path.Combine(
                _options.Configuration.DownloadsDirectory,
                transfer.Destination.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
                transfer.File.BareName);

            var incomplete = IncompletePath(transfer);

            var fingerprint = await _options.AudioGenerator
                .GenerateAsync(transfer.File, incomplete, transfer.Cancellation.Token)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.Move(incomplete, destination, overwrite: true);

            var actualSize = new FileInfo(destination).Length;
            var startedAt = transfer.StartedAt;
            SlskdTransferResource resource;

            lock (_gate)
            {
                transfer.Size = actualSize;
                transfer.BytesTransferred = actualSize;
                transfer.State = "Completed, Succeeded";
                transfer.EndedAt = DateTime.UtcNow;
                transfer.PlaceInQueue = null;

                if (startedAt is { } started)
                {
                    var seconds = Math.Max(0.001, (transfer.EndedAt.Value - started).TotalSeconds);
                    transfer.AverageSpeed = actualSize / seconds;
                }

                resource = ToResource(transfer);
            }

            if (fingerprint.Length > 0 && transfer.File.Identity is { } identity)
            {
                RememberIdentity(fingerprint, identity);
            }

            await _webhooks
                .SendAsync(
                    new DownloadFileCompleteEvent(
                        DownloadFileCompleteEvent.EventName,
                        Guid.NewGuid(),
                        DateTime.UtcNow,
                        destination,
                        transfer.File.Path,
                        resource),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (!transfer.State.StartsWith("Completed", StringComparison.Ordinal))
                {
                    transfer.State = "Completed, Cancelled";
                    transfer.EndedAt = DateTime.UtcNow;
                    transfer.PlaceInQueue = null;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                transfer.State = "Completed, Errored";
                transfer.EndedAt = DateTime.UtcNow;
                transfer.PlaceInQueue = null;
            }

            FakeSlskdLog.Error($"Transfer {transfer.Id} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Where the audio is generated: slskd's own layout, <c>&lt;incomplete&gt;/&lt;username&gt;/&lt;remote
    /// path&gt;</c>, sanitised so a peer path cannot escape the directory.
    /// </summary>
    private string IncompletePath(FakeTransfer transfer)
    {
        var segments = transfer.File.Path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Sanitize)
            .Where(segment => segment.Length > 0)
            .ToArray();

        return Path.Combine([_options.Configuration.IncompleteDirectory, Sanitize(transfer.Username), .. segments]);
    }

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return cleaned is "." or ".." ? string.Empty : cleaned;
    }

    private sealed class ScheduledSearch
    {
        public required Guid Id { get; init; }

        public required string Text { get; init; }

        public required int Token { get; init; }

        public required DateTime PostedAt { get; init; }

        public required DateTime CompleteAt { get; init; }

        public required IReadOnlyList<ScenarioFile> Matches { get; init; }

        public DateTime? EndedAt { get; set; }

        public DateTime? DeletedAt { get; set; }

        public string EndReason { get; set; } = "TimedOut";
    }

    private sealed class FakeTransfer
    {
        public required Guid Id { get; init; }

        public required Guid BatchId { get; init; }

        public required Guid? SearchId { get; init; }

        public required string Username { get; init; }

        public required ScenarioFile File { get; init; }

        public required string Destination { get; init; }

        public required DateTime RequestedAt { get; init; }

        public CancellationTokenSource Cancellation { get; } = new();

        public long Size { get; set; }

        public string State { get; set; } = "Queued, Remotely";

        public long BytesTransferred { get; set; }

        public int? PlaceInQueue { get; set; } = 1;

        public bool Removed { get; set; }

        public DateTime? EnqueuedAt { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        public double AverageSpeed { get; set; }
    }
}
