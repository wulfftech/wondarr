namespace FakeSlskd;

/// <summary>
/// Everything the fake needs to run: the parsed <c>slskd.yml</c>, the scenario it answers from, the
/// port its AcoustID stub binds to, and the generator it produces downloads with.
/// </summary>
public sealed record FakeSlskdOptions
{
    /// <summary>Where the AcoustID stub listens unless the environment says otherwise.</summary>
    public const int DefaultAcoustIdPort = 5031;

    /// <summary>Where the InnerTube stub listens unless the environment says otherwise.</summary>
    public const int DefaultInnertubePort = 5032;

    /// <summary>The loopback port of the fake Plex Media Server (and plex.tv) when <c>FAKE_PLEX_PORT</c> is not set.</summary>
    public const int DefaultPlexPort = 5033;

    /// <summary>The Phase 7 stub's port: the indexers, qBittorrent and SABnzbd (<see cref="ContainerStubApp"/>).</summary>
    public const int DefaultContainersPort = 5034;

    /// <summary>The app's rendered slskd configuration.</summary>
    public required SlskdConfiguration Configuration { get; init; }

    /// <summary>The scenario the fake answers searches and downloads from.</summary>
    public Scenario Scenario { get; init; } = Scenario.Empty;

    /// <summary>The port the AcoustID stub listens on, on loopback only.</summary>
    public int AcoustIdPort { get; init; } = DefaultAcoustIdPort;

    /// <summary>The port the InnerTube stub listens on, on loopback only.</summary>
    public int InnertubePort { get; init; } = DefaultInnertubePort;

    /// <summary>Gets the loopback port the fake Plex listens on.</summary>
    public int PlexPort { get; init; } = DefaultPlexPort;

    /// <summary>The port the Phase 7 stub listens on, loopback only.</summary>
    public int ContainersPort { get; init; } = DefaultContainersPort;

    /// <summary>Where the Phase 7 stub's qBittorrent and SABnzbd keep their downloads.</summary>
    public string ContainersRoot { get; init; } = ContainerStubApp.DefaultRoot;

    /// <summary>The directory of recorded InnerTube responses the stub answers from.</summary>
    public string? InnertubeFixtureDir { get; init; }

    /// <summary>The Phase 4 gate scenario (<c>FAKE_YT_SCENARIO</c>) the InnerTube stub answers gate songs from.</summary>
    public string? YouTubeScenarioPath { get; init; }

    /// <summary>The audio generator; tests inject a fake because they do not have <c>ffmpeg</c>.</summary>
    public IAudioGenerator AudioGenerator { get; init; } = new FfmpegAudioGenerator();

    /// <summary>
    /// Where the share scan is kept between runs, or <see langword="null"/> to scan at every start. Like
    /// slskd, a fake that finds one restores it instead of scanning (<c>$SLSKD_APP_DIR/fake-share-cache.json</c>).
    /// </summary>
    public string? ShareCachePath { get; init; }

    /// <summary>
    /// Reads <c>SLSKD_CONFIG</c> (falling back to <c>$SLSKD_APP_DIR/slskd.yml</c>),
    /// <c>FAKE_SLSKD_SCENARIO</c> and <c>FAKE_ACOUSTID_PORT</c>.
    /// </summary>
    public static FakeSlskdOptions FromEnvironment()
    {
        var configPath = Environment.GetEnvironmentVariable("SLSKD_CONFIG");
        var appDirectory = Environment.GetEnvironmentVariable("SLSKD_APP_DIR");

        if (string.IsNullOrWhiteSpace(configPath))
        {
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
            AcoustIdPort = ReadPort(Environment.GetEnvironmentVariable("FAKE_ACOUSTID_PORT"), DefaultAcoustIdPort),
            InnertubePort = ReadPort(Environment.GetEnvironmentVariable("FAKE_INNERTUBE_PORT"), DefaultInnertubePort),
            PlexPort = ReadPort(Environment.GetEnvironmentVariable("FAKE_PLEX_PORT"), DefaultPlexPort),
            ContainersPort = ReadPort(Environment.GetEnvironmentVariable("FAKE_CONTAINERS_PORT"), DefaultContainersPort),
            ContainersRoot = Environment.GetEnvironmentVariable("FAKE_CONTAINERS_ROOT") is { Length: > 0 } root ? root : ContainerStubApp.DefaultRoot,
            InnertubeFixtureDir = Environment.GetEnvironmentVariable("FAKE_INNERTUBE_FIXTURES"),
            YouTubeScenarioPath = Environment.GetEnvironmentVariable("FAKE_YT_SCENARIO"),
            ShareCachePath = string.IsNullOrWhiteSpace(appDirectory) ? null : Path.Combine(appDirectory, "fake-share-cache.json"),
        };
    }

    // Each port falls back to its own default: one shared fallback put the InnerTube stub on the AcoustID
    // stub's port whenever FAKE_INNERTUBE_PORT was unset, and the fake died binding it twice.
    public static int ReadPort(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535
                ? port
                : fallback;
}
