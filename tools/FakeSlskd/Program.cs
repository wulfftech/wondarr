namespace FakeSlskd;

/// <summary>
/// Entry point of the fake. The app launches it exactly as it launches slskd — through
/// <c>APP__SOULSEEK__BINARY_PATH</c>, with <c>SLSKD_CONFIG</c> pointing at the rendered
/// <c>slskd.yml</c> — so it is a single process that serves the slskd API on <c>web.port</c> and the
/// AcoustID stub on its own loopback port.
/// </summary>
public static class Program
{
    /// <summary>Runs the fake until the host is stopped.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        // The same binary stands in for yt-dlp in the Phase 4 gate (the image ships no Python).
        if (args.Length > 0 && args[0] == FakeYtDlp.Switch)
        {
            return await FakeYtDlp.RunAsync(args[1..], Console.Out, Console.Error).ConfigureAwait(false);
        }

        var options = FakeSlskdOptions.FromEnvironment();

        using var state = new FakeSlskdState(options);

        var app = FakeSlskdApp.Build(options, state);
        var acoustId = AcoustIdStubApp.Build(options, state);
        var innertube = InnertubeStubApp.Build(options, state);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            await acoustId.StartAsync().ConfigureAwait(false);
            await innertube.StartAsync().ConfigureAwait(false);

            FakeSlskdLog.Info($"Listening on {options.Configuration.WebIpAddress}:{options.Configuration.WebPort}");
            FakeSlskdLog.Info($"AcoustID stub listening on 127.0.0.1:{options.AcoustIdPort}");
            FakeSlskdLog.Info($"InnerTube stub listening on 127.0.0.1:{options.InnertubePort}");

            if (options.Configuration.SoulseekUsername is { } username)
            {
                // The app's log watcher reads this line to learn who its slskd is logged in as.
                FakeSlskdLog.Info($"Logged in to the Soulseek server as {username}");
            }
            else
            {
                FakeSlskdLog.Error("No Soulseek username is configured; reporting as logged out");
            }

            await Task.WhenAll(
                app.WaitForShutdownAsync(),
                acoustId.WaitForShutdownAsync(),
                innertube.WaitForShutdownAsync()).ConfigureAwait(false);

            return 0;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            await acoustId.DisposeAsync().ConfigureAwait(false);
            await innertube.DisposeAsync().ConfigureAwait(false);
        }
    }
}
