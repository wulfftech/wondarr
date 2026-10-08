using Wondarr.Core.Domain;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// Drives a usenet download client: add a post paused, look at its files and drop the ones nobody
/// wants, resume it, follow it through the queue and the history, and remove it. P7-07 decides when
/// (ADR-0009; DECISIONS build session 8 #5, #10).
/// </summary>
public interface IUsenetClient
{
    /// <summary>Adds a post and returns the client's id for the job.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="request">What to add; exactly one of the file and the URL.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> AddAsync(DownloadClient client, UsenetAddRequest request, CancellationToken cancellationToken);

    /// <summary>Lists a queued job's files.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="jobId">The job's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<UsenetFileInfo>> GetFilesAsync(DownloadClient client, string jobId, CancellationToken cancellationToken);

    /// <summary>Removes one file from a queued job, so it is never downloaded.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="jobId">The job's id.</param>
    /// <param name="fileId">The file's id, as <see cref="GetFilesAsync"/> listed it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task DeleteFileAsync(DownloadClient client, string jobId, string fileId, CancellationToken cancellationToken);

    /// <summary>Resumes a paused job.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="jobId">The job's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task ResumeAsync(DownloadClient client, string jobId, CancellationToken cancellationToken);

    /// <summary>Reads a job from the queue, else from the history; <see langword="null"/> when it is in neither.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="jobId">The job's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<UsenetJobInfo?> GetAsync(DownloadClient client, string jobId, CancellationToken cancellationToken);

    /// <summary>Removes a job from the queue or the history. Only does what it is told.</summary>
    /// <param name="client">The configured client row.</param>
    /// <param name="jobId">The job's id.</param>
    /// <param name="deleteFiles">Whether the client deletes the job's files with it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task RemoveAsync(DownloadClient client, string jobId, bool deleteFiles, CancellationToken cancellationToken);
}

/// <summary>What to add to a usenet client; exactly one of <paramref name="NzbFile"/> and <paramref name="NzbUrl"/> is set.</summary>
/// <param name="Title">The job's name in the client (the release title).</param>
/// <param name="NzbFile">The NZB's bytes.</param>
/// <param name="NzbUrl">A URL the client fetches the NZB from itself.</param>
/// <param name="Paused">Whether the job is added paused, so its files can be trimmed first.</param>
public sealed record UsenetAddRequest(string Title, byte[]? NzbFile, string? NzbUrl, bool Paused);

/// <summary>Where a usenet job is, as Wondarr sees it.</summary>
public enum UsenetStatus
{
    /// <summary>Waiting for its turn, being fetched, or propagating.</summary>
    Queued,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>Articles are being downloaded or checked.</summary>
    Downloading,

    /// <summary>Par2 verification.</summary>
    Verifying,

    /// <summary>Par2 repair.</summary>
    Repairing,

    /// <summary>Unpacking, or running a post-processing script.</summary>
    Extracting,

    /// <summary>Moving the result into the complete folder.</summary>
    Moving,

    /// <summary>Done; the files are in <see cref="UsenetJobInfo.StoragePath"/>.</summary>
    Completed,

    /// <summary>Failed or deleted.</summary>
    Failed,
}

/// <summary>One usenet job.</summary>
/// <param name="JobId">The client's id for it.</param>
/// <param name="Name">The job's name.</param>
/// <param name="Status">Where it is.</param>
/// <param name="Progress">How much is downloaded, 0 to 1.</param>
/// <param name="SizeBytes">The job's size, when known.</param>
/// <param name="StoragePath">Where the finished files are, mapped to Wondarr's view; only once completed.</param>
/// <param name="FailMessage">Why it failed, when it did.</param>
public sealed record UsenetJobInfo(
    string JobId,
    string Name,
    UsenetStatus Status,
    double Progress,
    long? SizeBytes,
    string? StoragePath,
    string? FailMessage);

/// <summary>One file of a queued job.</summary>
/// <param name="FileId">The client's id for the file (SABnzbd's <c>nzf_id</c>).</param>
/// <param name="FileName">The file's name.</param>
/// <param name="SizeBytes">The file's size in bytes.</param>
public sealed record UsenetFileInfo(string FileId, string FileName, long SizeBytes);
