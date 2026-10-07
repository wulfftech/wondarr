using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FakeSlskd;

/// <summary>
/// A stand-in yt-dlp for the Phase 4 gate, run as <c>slskd --fake-ytdlp &lt;yt-dlp args&gt;</c> through a
/// one-line wrapper script (the image has no Python, and the fake is already a self-contained binary
/// in the container). It answers exactly the surface <c>YtDlpRunner</c> uses — <c>--version</c>, the
/// <c>-F</c> formats probe and the download with <c>--print after_move:filepath</c> — from the gate
/// scenario (<c>FAKE_YT_SCENARIO</c>, <c>scripts/phase4-scenario.py</c>): a video is a real encoded
/// Opus file (ffmpeg), or a failure with the exact stderr string <c>YtDlpErrorTaxonomy</c> matches. A
/// video with an identity registers its fingerprint with the AcoustID stub, so verification passes.
/// Ported from the gate's earlier Python FakeYT (tools/FakeYT/yt-dlp.py).
/// </summary>
public static partial class FakeYtDlp
{
    /// <summary>The argument that switches the fake binary into yt-dlp mode.</summary>
    public const string Switch = "--fake-ytdlp";

    private const string DefaultVersion = "2026.08.19";

    /// <summary>The exact stderr strings the app's error taxonomy matches.</summary>
    public static readonly IReadOnlyDictionary<string, string> Failures = new Dictionary<string, string>
    {
        ["bot-check"] = "ERROR: [youtube] <id>: Sign in to confirm you're not a bot. This helps protect our community. Learn more",
        ["rate-limited"] = "ERROR: [youtube] <id>: This content isn't available, try again later. (Client 429)",
        ["geo"] = "ERROR: [youtube] <id>: The uploader has not made this video available in your country",
        ["age-gated"] = "ERROR: [youtube] <id>: Login details are needed to download this content (age-restricted video)",
        ["private"] = "ERROR: [youtube] <id>: This video is private",
        ["unavailable"] = "ERROR: [youtube] <id>: Video unavailable",
    };

    /// <summary>Runs one yt-dlp invocation and returns its exit code.</summary>
    /// <param name="args">yt-dlp's arguments.</param>
    /// <param name="stdout">Where yt-dlp's standard output goes.</param>
    /// <param name="stderr">Where its errors go.</param>
    /// <returns>0 on success, 1 on a failure, as yt-dlp exits.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var scenario = LoadScenario(Environment.GetEnvironmentVariable("FAKE_YT_SCENARIO"));

        if (args.Length > 0 && args[0] == "--version")
        {
            await stdout.WriteLineAsync(scenario.TryGetProperty("version", out var version) ? version.GetString() : DefaultVersion).ConfigureAwait(false);
            return 0;
        }

        var joined = string.Join(' ', args);
        var url = WatchUrl().Match(joined);
        var videoId = url.Success ? url.Groups[1].Value : "unknown";
        var entry = Video(scenario, videoId);
        var kind = entry is { } e && e.TryGetProperty("kind", out var k) ? k.GetString() ?? "ok" : "ok";

        if (Failures.TryGetValue(kind, out var failure))
        {
            await stderr.WriteLineAsync(failure.Replace("<id>", videoId, StringComparison.Ordinal)).ConfigureAwait(false);
            return 1;
        }

        if (args.Contains("-F"))
        {
            // The formats probe: a listing with format rows, so the runner's probe passes.
            await stdout.WriteAsync(
                $"[info] {videoId}: Downloading webpage\n"
                + "ID  EXT   RESOLUTION CHOPS FILESIZE   TBR PROTO INFOCODEC VCODEC    ACODEC   MORE INFO\n"
                + "251 opus audio only        3.2MiB   160k https          unknown    opus   [default]\n"
                + "140 m4a  audio only        2.1MiB   128k https          unknown    mp4a   \n").ConfigureAwait(false);
            return 0;
        }

        if (!url.Success)
        {
            await stderr.WriteLineAsync("ERROR: no watch URL in the download arguments").ConfigureAwait(false);
            return 1;
        }

