using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FakeSlskd;

/// <summary>
/// Produces the bytes a scenario file "downloads": a real encoded audio file, so the app's own
/// verification (duration, AcoustID fingerprint) sees something it can measure.
/// </summary>
public interface IAudioGenerator
{
    /// <summary>
    /// Writes <paramref name="file"/>'s audio to <paramref name="path"/>.
    /// </summary>
    /// <param name="file">The scenario file being downloaded.</param>
    /// <param name="path">Where to write the file.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    /// <returns>The file's audio fingerprint, or an empty string when it cannot be computed.</returns>
    Task<string> GenerateAsync(ScenarioFile file, string path, CancellationToken cancellationToken);
}

/// <summary>
/// The generator the container uses: <c>ffmpeg</c> builds the file from pink noise mixed with a sine,
/// and <c>fpcalc</c> fingerprints it. Distinct seeds give distinct fingerprints, which is what lets
/// the AcoustID stub tell the scenario's files apart.
/// </summary>
public sealed class FfmpegAudioGenerator : IAudioGenerator
{
    /// <inheritdoc />
    public async Task<string> GenerateAsync(ScenarioFile file, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var audio = file.Audio ?? throw new InvalidOperationException(
            $"the scenario entry for '{file.Path}' has no audio to generate");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        await RunAsync("ffmpeg", BuildArguments(audio, path), cancellationToken).ConfigureAwait(false);

        var output = await RunAsync("fpcalc", ["-json", path], cancellationToken).ConfigureAwait(false);

        return ReadFingerprint(output);
    }

    /// <summary>Whether <c>ffmpeg</c> and <c>fpcalc</c> are on this machine's <c>PATH</c>.</summary>
    public static bool IsAvailable() => FindOnPath("ffmpeg") is not null && FindOnPath("fpcalc") is not null;

    private static List<string> BuildArguments(ScenarioAudio audio, string path)
    {
        var duration = audio.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var frequency = audio.Frequency.ToString("0.###", CultureInfo.InvariantCulture);
        var seed = audio.Seed.ToString(CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "-v", "error",
            "-y",
            "-f", "lavfi",
            "-i", $"anoisesrc=d={duration}:c=pink:r=44100:a=0.25:seed={seed}",
            "-f", "lavfi",
            "-i", $"sine=f={frequency}:d={duration}:r=44100",
            "-filter_complex", "[0][1]amix=inputs=2:duration=first",
            "-ac", "2",
        };

        if (string.Equals(audio.Codec, "flac", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:a", "flac", "-sample_fmt", "s16"]);
        }
        else
        {
            arguments.AddRange(["-c:a", "libmp3lame", "-b:a", $"{audio.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k"]);
        }

        arguments.Add(path);

        return arguments;
    }

    private static string ReadFingerprint(string fpcalcOutput)
    {
        if (string.IsNullOrWhiteSpace(fpcalcOutput))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(fpcalcOutput);

        return document.RootElement.TryGetProperty("fingerprint", out var fingerprint)
            ? fingerprint.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {executable}");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}: {errors}");
        }

        return output;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }
}
