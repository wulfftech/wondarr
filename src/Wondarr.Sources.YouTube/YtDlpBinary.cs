using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Sources.YouTube;

/// <summary>Which yt-dlp the effective binary is.</summary>
public enum YtDlpBinarySource
{
    /// <summary>Wondarr's own copy under the config directory, which the update task keeps current.</summary>
    Managed,

    /// <summary>The copy the image bundles (<c>youtube.ytdlp.binary_path</c>, <c>yt-dlp</c> by default).</summary>
    Bundled,

    /// <summary>A binary the user pointed <c>binary_path</c> at; Wondarr never manages it.</summary>
    Custom,
}

/// <summary>The binary every yt-dlp call runs, and why.</summary>
/// <param name="Path">What to hand to <see cref="IProcessRunner"/>.</param>
/// <param name="Source">Which of the three it is.</param>
/// <param name="BundledVersion">The bundled binary's version when it was compared with the managed copy, or <see langword="null"/>.</param>
/// <param name="FallbackReason">Why the managed copy was passed over, when it was broken; <see langword="null"/> otherwise.</param>
/// <param name="Generation">Changes whenever the answer may have changed, so a cached probe can tell it is stale.</param>
public sealed record YtDlpResolution(
    string Path,
    YtDlpBinarySource Source,
    string? BundledVersion,
    string? FallbackReason,
    int Generation);

/// <summary>What the settings page and the health check need to know about the update.</summary>
/// <param name="Source">Which binary runs.</param>
/// <param name="BundledVersion">The bundled binary's version, when known.</param>
/// <param name="AutoUpdate">The <c>youtube.ytdlp.auto_update</c> setting.</param>
/// <param name="UpdateChannel">The <c>youtube.ytdlp.update_channel</c> setting.</param>
/// <param name="UpdateActive">Whether the update task would do anything now: auto-update on, no custom path, and the source or music videos on.</param>
/// <param name="LastCheckedAt">When the last check ran, whatever it found; <see langword="null"/> before the first.</param>
/// <param name="LastCheckSucceeded">Whether the last check reached yt-dlp's updater and it finished; <see langword="null"/> before the first.</param>
/// <param name="LastCheckMessage">What the last check found, in a sentence.</param>
/// <param name="LastSuccessAt">When a check last succeeded.</param>
/// <param name="LatestVersion">The newest release that last successful check saw.</param>
/// <param name="StartedAt">When this process began watching, the reference point until a first check succeeds.</param>
/// <param name="JsRuntimeWarning">yt-dlp's complaint that the JS runtime is unsupported or too old, when it made one.</param>
/// <param name="FallbackReason">Why the managed copy is not in use, when it is broken.</param>
public sealed record YtDlpUpdateStatus(
    YtDlpBinarySource Source,
    string? BundledVersion,
    bool AutoUpdate,
    string UpdateChannel,
    bool UpdateActive,
    DateTimeOffset? LastCheckedAt,
    bool? LastCheckSucceeded,
    string? LastCheckMessage,
    DateTimeOffset? LastSuccessAt,
    string? LatestVersion,
    DateTimeOffset StartedAt,
    string? JsRuntimeWarning,
    string? FallbackReason);

/// <summary>How an update run ended.</summary>
public enum YtDlpUpdateRunKind
{
    /// <summary>It did nothing, and made no network call: auto-update is off, nothing uses yt-dlp, or the path is custom.</summary>
    Skipped,

    /// <summary>The managed copy already was the newest release.</summary>
    UpToDate,

    /// <summary>The managed copy was replaced by a newer release, and answered <c>--version</c>.</summary>
    Updated,

    /// <summary>It could not finish; the binary in use is unchanged.</summary>
    Failed,
}

/// <summary>The result of one update run.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Message">One sentence for the task list.</param>
public sealed record YtDlpUpdateRun(YtDlpUpdateRunKind Kind, string Message);

/// <summary>
/// Which yt-dlp binary runs, and keeping Wondarr's own copy of it current. Every place that runs yt-dlp
/// asks this for the path; none reads <c>youtube.ytdlp.binary_path</c> itself.
/// </summary>
public interface IYtDlpBinary
{
    /// <summary>Gets a number that changes whenever <see cref="ResolveAsync"/> may answer differently.</summary>
    int Generation { get; }

