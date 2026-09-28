using Compilarr.Core.Configuration;
using Compilarr.Core.Logging;
using Compilarr.Core.Persistence;
using Compilarr.Sources.Slskd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Compilarr.Sources.Tests.Slskd;

/// <summary>
/// Runs a real <see cref="SlskdHost"/> against fake processes, a substituted slskd client, fake
/// time and a temporary configuration directory. Nothing is started for real and no port is opened.
/// </summary>
internal sealed class SlskdHostHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _tempRoot;

    /// <param name="options">The Soulseek settings the host starts with.</param>
    /// <param name="binaryExists">
    /// Whether the file at <see cref="SoulseekOptions.BinaryPath"/> is really there. Tests that want
    /// the missing-binary path pass <see langword="false"/>.
    /// </param>
    public SlskdHostHarness(SoulseekOptions options, bool binaryExists = true)
    {
        ArgumentNullException.ThrowIfNull(options);

        _tempRoot = Path.Combine(Path.GetTempPath(), "compilarr-slskd-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        Paths = new CompilarrPaths(Path.Combine(_tempRoot, "config"));
        Options = options;
        Options.BinaryPath = Path.Combine(_tempRoot, binaryExists ? "slskd" : "not-here", "slskd");

        if (binaryExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Options.BinaryPath)!);
            File.WriteAllText(Options.BinaryPath, "#!/bin/sh\n");
        }

        Time = new FakeTimeProvider();
        Launcher = new FakeProcessLauncher(Time);
        Status = new SlskdStatus();

        CurrentOptions = Options;

        Monitor = Substitute.For<IOptionsMonitor<SoulseekOptions>>();
        Monitor.CurrentValue.Returns(_ => CurrentOptions);

        Client = Substitute.For<ISlskdClient>();
        Client.GetApplicationStateAsync(Arg.Any<CancellationToken>()).Returns(_ => Reachable
            ? Task.FromResult(ApplicationState)
            : Task.FromException<SlskdApplicationState>(new HttpRequestException("slskd is not answering")));

        Logs = new RecordingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs));

        var services = new ServiceCollection();
        services.AddSingleton(Time);
        services.AddSingleton(Paths);
        services.AddSingleton(Status);
        services.AddSingleton(Monitor);
        services.AddSingleton<ISettingsRepository>(new InMemorySettingsRepository());
        services.AddSingleton<ISecretRegistry>(new SecretRegistry());
        services.AddScoped<SlskdSecretsStore>();
        services.AddScoped(_ => Client);

        _provider = services.BuildServiceProvider();

        Host = new SlskdHost(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Launcher,
            new SlskdConfigRenderer(),
            Monitor,
            Paths,
            Status,
            Time,
            _loggerFactory);

        HealthCheck = new SlskdHealthCheck(Status, Monitor);
    }

    public FakeTimeProvider Time { get; }

    public FakeProcessLauncher Launcher { get; }

    public SlskdStatus Status { get; }

    public SoulseekOptions Options { get; }

    /// <summary>
    /// What the options monitor currently reports. A test that applies new settings sets this too,
    /// the way re-reading <c>config.yml</c> would in a running app.
    /// </summary>
    public SoulseekOptions CurrentOptions { get; set; }

    public CompilarrPaths Paths { get; }

    public IOptionsMonitor<SoulseekOptions> Monitor { get; }

    public ISlskdClient Client { get; }

    public RecordingLoggerProvider Logs { get; }

    public SlskdHost Host { get; }

    public SlskdHealthCheck HealthCheck { get; }

    /// <summary>The settings file the host rendered.</summary>
    public string ConfigPath => Host.ConfigPath;

    /// <summary>Whether the substituted slskd answers its API.</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>What slskd reports while it is reachable.</summary>
    public SlskdApplicationState ApplicationState { get; set; } = new()
    {
        Version = new SlskdVersion { Current = "0.26.0.0" },
        Server = new SlskdServer { IsLoggedIn = false },
        User = new SlskdUser { Username = string.Empty },
    };

    /// <summary>Starts the supervisor, returning as soon as it has been handed control.</summary>
    public Task StartAsync() => Host.StartAsync(CancellationToken.None);

    /// <summary>Stops the supervisor, as host shutdown would.</summary>
    public Task StopAsync() => Host.StopAsync(CancellationToken.None);

    /// <summary>Reads the rendered <c>slskd.yml</c>.</summary>
    public Task<string> ReadConfigAsync() => File.ReadAllTextAsync(ConfigPath);

    /// <summary>
    /// Waits for something the supervisor does on its own, on real time: a wait that never completes
    /// is a failure, not a hang.
    /// </summary>
    public static async Task AwaitAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = Environment.TickCount64 + timeoutMs;

        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {because}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Moves fake time forward in <paramref name="step"/>s until <paramref name="condition"/> holds.
    /// </summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step, int steps, string because)
    {
        ArgumentNullException.ThrowIfNull(condition);

        for (var index = 0; index < steps; index++)
        {
            if (condition())
            {
                return;
            }

            Time.Advance(step);

            // Give the supervisor's continuations a turn on the thread pool.
            await Task.Delay(2);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for {because} after advancing {steps} x {step}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
        _loggerFactory.Dispose();

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }
}
