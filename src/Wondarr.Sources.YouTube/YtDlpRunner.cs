// Ported from yt-dlp (https://github.com/yt-dlp/yt-dlp), the download invocation its README documents
// (format selection, sleep flags, --print after_move:filepath), Unlicense.
// Ported from spotDL (https://github.com/spotDL/spotify-downloader), the pacing and retry strategy, MIT.

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Media;

namespace Wondarr.Sources.YouTube;

/// <summary>A completed download: the video id, the file yt-dlp moved into place, and its extension.</summary>
/// <param name="VideoId">The YouTube Music video id that was fetched.</param>
/// <param name="FilePath">The path yt-dlp printed with <c>--print after_move:filepath</c>.</param>
/// <param name="Extension">The file's extension, without the dot — always <c>opus</c>.</param>
public sealed record YtDlpDownload(string VideoId, string FilePath, string Extension);

/// <summary>The result of the formats probe: yt-dlp's <c>-F</c> output and whether a JS runtime answered.</summary>
/// <param name="VideoId">The probed video id.</param>
/// <param name="StandardOutput">Everything <c>yt-dlp -F</c> wrote to stdout.</param>
/// <param name="HasJsRuntime">Whether the EJS runtime (Deno) was available for extraction.</param>
public sealed record YtDlpFormats(string VideoId, string StandardOutput, bool HasJsRuntime);

/// <summary>A classified yt-dlp failure; the kind decides retry-later vs blocklist.</summary>
public sealed class YtDlpException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="YtDlpException"/> class.</summary>
    /// <param name="videoId">The video id that was being fetched.</param>
    /// <param name="kind">The classified failure.</param>
    /// <param name="message">The detail; never contains a secret.</param>
    /// <param name="innerException">The underlying exception, or <see langword="null"/>.</param>
    public YtDlpException(string videoId, YtDlpErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        VideoId = videoId;
        Kind = kind;
    }

    /// <summary>Gets the video id that failed.</summary>
    public string VideoId { get; }

    /// <summary>Gets the classified failure.</summary>
    public YtDlpErrorKind Kind { get; }

    /// <summary>Gets what the queue should do with the failure.</summary>
    public YtDlpErrorAction Action => YtDlpErrorTaxonomy.ActionFor(Kind);
}

/// <summary>
/// Shells out to the bundled yt-dlp through <see cref="IProcessRunner"/>: downloads a video's best
/// audio as a lossless Opus remux, one download at a time (YouTube's tolerance), and classifies every
/// failure into the retry-later vs blocklist taxonomy.
/// </summary>
public sealed partial class YtDlpRunner : IDisposable
{
    private const string JsRuntimeWarning = "YouTube extraction without a JS runtime has been deprecated";

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly ILogger<YtDlpRunner> _logger;
    private readonly SemaphoreSlim _gate;