        var template = OutputTemplate(args);

        if (template is null)
        {
            await stderr.WriteLineAsync("ERROR: no -o template in the download arguments").ConfigureAwait(false);
            return 1;
        }

        var target = await GenerateAsync(videoId, entry, template).ConfigureAwait(false);

        // --print after_move:filepath: the moved file's path is the last line of stdout.
        await stdout.WriteLineAsync(target).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The value after <c>-o</c>.</summary>
    private static string? OutputTemplate(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "-o")
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static JsonElement LoadScenario(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return JsonDocument.Parse("{}").RootElement;
        }

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static JsonElement? Video(JsonElement scenario, string videoId) =>
        scenario.TryGetProperty("videos", out var videos)
        && videos.ValueKind == JsonValueKind.Object
        && videos.TryGetProperty(videoId, out var entry)
        && entry.ValueKind == JsonValueKind.Object
            ? entry
            : null;

    private static double Number(JsonElement? entry, string name, double fallback) =>
        entry is { } e && e.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : fallback;

    /// <summary>Writes the Opus file yt-dlp would have produced and returns its path.</summary>
    private static async Task<string> GenerateAsync(string videoId, JsonElement? entry, string template)
    {
        var duration = Number(entry, "durationSeconds", 30);
        var seed = (int)Number(entry, "seed", Math.Abs(StringComparer.Ordinal.GetHashCode(videoId)) % 100000);
        var frequency = Number(entry, "frequency", 440);

        // The template is <dir>/%(id)s.%(ext)s; the remux lands .opus (ADR-0006).
        var target = template.Replace("%(id)s", videoId, StringComparison.Ordinal).Replace("%(ext)s", "opus", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

        string F(double value) => value.ToString(CultureInfo.InvariantCulture);

        await RunToolAsync(
            "ffmpeg",
            [
                "-nostdin", "-v", "error", "-y",
                "-f", "lavfi", "-i", $"anoisesrc=d={F(duration)}:c=pink:r=44100:a=0.25:seed={seed}",
                "-f", "lavfi", "-i", $"sine=f={F(frequency)}:d={F(duration)}:r=44100",
                "-filter_complex", "[0][1]amix=inputs=2:duration=first",
                "-ac", "2", "-c:a", "libopus", "-b:a", "160k", target,
            ]).ConfigureAwait(false);

        // The verification fingerprints this file and asks AcoustID what it is; the stub only knows
        // fingerprints it was told about, so a video with an identity registers it.
        if (entry is { } video && video.TryGetProperty("identity", out var identity) && identity.TryGetProperty("recordingId", out _))
        {
            var register = Environment.GetEnvironmentVariable("FAKE_ACOUSTID_REGISTER");
            var json = await RunToolAsync("fpcalc", ["-json", target]).ConfigureAwait(false);
            var fingerprint = JsonDocument.Parse(json).RootElement.TryGetProperty("fingerprint", out var fp) ? fp.GetString() : null;

            if (!string.IsNullOrEmpty(register) && !string.IsNullOrEmpty(fingerprint))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var response = await http.PostAsJsonAsync(register, new
                {
                    fingerprint,
                    recordingId = identity.GetProperty("recordingId").GetString(),
                    title = identity.TryGetProperty("title", out var title) ? title.GetString() : string.Empty,
                    artists = identity.TryGetProperty("artists", out var artists) ? artists : JsonDocument.Parse("[]").RootElement,
                    durationSeconds = identity.TryGetProperty("durationSeconds", out var seconds) ? seconds.GetDouble() : duration,
                }).ConfigureAwait(false);
            }
        }

        return target;
    }

    private static async Task<string> RunToolAsync(string tool, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} exited {process.ExitCode}: {await errors.ConfigureAwait(false)}");
        }

        return await output.ConfigureAwait(false);
    }

    [GeneratedRegex(@"https://music\.youtube\.com/watch\?v=([A-Za-z0-9_-]+)")]
    private static partial Regex WatchUrl();
}