    /// <summary>The binary to run: the managed copy, the bundled one, or the user's own.</summary>
    /// <param name="cancellationToken">Cancels the first resolution, which runs <c>--version</c>.</param>
    Task<YtDlpResolution> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>What the settings page and the health check show.</summary>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    Task<YtDlpUpdateStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the resolution, so the next call decides again.</summary>
    void Invalidate();

    /// <summary>Copies the bundled binary on the first run, then asks yt-dlp's own updater for the newest release.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<YtDlpUpdateRun> UpdateAsync(CancellationToken cancellationToken);
}

/// <summary>Finds an executable the way the shell does.</summary>
public interface IExecutableLocator
{
    /// <summary>Finds <paramref name="name"/> on <c>PATH</c>, or returns it as a path when it is one.</summary>
    /// <param name="name">A bare command name, or a path.</param>
    /// <returns>The file's full path, or <see langword="null"/> when it is not there.</returns>
    string? Locate(string name);
}

/// <summary>Searches <c>PATH</c> (and <c>PATHEXT</c> on Windows).</summary>
public sealed class PathExecutableLocator : IExecutableLocator
{
    /// <inheritdoc />
    public string? Locate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Path.IsPathRooted(name) || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), name + extension);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <inheritdoc cref="IYtDlpBinary" />
/// <remarks>
/// <para>
/// The container runs as an unprivileged user that cannot write to the bundled binary, so the updater works on a
/// copy under <c>&lt;config dir&gt;/tools/yt-dlp</c>. The first update run puts the bundled binary there (through a
/// temporary neighbour and a rename, so the swap is atomic) and never writes over the bundled one. Updating is
/// yt-dlp's own <c>--update-to</c>: it picks the asset, checks the published SHA2-256SUMS and replaces the file by
/// rename, so a download already running keeps its own process and the next one finds the new file. The update takes
/// no part in the download gate for that reason.
/// </para>
/// <para>
/// The managed copy is used only while auto-update is on, the file exists, it answered <c>--version</c> when last
/// looked at, and the bundled binary is not newer (a new image can ship a newer yt-dlp than the copy holds). A custom
/// <c>binary_path</c> is returned as it is and never copied or updated.
/// </para>
/// </remarks>
public sealed partial class YtDlpBinary : IYtDlpBinary, IDisposable
{
    /// <summary>The longest a <c>--version</c> probe may run.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest the updater may run: it downloads one executable of a few tens of megabytes.</summary>
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly IOptionsMonitor<MusicVideoOptions> _musicVideos;
    private readonly IExecutableLocator _locator;
    private readonly TimeProvider _time;
    private readonly ILogger<YtDlpBinary> _logger;
    private readonly string _directory;
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly DateTimeOffset _startedAt;

    private Decision? _decision;
    private int _generation;
    private DateTimeOffset? _lastCheckedAt;
    private bool? _lastCheckSucceeded;
    private string? _lastCheckMessage;
    private DateTimeOffset? _lastSuccessAt;
    private string? _latestVersion;
    private string? _jsRuntimeWarning;

    /// <summary>Initialises a new instance of the <see cref="YtDlpBinary"/> class.</summary>
    /// <param name="runner">Runs yt-dlp without a shell.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="musicVideos">The <c>music_videos</c> section, which also uses yt-dlp.</param>
    /// <param name="paths">Where the configuration directory is.</param>
    /// <param name="locator">Finds the bundled binary on <c>PATH</c>.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Logs a fallback or a failed update at Warning.</param>
    public YtDlpBinary(
        IProcessRunner runner,
        IOptionsMonitor<YouTubeOptions> options,
        IOptionsMonitor<MusicVideoOptions> musicVideos,
        WondarrPaths paths,
        IExecutableLocator locator,
        TimeProvider time,
        ILogger<YtDlpBinary> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(musicVideos);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _options = options;
        _musicVideos = musicVideos;
        _locator = locator;
        _time = time;
        _logger = logger;
        _directory = Path.Combine(paths.ConfigDir, "tools", "yt-dlp");
        _startedAt = time.GetUtcNow();
    }

