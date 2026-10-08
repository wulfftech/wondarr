using System.Text.Json;
using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// <see cref="IUsenetClient"/> over SABnzbd: the calls of <see cref="SabnzbdProxy"/> with the row's
/// settings, SABnzbd's status words in Wondarr's terms, and the finished job's folder through the
/// row's remote path mappings.
/// </summary>
public sealed class SabnzbdClient : IUsenetClient
{
    private readonly SabnzbdProxy _proxy;

    /// <summary>Initialises a new instance of the <see cref="SabnzbdClient"/> class.</summary>
    /// <param name="proxy">The SABnzbd HTTP layer.</param>
    public SabnzbdClient(SabnzbdProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        _proxy = proxy;
    }

    /// <inheritdoc />
    public Task<string> AddAsync(DownloadClient client, UsenetAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((request.NzbFile is null) == (request.NzbUrl is null))
        {
            throw new ArgumentException("Exactly one of the NZB file and the NZB URL must be given.", nameof(request));
        }

        var settings = Settings(client);

        return request.NzbFile is { } nzb
            ? _proxy.AddFileAsync(settings, nzb, request.Title, request.Paused, cancellationToken)
            : _proxy.AddUrlAsync(settings, request.NzbUrl!, request.Title, request.Paused, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UsenetFileInfo>> GetFilesAsync(DownloadClient client, string jobId, CancellationToken cancellationToken) =>
        _proxy.GetFilesAsync(Settings(client), jobId, cancellationToken);

    /// <inheritdoc />
    public Task DeleteFileAsync(DownloadClient client, string jobId, string fileId, CancellationToken cancellationToken) =>
        _proxy.DeleteFileAsync(Settings(client), jobId, fileId, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(DownloadClient client, string jobId, CancellationToken cancellationToken) =>
        _proxy.ResumeAsync(Settings(client), jobId, cancellationToken);

    /// <inheritdoc />
    public async Task<UsenetJobInfo?> GetAsync(DownloadClient client, string jobId, CancellationToken cancellationToken)
    {
        var settings = Settings(client);
        var queued = await _proxy.FindInQueueAsync(settings, jobId, cancellationToken).ConfigureAwait(false);

        if (queued is not null)
        {
            return new UsenetJobInfo(
                jobId,
                queued.Name,
                MapStatus(queued.Status),
                Math.Clamp(queued.Percentage / 100, 0, 1),
                queued.SizeBytes,
                null,
                null);
        }

        var finished = await _proxy.FindInHistoryAsync(settings, jobId, cancellationToken).ConfigureAwait(false);

        if (finished is null)
        {
            return null;
        }

        var status = MapStatus(finished.Status);

        return new UsenetJobInfo(
            jobId,
            finished.Name,
            status,
            status == UsenetStatus.Completed ? 1 : 0,
            finished.SizeBytes,
            finished.Storage is { } storage ? RemotePathMapper.Map(storage, settings.RemotePathMappings) : null,
            finished.FailMessage);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(DownloadClient client, string jobId, bool deleteFiles, CancellationToken cancellationToken)
    {
        var settings = Settings(client);

        if (await _proxy.FindInQueueAsync(settings, jobId, cancellationToken).ConfigureAwait(false) is not null)
        {
            try
            {
                await _proxy.RemoveFromQueueAsync(settings, jobId, deleteFiles, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DownloadClientException)
            {
                // The job finished between the two calls: it is in the history now.
            }
        }

        await _proxy.RemoveFromHistoryAsync(settings, jobId, deleteFiles, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>SABnzbd's status words (Lidarr's <c>SabnzbdDownloadStatus</c>) in Wondarr's terms.</summary>
    /// <param name="status">The word SABnzbd reported.</param>
    public static UsenetStatus MapStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "grabbing" or "queued" or "propagating" => UsenetStatus.Queued,
        "paused" => UsenetStatus.Paused,
        "downloading" or "fetching" or "checking" or "quickcheck" => UsenetStatus.Downloading,
        "verifying" => UsenetStatus.Verifying,
        "repairing" => UsenetStatus.Repairing,
        "extracting" or "running" => UsenetStatus.Extracting,
        "moving" => UsenetStatus.Moving,
        "completed" => UsenetStatus.Completed,
        "failed" or "deleted" => UsenetStatus.Failed,
        _ => UsenetStatus.Queued,
    };

    private static SabnzbdSettings Settings(DownloadClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(client.Settings) ? "{}" : client.Settings);

        return SabnzbdSettings.FromJson(document.RootElement);
    }
}
