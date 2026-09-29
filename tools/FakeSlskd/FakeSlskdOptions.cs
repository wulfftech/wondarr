namespace FakeSlskd;

/// <summary>
/// Everything the fake needs to run: the parsed <c>slskd.yml</c>, the scenario it answers from, the
/// port its AcoustID stub binds to, and the generator it produces downloads with.
/// </summary>
public sealed record FakeSlskdOptions
{
    /// <summary>Where the AcoustID stub listens unless the environment says otherwise.</summary>
    public const int DefaultAcoustIdPort = 5031;

    /// <summary>The app's rendered slskd configuration.</summary>
    public required SlskdConfiguration Configuration { get; init; }

    /// <summary>The scenario the fake answers searches and downloads from.</summary>
    public Scenario Scenario { get; init; } = Scenario.Empty;

    /// <summary>The port the AcoustID stub listens on, on loopback only.</summary>
    public int AcoustIdPort { get; init; } = DefaultAcoustIdPort;

    /// <summary>The audio generator; tests inject a fake because they do not have <c>ffmpeg</c>.</summary>
    public IAudioGenerator AudioGenerator { get; init; } = new FfmpegAudioGenerator();

    /// <summary>
    /// Reads <c>SLSKD_CONFIG</c> (falling back to <c>$SLSKD_APP_DIR/slskd.yml</c>),
    /// <c>FAKE_SLSKD_SCENARIO</c> and <c>FAKE_ACOUSTID_PORT</c>.
    /// </summary>
    public static FakeSlskdOptions FromEnvironment()
    {
        var configPath = Environment.GetEnvironmentVariable("SLSKD_CONFIG");

        if (string.IsNullOrWhiteSpace(configPath))
        {
            var appDirectory = Environment.GetEnvironmentVariable("SLSKD_APP_DIR");
            configPath = string.IsNullOrWhiteSpace(appDirectory)
                ? null
                : Path.Combine(appDirectory, "slskd.yml");
        }

        SlskdConfiguration configuration;

        if (configPath is not null && File.Exists(configPath))
        {
            FakeSlskdLog.Info($"Reading configuration from {configPath}");
            configuration = SlskdConfiguration.Load(configPath);
        }
        else
        {
            FakeSlskdLog.Error($"No slskd configuration at {configPath ?? "(unset)"}; using defaults");
            configuration = new SlskdConfiguration();
        }

        return new FakeSlskdOptions
        {
            Configuration = configuration,
            Scenario = Scenario.Load(Environment.GetEnvironmentVariable("FAKE_SLSKD_SCENARIO")),
            AcoustIdPort = ReadPort(Environment.GetEnvironmentVariable("FAKE_ACOUSTID_PORT")),
        };
    }

    private static int ReadPort(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535
                ? port
                : DefaultAcoustIdPort;
}