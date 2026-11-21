// Reads the output of yt-dlp's own updater (https://github.com/yt-dlp/yt-dlp, Unlicense): the strings below are the
// ones yt_dlp/update.py prints at tag 2026.10.26. Nothing is ported; the strings are matched.

using System.Text.RegularExpressions;
using Wondarr.Core.Media;

namespace Wondarr.Sources.YouTube;

/// <summary>How a <c>yt-dlp --update-to</c> run ended.</summary>
public enum YtDlpUpdateKind
{
    /// <summary>The updater replaced the binary with a newer release.</summary>
    Updated,

    /// <summary>The binary already is the newest release of the channel.</summary>
    UpToDate,

    /// <summary>The updater could not finish: no network, GitHub down or rate limiting, a file it may not write.</summary>
    Failed,
}

/// <summary>What the updater said.</summary>
/// <param name="Kind">How the run ended.</param>
/// <param name="Version">The version the updater reported for this binary after the run (the new one, or the current one), or <see langword="null"/>.</param>
/// <param name="LatestVersion">The newest release the updater saw, or <see langword="null"/> when it never got that far.</param>
/// <param name="Message">One sentence for the status line and the log; for a failure, the updater's own reason.</param>
public sealed record YtDlpUpdateOutcome(YtDlpUpdateKind Kind, string? Version, string? LatestVersion, string Message);

/// <summary>
/// Parses what <c>yt-dlp --update-to &lt;channel&gt;</c> prints. The updater (yt_dlp/update.py at tag 2026.10.26)
/// writes to the screen with <c>to_screen</c> (stdout) and reports problems with <c>report_error</c>
/// (stderr, <c>ERROR: …</c>, exit code 100):
/// <list type="bullet">
/// <item><c>Latest version: stable@2026.10.26 from yt-dlp/yt-dlp</c> (or <c>Requested version: …</c>): update.py,
/// <c>query_update</c>, <c>latest_or_requested</c>; the label is <c>_make_label</c>.</item>
/// <item><c>yt-dlp is up to date (stable@2026.10.26 from yt-dlp/yt-dlp)</c>: <c>query_update</c>, when there is no update.</item>
/// <item><c>Updated yt-dlp to stable@2026.10.26 from yt-dlp/yt-dlp</c>: <c>update</c>, once the new binary is in place.</item>
/// <item><c>ERROR: Unable to obtain version info (…); Please try again later or visit https://github.com/…</c>,
/// <c>ERROR: Unable to fetch updates: …</c>, <c>ERROR: Insufficient permissions to write to …</c>,
/// <c>ERROR: Unable to remove the old version</c> and the rest: <c>_report_error</c> and its helpers.</item>
/// </list>
/// </summary>
public static partial class YtDlpUpdateOutput
{
    /// <summary>The longest failure sentence kept for the UI and the log.</summary>
    private const int MaxMessageLength = 300;

    /// <summary>Reads the result of one updater run.</summary>
    /// <param name="result">What the process wrote and how it exited.</param>
    /// <returns>What the run came to.</returns>
    public static YtDlpUpdateOutcome Parse(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.TimedOut)
        {
            return new YtDlpUpdateOutcome(YtDlpUpdateKind.Failed, null, null, "yt-dlp's updater did not finish in time");
        }

        var text = $"{result.StandardOutput}\n{result.StandardError}";
        var latest = TagOf(LatestRegex().Match(text));
        var upToDate = UpToDateRegex().Match(text);
        var updated = UpdatedRegex().Match(text);
        var error = ErrorRegex().Match(text);

        if (error.Success || result.ExitCode != 0)
        {
            return new YtDlpUpdateOutcome(YtDlpUpdateKind.Failed, null, latest, FailureMessage(result, error));
        }

        if (updated.Success)
        {
            var version = TagOf(updated);

            return new YtDlpUpdateOutcome(
                YtDlpUpdateKind.Updated,
                version,
                latest ?? version,
                $"Updated yt-dlp to {version ?? "a newer release"}");
        }

        if (upToDate.Success)
        {
            var version = TagOf(upToDate);

            return new YtDlpUpdateOutcome(
                YtDlpUpdateKind.UpToDate,
                version,
                latest ?? version,
                $"yt-dlp {version} is up to date");
        }

        // Exit 0 and nothing recognised: query_update returned None without printing, which is what the
        // _update_spec lock rules do (update.py, `if not result_tag or result_tag == self.current_version`).
        // The updater found nothing it is allowed to install, so the binary is as new as it may be.
        return new YtDlpUpdateOutcome(YtDlpUpdateKind.UpToDate, null, latest, "yt-dlp has no newer release it may install");
    }

    /// <summary>
    /// Finds a line that says the JS runtime, Deno or EJS is unsupported or too old. yt-dlp words this as a
    /// warning on stderr when its extractor asks for more than the installed runtime offers.
    /// </summary>
    /// <param name="text">Output of yt-dlp: the updater's, or <c>--version</c>'s.</param>
    /// <returns>The line, trimmed, or <see langword="null"/> when there is none.</returns>
    public static string? FindJsRuntimeProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            if (RuntimeRegex().IsMatch(line) && RuntimeProblemRegex().IsMatch(line))
            {
                var trimmed = line.Trim();

                return trimmed.Length > MaxMessageLength ? trimmed[..MaxMessageLength] : trimmed;
            }
        }

        return null;
    }

    private static string FailureMessage(ProcessResult result, Match error)
    {
        var reason = error.Success
            ? error.Groups["reason"].Value.Trim()
            // The last line: a traceback ends with the exception that explains it.
            : result.StandardError.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0)
                ?? $"yt-dlp exited with code {result.ExitCode}";

        return reason.Length > MaxMessageLength ? reason[..MaxMessageLength] : reason;
    }

    /// <summary>The release tag of a label: the part after the <c>@</c>, as <c>_make_label</c> writes it.</summary>
    private static string? TagOf(Match match) =>
        match.Success ? LabelTagRegex().Match(match.Groups["label"].Value) is { Success: true } tag ? tag.Groups["tag"].Value : null : null;

    [GeneratedRegex(@"^(?:Latest|Requested) version: (?<label>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LatestRegex();

    [GeneratedRegex(@"^yt-dlp is up to date \((?<label>.+)\)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UpToDateRegex();

    [GeneratedRegex(@"^Updated yt-dlp to (?<label>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UpdatedRegex();

    [GeneratedRegex(@"^ERROR:\s*(?<reason>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"@(?<tag>[^\s)]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LabelTagRegex();

    [GeneratedRegex(@"\b(?:deno|ejs|js runtime|javascript runtime)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeRegex();

    [GeneratedRegex(@"unsupported|not supported|too old|outdated|older than|no longer supported|upgrade|update (?:deno|your)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeProblemRegex();
}