    /// <summary>Gets where the managed copy lives: <c>&lt;config dir&gt;/tools/yt-dlp/yt-dlp</c> (<c>yt-dlp.exe</c> on Windows).</summary>
    public string ManagedPath => Path.Combine(_directory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>Gets where the copy from before the last update is kept.</summary>
    public string PreviousPath => Path.Combine(_directory, "yt-dlp.previous");

    /// <inheritdoc />
    public int Generation => Volatile.Read(ref _generation);

    /// <inheritdoc />
    public void Dispose()
    {
        _resolveGate.Dispose();
        _updateGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        Volatile.Write(ref _decision, null);
        Interlocked.Increment(ref _generation);
    }

    /// <inheritdoc />
    public async Task<YtDlpResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var ytdlp = _options.CurrentValue.Ytdlp;
        var key = (ytdlp.BinaryPath, ytdlp.AutoUpdate);

        if (Volatile.Read(ref _decision) is { } cached && cached.Key == key)
        {
            return cached.Resolution;
        }

        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _decision) is { } again && again.Key == key)
            {
                return again.Resolution;
            }

            var resolution = await DecideAsync(ytdlp, cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _decision, new Decision(key, resolution));

            return resolution;
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<YtDlpUpdateStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.CurrentValue;

        return new YtDlpUpdateStatus(
            resolution.Source,
            resolution.BundledVersion,
            options.Ytdlp.AutoUpdate,
            options.Ytdlp.UpdateChannel,
            SkipReason(options) is null,
            _lastCheckedAt,
            _lastCheckSucceeded,
            _lastCheckMessage,
            _lastSuccessAt,
            _latestVersion,
            _startedAt,
            _jsRuntimeWarning,
            resolution.FallbackReason);
    }

    /// <inheritdoc />
    public async Task<YtDlpUpdateRun> UpdateAsync(CancellationToken cancellationToken)
    {
        // Nothing below this point runs, and nothing touches the network, when one of these holds.
        if (SkipReason(_options.CurrentValue) is { } skipped)
        {
            return new YtDlpUpdateRun(YtDlpUpdateRunKind.Skipped, skipped);
        }

        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Read again under the gate: the settings may have changed while another run held it.
            if (SkipReason(_options.CurrentValue) is { } skippedNow)
            {
                return new YtDlpUpdateRun(YtDlpUpdateRunKind.Skipped, skippedNow);
            }

            var run = await UpdateCoreAsync(_options.CurrentValue.Ytdlp, cancellationToken).ConfigureAwait(false);

            // Whatever happened, the next call decides afresh: the file, its version or the fallback may differ.
            Invalidate();

            return run;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    /// <summary>Why the update has nothing to do, or <see langword="null"/> when it has.</summary>
    private string? SkipReason(YouTubeOptions options)
    {
        if (!options.Ytdlp.AutoUpdate)
        {
            return "Automatic yt-dlp updates are off";
        }

        if (options.Ytdlp.HasCustomBinaryPath)
        {
            return "A custom yt-dlp path is set, so Wondarr leaves it alone";
        }

        if (!options.Enabled && !_musicVideos.CurrentValue.Enabled)
        {
            return "Neither the YouTube source nor music videos is on";
        }

        return null;
    }

    private async Task<YtDlpResolution> DecideAsync(YtDlpOptions ytdlp, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);

        if (ytdlp.HasCustomBinaryPath)
        {
            return new YtDlpResolution(ytdlp.BinaryPath, YtDlpBinarySource.Custom, null, null, generation);
        }

        if (!ytdlp.AutoUpdate || !File.Exists(ManagedPath))
        {
            return new YtDlpResolution(ytdlp.BinaryPath, YtDlpBinarySource.Bundled, null, null, generation);
        }

        var (managedVersion, failure, _) = await ProbeAsync(ManagedPath, cancellationToken).ConfigureAwait(false);

        if (managedVersion is null || !YtDlpVersion.TryParse(managedVersion, out _))
        {
            var reason = failure ?? $"it printed '{managedVersion}' instead of a version";

            LogManagedBroken(_logger, ManagedPath, reason);

            return new YtDlpResolution(
                ytdlp.BinaryPath,
                YtDlpBinarySource.Bundled,
                null,
                $"The updated copy of yt-dlp does not run ({reason}), so the bundled one is in use",
                generation);
        }

        // The newer copy wins: a new image can ship a newer yt-dlp than the managed copy holds.
        var (bundledVersion, _, _) = await ProbeAsync(ytdlp.BinaryPath, cancellationToken).ConfigureAwait(false);
        var bundledIsNewer = YtDlpVersion.Compare(bundledVersion, managedVersion) is > 0;

        return new YtDlpResolution(
            bundledIsNewer ? ytdlp.BinaryPath : ManagedPath,
            bundledIsNewer ? YtDlpBinarySource.Bundled : YtDlpBinarySource.Managed,
            bundledVersion,
            null,
            generation);
    }

    private async Task<YtDlpUpdateRun> UpdateCoreAsync(YtDlpOptions ytdlp, CancellationToken cancellationToken)
    {
        var bundled = _locator.Locate(ytdlp.BinaryPath);

        if (bundled is null)
        {
            return Failed("The bundled yt-dlp was not found, so there is nothing to copy", null);
        }

        string? preUpdate = null;

        try
        {
            if (!await PrepareManagedCopyAsync(bundled, cancellationToken).ConfigureAwait(false))
            {
                return Failed("Could not copy the bundled yt-dlp into the config directory", null);
            }

            // The copy that was in place before this update, kept as yt-dlp.previous once the update worked.
            preUpdate = ManagedPath + ".pre-update";
            File.Copy(ManagedPath, preUpdate, overwrite: true);

            ProcessResult result;

            try
            {
                result = await _runner
                    .RunAsync(ManagedPath, ["--update-to", ytdlp.UpdateChannel], UpdateTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MediaToolMissingException or Win32Exception or IOException or UnauthorizedAccessException)
            {
                return Failed($"yt-dlp could not be started: {exception.Message}", null);
            }

            var outcome = YtDlpUpdateOutput.Parse(result);
            var output = $"{result.StandardOutput}\n{result.StandardError}";

            if (outcome.Kind == YtDlpUpdateKind.Failed)
            {
                return Failed(outcome.Message, YtDlpUpdateOutput.FindJsRuntimeProblem(output));
            }

            var jsProblem = YtDlpUpdateOutput.FindJsRuntimeProblem(output);
            string? version = null;

            if (outcome.Kind == YtDlpUpdateKind.Updated)
            {
                // After an update the copy must answer --version; otherwise the previous one is put back.
                var (answered, failure, probeOutput) = await ProbeAsync(ManagedPath, cancellationToken).ConfigureAwait(false);

                // yt-dlp may warn about the runtime on stderr of any run, --version included.
                jsProblem ??= YtDlpUpdateOutput.FindJsRuntimeProblem(probeOutput);

                if (answered is null || !YtDlpVersion.TryParse(answered, out _))
                {
                    var reason = failure ?? $"it printed '{answered}' instead of a version";

                    RestorePrevious(preUpdate);

                    return Failed($"The updated yt-dlp does not run ({reason}); the previous copy was put back", jsProblem);
                }

                version = answered;
                File.Move(preUpdate, PreviousPath, overwrite: true);
                preUpdate = null;
                LogUpdated(_logger, version, ytdlp.UpdateChannel);
            }

            RecordCheck(succeeded: true, outcome.Message, outcome.LatestVersion ?? version ?? outcome.Version, jsProblem);

            if (jsProblem is not null)
            {
                LogRuntimeProblem(_logger, jsProblem);
            }

            return new YtDlpUpdateRun(
                outcome.Kind == YtDlpUpdateKind.Updated ? YtDlpUpdateRunKind.Updated : YtDlpUpdateRunKind.UpToDate,
                jsProblem is null ? outcome.Message : $"{outcome.Message}. {jsProblem}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed($"A file operation failed: {exception.Message}", null);
        }
        finally
        {
            TryDelete(preUpdate);
        }
    }

    /// <summary>
    /// Makes sure the managed copy exists and is at least as new as the bundled binary: the first run copies it, and
    /// a copy that does not answer, or that a newer image has overtaken, is replaced by the bundled one.
    /// </summary>
    private async Task<bool> PrepareManagedCopyAsync(string bundled, CancellationToken cancellationToken)
    {
        if (File.Exists(ManagedPath))
        {
            var (managedVersion, _, _) = await ProbeAsync(ManagedPath, cancellationToken).ConfigureAwait(false);
            var (bundledVersion, _, _) = await ProbeAsync(bundled, cancellationToken).ConfigureAwait(false);

            var broken = managedVersion is null || !YtDlpVersion.TryParse(managedVersion, out _);
            var overtaken = YtDlpVersion.Compare(bundledVersion, managedVersion) is > 0;

            if (!broken && !overtaken)
            {
                return true;
            }
        }

        try
        {
            CopyAtomically(bundled, ManagedPath);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCopyFailed(_logger, ManagedPath, exception.Message);

            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/> to a temporary file beside <paramref name="target"/>, makes it executable and
    /// renames it over the target, so a reader sees the old file or the new one and never half of one.
    /// </summary>
    private static void CopyAtomically(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.Copy(source, temporary, overwrite: false);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Puts the copy taken before the update back over the managed one.</summary>
    private void RestorePrevious(string preUpdate)
    {
        try
        {
            CopyAtomically(preUpdate, ManagedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The resolution probes the managed copy before trusting it, so a copy that is still broken is
            // passed over for the bundled binary.
            LogCopyFailed(_logger, ManagedPath, exception.Message);
        }
    }

    private YtDlpUpdateRun Failed(string message, string? jsProblem)
    {
        RecordCheck(succeeded: false, message, latestVersion: null, jsProblem);
        LogUpdateFailed(_logger, message);

        return new YtDlpUpdateRun(YtDlpUpdateRunKind.Failed, message);
    }

    private void RecordCheck(bool succeeded, string message, string? latestVersion, string? jsProblem)
    {
        var now = _time.GetUtcNow();

        _lastCheckedAt = now;
        _lastCheckSucceeded = succeeded;
        _lastCheckMessage = message;

        if (succeeded)
        {
            _lastSuccessAt = now;
            _latestVersion = latestVersion ?? _latestVersion;
        }

        // A failed run says nothing about the runtime, so the last word on it stays.
        if (succeeded || jsProblem is not null)
        {
            _jsRuntimeWarning = jsProblem;
        }
    }

    /// <summary>Runs <c>--version</c>; the first output line when it answered, or the reason it did not.</summary>
    private async Task<(string? Version, string? Failure, string Output)> ProbeAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(executable, ["--version"], ProbeTimeout, cancellationToken).ConfigureAwait(false);
            var output = $"{result.StandardOutput}\n{result.StandardError}";

            if (result.TimedOut)
            {
                return (null, "it did not answer --version in time", output);
            }

            if (result.ExitCode != 0)
            {
                return (null, $"--version exited with code {result.ExitCode}", output);
            }

            var line = result.StandardOutput.Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0);

            return line is null ? (null, "--version printed nothing", output) : (line, null, output);
        }
        catch (Exception exception) when (exception is MediaToolMissingException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return (null, exception.Message, string.Empty);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the next run overwrites or ignores it.
        }
    }

    /// <summary>A resolution and the settings it was made for.</summary>
    private sealed record Decision((string BinaryPath, bool AutoUpdate) Key, YtDlpResolution Resolution);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The managed yt-dlp at {Path} does not run ({Reason}); using the bundled one")]
    private static partial void LogManagedBroken(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The yt-dlp update did not finish: {Reason}. The binary in use is unchanged; the next run tries again")]
    private static partial void LogUpdateFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the managed yt-dlp at {Path}: {Reason}")]
    private static partial void LogCopyFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated yt-dlp to {Version} ({Channel} channel)")]
    private static partial void LogUpdated(ILogger logger, string version, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "yt-dlp reports a problem with the JS runtime (Deno): {Detail}. Wondarr does not update Deno")]
    private static partial void LogRuntimeProblem(ILogger logger, string detail);
}
