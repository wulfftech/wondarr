namespace Compilarr.Core.HealthCheck;

/// <summary>Base for the checks that prove a directory Compilarr owns can be written to.</summary>
public abstract class FolderHealthCheck : IHealthCheck
{
    /// <summary>Initialises a new instance of the <see cref="FolderHealthCheck"/> class.</summary>
    /// <param name="folderPath">The absolute directory to probe.</param>
    protected FolderHealthCheck(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        FolderPath = folderPath;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>The directory this check proves writable.</summary>
    protected string FolderPath { get; }

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var probeFile = Path.Combine(FolderPath, $".compilarr-health-{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(FolderPath);
            await File.WriteAllTextAsync(probeFile, string.Empty, cancellationToken).ConfigureAwait(false);

            return new HealthCheck(Name, HealthCheckResult.Ok, $"{FolderPath} is writable", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HealthCheck(Name, HealthCheckResult.Error, $"{FolderPath} is not writable: {exception.Message}", null);
        }
        finally
        {
            TryDeleteProbeFile(probeFile);
        }
    }

    private static void TryDeleteProbeFile(string probeFile)
    {
        try
        {
            File.Delete(probeFile);
        }
        catch (IOException)
        {
            // A leftover probe file is not worth failing the check over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the result already says whether the folder is writable.
        }
    }
}