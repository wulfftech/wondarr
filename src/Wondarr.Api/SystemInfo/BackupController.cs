// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/Backup/BackupController.cs, GPL-3.0.
// Adapted for Wondarr: the backup is made directly rather than through a command, the id is the
// backup's file name, a restore is staged and applied on the next start (Lidarr swaps the files in
// place), and validation failures answer 400 problem details rather than throwing.

using Wondarr.Core.Backup;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.SystemInfo;

/// <summary>
/// The System → Backup endpoints: list, back up now, download, delete, and stage a restore from a
/// stored backup or an uploaded zip. A restore never touches the live files: it is validated,
/// staged under <c>&lt;ConfigDir&gt;/restore/</c> and applied before the database opens on the next
/// start, which is why every restore answers <c>restartRequired</c> and stops the app.
/// </summary>
[ApiController]
[Route("api/v1/system/backup")]
public sealed class BackupController : ControllerBase
{
    /// <summary>The largest restore archive that can be uploaded.</summary>
    public const long MaximumUploadBytes = 512 * 1024 * 1024;

    private readonly IBackupService _backups;
    private readonly IApplicationShutdown _shutdown;

    /// <summary>Initialises a new instance of the <see cref="BackupController"/> class.</summary>
    /// <param name="backups">The service that writes, lists and stages the backups.</param>
    /// <param name="shutdown">Stops the app after the response is written, so the restore applies.</param>
    public BackupController(IBackupService backups, IApplicationShutdown shutdown)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(shutdown);

        _backups = backups;
        _shutdown = shutdown;
    }

    /// <summary>Lists the backups, newest first.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<BackupResource>>> GetBackups(CancellationToken cancellationToken)
    {
        var backups = await _backups.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(backups.Select(ToResource).ToList());
    }

    /// <summary>Creates a manual backup now.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>201 with the new backup.</returns>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType<BackupResource>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BackupResource>> CreateBackup(CancellationToken cancellationToken)
    {
        var backup = await _backups.CreateAsync(BackupType.Manual, cancellationToken).ConfigureAwait(false);

        // There is no single-backup GET to point a Location header at: the list is where it shows up.
        return StatusCode(StatusCodes.Status201Created, ToResource(backup));
    }

    /// <summary>Deletes one backup.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <returns>204, or 404 when no such backup exists.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteBackup(string id) => _backups.Delete(id) ? NoContent() : NotFound();

    /// <summary>Downloads one backup.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <returns>The zip, or 404 when no such backup exists.</returns>
    [HttpGet("{id}/download")]
    [Produces("application/zip")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "application/zip")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DownloadBackup(string id)
    {
        var zip = _backups.OpenRead(id);

        return zip is null
            ? NotFound()
            : File(zip, "application/zip", Path.GetFileName(id));
    }

    /// <summary>Stages a restore from a stored backup.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>{ restartRequired: true }</c>, or 400 problem details when the archive is not a valid backup.</returns>
    [HttpPost("restore/{id}")]
    [Produces("application/json")]
    [ProducesResponseType<RestoreResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RestoreBackup(string id, CancellationToken cancellationToken) =>
        await StageRestoreAsync(() => _backups.StageRestoreAsync(id, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Stages a restore from an uploaded backup.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>{ restartRequired: true }</c>, or 400 problem details when the archive is not a valid backup.</returns>
    [HttpPost("restore/upload")]
    [RequestSizeLimit(MaximumUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumUploadBytes)]
    [Produces("application/json")]
    [ProducesResponseType<RestoreResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadAndRestoreBackup(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        if (form.Files.Count != 1)
        {
            return Problem(
                title: "Invalid restore upload",
                detail: "Upload exactly one zip file.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var zip = form.Files[0].OpenReadStream();

        return await StageRestoreAsync(
            () => _backups.StageRestoreAsync(zip, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IActionResult> StageRestoreAsync(
        Func<Task<StagedRestoreResult>> stage,
        CancellationToken cancellationToken)
    {
        var result = await stage().ConfigureAwait(false);

        if (!result.Staged)
        {
            return Problem(
                title: "Invalid backup",
                detail: result.Reason,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The response is written before the app stops, and the supervisor starts it again; the
        // next start applies the restore.
        _shutdown.StopAfterResponse();

        return Ok(new RestoreResource(RestartRequired: true));
    }

    private static BackupResource ToResource(BackupItem backup) => new(
        backup.Id,
        backup.Name,
        $"/backup/{backup.Type.ToString().ToLowerInvariant()}/{backup.Name}",
        backup.Type.ToString().ToLowerInvariant(),
        backup.Size,
        backup.Time);
}
