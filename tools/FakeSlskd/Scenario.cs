using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeSlskd;

/// <summary>
/// The scenario the gate drives the fake with: the files the fake "network" offers, and the timings
/// and limits that make the fake behave like a busy slskd. See
/// <c>tests/gate/phase2-scenario.schema.md</c>.
/// </summary>
public sealed record Scenario
{
    /// <summary>The field names the scenario document uses.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>How long a submitted search stays in progress, in milliseconds.</summary>
    public int SearchDelayMs { get; init; } = 1500;

    /// <summary>How long a queued transfer stays queued, in milliseconds.</summary>
    public int TransferDelayMs { get; init; } = 1000;

    /// <summary>How many searches may be in flight before the fake answers 429.</summary>
    public int MaxInFlight { get; init; } = 2;

    /// <summary>The files the fake network offers.</summary>
    public IReadOnlyList<ScenarioFile> Files { get; init; } = [];

    /// <summary>A scenario with nothing in it: every search completes with no responses.</summary>
    public static Scenario Empty { get; } = new();

    /// <summary>Reads a scenario document.</summary>
    /// <param name="json">The scenario's JSON.</param>
    public static Scenario Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var scenario = JsonSerializer.Deserialize<Scenario>(json, SerializerOptions)
            ?? throw new InvalidOperationException("the scenario document is empty");

        return scenario;
    }

    /// <summary>
    /// Reads the scenario named by <paramref name="path"/>. A missing path (or a path that does not
    /// exist) is not an error: the fake then answers every search with no responses.
    /// </summary>
    /// <param name="path">Path of the scenario file, or <see langword="null"/>.</param>
    public static Scenario Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        return Parse(File.ReadAllText(path));
    }
}

/// <summary>What the fake served for one network search — a file inside one <see cref="ScenarioFile"/> set.</summary>
public sealed record ScenarioFile
{
    /// <summary>The peer that offers the file.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>The peer's own path, with backslash separators.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The file's size in bytes; estimated from the bitrate and length when omitted.</summary>
    public long? Size { get; init; }

    /// <summary>The bitrate in kbps; absent for lossless files.</summary>
    public int? BitRate { get; init; }

    /// <summary>The sample rate in Hz; absent for lossy files.</summary>
    public int? SampleRate { get; init; }

    /// <summary>The bit depth; absent for lossy files.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The length in seconds.</summary>
    public int Length { get; init; }

    /// <summary>Whether the peer advertises a free upload slot.</summary>
    public bool HasFreeUploadSlot { get; init; } = true;

    /// <summary>The peer's advertised upload speed in bytes per second.</summary>
    public long UploadSpeed { get; init; } = 1_000_000;

    /// <summary>How many transfers are ahead of ours in the peer's queue.</summary>
    public int QueueLength { get; init; }

    /// <summary>How the peer behaves once the file is queued: <c>ok</c>, <c>reject</c> or <c>stall</c>.</summary>
    public string Transfer { get; init; } = TransferBehaviour.Ok;

    /// <summary>How to generate the file's audio, when the transfer succeeds.</summary>
    public ScenarioAudio? Audio { get; init; }

    /// <summary>What the AcoustID stub should answer for the generated file's fingerprint.</summary>
    public ScenarioIdentity? Identity { get; init; }

    /// <summary>The file's size in bytes: the scenario's value, or an estimate from bitrate and length.</summary>
    public long EffectiveSize => Size ?? (BitRate is > 0 ? (long)BitRate * 1000 / 8 * Length : 0);

    /// <summary>The file's name without any directory part.</summary>
    public string BareName => System.IO.Path.GetFileName(Path.Replace('\\', '/'));
}

/// <summary>How a scenario file's transfer behaves once it is queued.</summary>
public static class TransferBehaviour
{
    /// <summary>The download completes.</summary>
    public const string Ok = "ok";

    /// <summary>The peer rejects the download.</summary>
    public const string Reject = "reject";

    /// <summary>The download stays in progress for ever.</summary>
    public const string Stall = "stall";
}

/// <summary>How the fake generates a file's audio (the file is written by <c>ffmpeg</c>).</summary>
public sealed record ScenarioAudio
{
    /// <summary><c>mp3</c> or <c>flac</c>.</summary>
    public string Codec { get; init; } = "mp3";

    /// <summary>Bitrate in kbps, for lossy codecs.</summary>
    public int BitrateKbps { get; init; } = 320;

    /// <summary>How long the generated file lasts.</summary>
    public double DurationSeconds { get; init; } = 8;

    /// <summary>Seed of the noise component; distinct seeds give distinct fingerprints.</summary>
    public int Seed { get; init; }

    /// <summary>Frequency of the sine component, in Hz.</summary>
    public double Frequency { get; init; } = 440;
}

/// <summary>What the AcoustID stub answers for a generated file.</summary>
public sealed record ScenarioIdentity
{
    /// <summary>The MusicBrainz recording id.</summary>
    public string RecordingId { get; init; } = string.Empty;

    /// <summary>The recording's title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The recording's artists.</summary>
    public IReadOnlyList<ScenarioArtist> Artists { get; init; } = [];

    /// <summary>The recording's length in seconds (AcoustID reports a float).</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>One artist of a <see cref="ScenarioIdentity"/>.</summary>
public sealed record ScenarioArtist
{
    /// <summary>The MusicBrainz artist id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The artist's name.</summary>
    public string Name { get; init; } = string.Empty;
}