    /// <summary>Initialises a new instance of the <see cref="YtDlpRunner"/> class.</summary>
    /// <param name="runner">Runs yt-dlp without a shell.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">Logs the video id and the classified kind; never the command line.</param>
    public YtDlpRunner(
        IProcessRunner runner,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpRunner> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The validator holds this at 1; the clamp is defence in depth so a misconfigured value can
        // never widen the gate past YouTube's tolerance.
        var concurrency = Math.Clamp(options.CurrentValue.Ytdlp.Concurrency, 1, 1);
        _gate = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Downloads a video's best audio as an Opus remux into <paramref name="destinationDir"/>.</summary>
    /// <param name="videoId">The YouTube Music video id.</param>
    /// <param name="destinationDir">The per-grab folder the queue item names.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    /// <returns>The completed file's path and extension.</returns>
    /// <exception cref="YtDlpException">The run failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled, or yt-dlp exited 101.</exception>
    public async Task<YtDlpDownload> DownloadAsync(
        string videoId,
        string destinationDir,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var options = _options.CurrentValue;
            var arguments = BuildDownloadArguments(videoId, destinationDir, options);

            ProcessResult result;

            try
            {
                result = await _runner.RunAsync(
                    options.Ytdlp.BinaryPath,
                    arguments,
                    TimeSpan.FromSeconds(options.Ytdlp.TimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MediaToolMissingException exception)
            {
                throw new YtDlpException(videoId, YtDlpErrorKind.ToolMissing, exception.Message, exception);
            }

            return HandleDownloadResult(videoId, result, options.Ytdlp.TimeoutSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Probes a video's formats (<c>yt-dlp -F</c>): the health probe, and the check that warns when the
    /// EJS runtime is missing.
    /// </summary>
    /// <param name="videoId">The YouTube Music video id.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The probe's stdout and whether a JS runtime was available.</returns>
    /// <exception cref="YtDlpException">The probe failed; the kind carries the classification.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled, or yt-dlp exited 101.</exception>
    public async Task<YtDlpFormats> ProbeFormatsAsync(string videoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        var options = _options.CurrentValue;

        // The probe shares the download gate: a -F probe racing a download adds requests against
        // the same YouTube tolerance the pacing flags exist for.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ProbeFormatsUngatedAsync(videoId, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<YtDlpFormats> ProbeFormatsUngatedAsync(
        string videoId,
        YouTubeOptions options,
        CancellationToken cancellationToken)
    {
        ProcessResult result;

        try
        {
            result = await _runner.RunAsync(
                options.Ytdlp.BinaryPath,
                ["-F", "--no-progress", "--", WatchUrl(videoId)],
                TimeSpan.FromSeconds(options.Ytdlp.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MediaToolMissingException exception)
        {
            throw new YtDlpException(videoId, YtDlpErrorKind.ToolMissing, exception.Message, exception);
        }

        if (result.TimedOut)
        {
            throw new YtDlpException(
                videoId,
                YtDlpErrorKind.TimedOut,
                $"yt-dlp was killed after {options.Ytdlp.TimeoutSeconds}s while probing formats.");
        }

        if (result.ExitCode == 101)
        {
            throw new OperationCanceledException($"yt-dlp cancelled the formats probe of '{videoId}'.");
        }

        if (result.ExitCode != 0)
        {
            throw Classify(videoId, result);
        }

        var hasJsRuntime = !result.StandardError.Contains(JsRuntimeWarning, StringComparison.OrdinalIgnoreCase)
            && HasFormatRows(result.StandardOutput);

        if (!hasJsRuntime)
        {
            LogJsRuntimeMissing(_logger, videoId);
        }

        return new YtDlpFormats(videoId, result.StandardOutput, hasJsRuntime);
    }

    private static string WatchUrl(string videoId) => $"https://music.youtube.com/watch?v={videoId}";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static List<string> BuildDownloadArguments(
        string videoId,
        string destinationDir,
        YouTubeOptions options)
    {
        var ytdlp = options.Ytdlp;
        var arguments = new List<string>
        {
            "-f",
            "bestaudio[acodec=opus]/bestaudio/best",
            "-x",
            "--audio-format",
            "opus",
            "--no-playlist",
            "--no-progress",
            "--retries",
            ytdlp.Retries.ToString(CultureInfo.InvariantCulture),
            "--sleep-requests",
            Number(ytdlp.SleepRequestsSeconds),
            "--sleep-interval",
            ytdlp.SleepIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-sleep-interval",
            ytdlp.MaxSleepIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            "--print",
            "after_move:filepath",
            "-o",
            $"{destinationDir}/%(id)s.%(ext)s",
        };

        if (!string.IsNullOrWhiteSpace(options.CookiesPath))
        {
            arguments.Add("--cookies");
            arguments.Add(options.CookiesPath);
        }

        if (!string.IsNullOrWhiteSpace(options.PoTokenBaseUrl))
        {
            arguments.Add("--extractor-args");
            arguments.Add($"youtubepot-bgutilhttp:base_url={options.PoTokenBaseUrl}");
        }

        arguments.Add("--");
        arguments.Add(WatchUrl(videoId));

        return arguments;
    }

    private YtDlpDownload HandleDownloadResult(string videoId, ProcessResult result, int timeoutSeconds)
    {
        if (result.TimedOut)
        {
            throw new YtDlpException(
                videoId,
                YtDlpErrorKind.TimedOut,
                $"yt-dlp was killed after {timeoutSeconds}s.");
        }

        if (result.ExitCode == 101)
        {
            throw new OperationCanceledException($"yt-dlp cancelled the download of '{videoId}'.");
        }

        if (result.ExitCode == 0)
        {
            var filePath = LastNonEmptyLine(result.StandardOutput);

            if (filePath is null)
            {
                throw new YtDlpException(
                    videoId,
                    YtDlpErrorKind.Unknown,
                    "yt-dlp exited 0 but printed no file path.");
            }

            var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (extension != "opus")
            {
                // A .webm result means the -x --audio-format opus remux did not run (ADR-0006).
                throw new YtDlpException(
                    videoId,
                    YtDlpErrorKind.Unknown,
                    $"yt-dlp produced a .{extension} file, not the expected .opus remux.");
            }

            LogDownloaded(_logger, videoId);

            return new YtDlpDownload(videoId, filePath, extension);
        }

        throw Classify(videoId, result);
    }

    private static YtDlpException Classify(string videoId, ProcessResult result)
    {
        // Exit 2 is a usage error: a flag we pass is wrong, so the stderr is the message.
        var kind = result.ExitCode == 2
            ? YtDlpErrorKind.Unknown
            : YtDlpErrorTaxonomy.Classify(result.StandardError);

        var detail = FirstNonEmptyLine(result.StandardError)
            ?? $"yt-dlp exited {result.ExitCode} with no stderr.";

        return new YtDlpException(videoId, kind, detail);
    }

    private static string? LastNonEmptyLine(string standardOutput)
    {
        string? last = null;

        foreach (var line in standardOutput.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                last = line.Trim();
            }
        }

        return last;
    }

    private static string? FirstNonEmptyLine(string standardError)
    {
        foreach (var line in standardError.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>Whether the <c>-F</c> output lists any format row (a row starts with the itag).</summary>
    private static bool HasFormatRows(string standardOutput)
    {
        foreach (var line in standardOutput.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstToken = trimmed.Split(' ')[0];

            if (firstToken.Length > 0 && firstToken.All(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded '{VideoId}' as Opus.")]
    private static partial void LogDownloaded(ILogger logger, string videoId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The formats probe of '{VideoId}' ran without a JS runtime: YouTube extraction will fail. Install Deno.")]
    private static partial void LogJsRuntimeMissing(ILogger logger, string videoId);
}
