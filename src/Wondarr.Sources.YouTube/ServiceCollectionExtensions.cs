using System.Net;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Registers the services owned by Wondarr.Sources.YouTube.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the YouTube Music search client, the yt-dlp runner, their options and the YouTube health
    /// check.
    /// </summary>
    public static IServiceCollection AddWondarrYouTube(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: a second call (a test, or a host that composes the sources itself) must not
        // duplicate the validators or the typed-client registration.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IInnertubeClient)))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);

        // The "not before" time every YouTube search shares after a 429 or 503. The typed client is
        // transient, so the state has to live in a singleton.
        services.TryAddSingleton<InnertubePause>();

        // Bound to the configuration root by the host once the settings API lands (P4-05); until then
        // the defaults apply and the validators still run.
        services.AddOptions<YouTubeOptions>().ValidateOnStart();

        // Both validators are registered against the bound YouTubeOptions type: the pipeline
        // validates the type it resolves, so an IValidateOptions<YtDlpOptions> would never be asked.
        services.AddSingleton<IValidateOptions<YouTubeOptions>, YouTubeOptionsValidator>();
        services.AddSingleton<IValidateOptions<YouTubeOptions>, YtDlpOptionsValidator>();

        // One search is one POST whose body is hundreds of kilobytes, so the headers are read first
        // and the body once. The consent cookie is set per request, so the handler must keep no cookie
        // jar of its own (one would drop the header), and decompression is on so the body is JSON.
        services.AddHttpClient<IInnertubeClient, InnertubeClient>(InnertubeClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
            });

        // The real title and channel of an upload (YouTube's keyless oEmbed): a music video candidate is checked against
        // them before it may be confident. The answers are kept for a day in a singleton, because the typed client is
        // transient; one lookup may take 5 seconds.
        services.TryAddSingleton<YouTubeOEmbedCache>();
        services.AddHttpClient<IYouTubeOEmbedClient, YouTubeOEmbedClient>(YouTubeOEmbedClient.HttpClientName, client =>
            {
                client.Timeout = YouTubeOEmbedClient.Timeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
            });

        // IProcessRunner is registered by Wondarr.Core.
        // Every yt-dlp call asks IYtDlpBinary which binary to run: Wondarr's own updated copy, the bundled one or
        // the user's. The daily UpdateYtDlp task keeps the copy current.
        services.TryAddSingleton<IExecutableLocator, PathExecutableLocator>();
        // What the last update check found is kept in the setting table, so a restart does not forget it.
        services.AddSingleton<IYtDlpUpdateStore, SettingsYtDlpUpdateStore>();
        services.AddSingleton<IYtDlpBinary, YtDlpBinary>();
        services.AddHostedService<YtDlpOverdueUpdateCheck>();
        services.AddScoped<ICommandHandler, UpdateYtDlpCommandHandler>();
        services.AddSingleton<IScheduledTaskSource, YtDlpScheduledTasks>();

        services.AddSingleton<IYtDlpRunner, YtDlpRunner>();
        services.AddSingleton<YtDlpAvailability>();
        services.AddSingleton<IHealthCheck, YtDlpHealthCheck>();

        // Music videos (ADR-0014): the finder and the downloader replace the stand-ins Wondarr.Core registers, whichever
        // of the two Add calls came first. Scoped, because the finder takes the typed InnerTube client.
        services.Replace(ServiceDescriptor.Scoped<IMusicVideoFinder, YouTubeMusicVideoFinder>());
        services.Replace(ServiceDescriptor.Scoped<IMusicVideoDownloader, YtDlpMusicVideoDownloader>());

        // The scan fills the titles of videos downloaded before the title was kept from oEmbed; without a YouTube source
        // nothing registers the backfill and the scan leaves them empty.
        services.AddScoped<IMusicVideoTitleSource, OEmbedMusicVideoTitleSource>();
        services.AddScoped<IMusicVideoTitleBackfill, MusicVideoTitleBackfill>();

        // The provider is a singleton, so the ISRC reader opens its own scope per call.
        services.AddSingleton<ISongIsrcSource, SongIsrcSource>();
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();

        // Reading a playlist is metadata only: it does not depend on the YouTube source being enabled.
        // It takes the IInnertubeClient above, which is a typed HttpClient and therefore scoped, so the
        // provider is scoped too; ImportListService takes IEnumerable<IImportListProvider>.
        services.AddScoped<YouTubeMusicPlaylistProvider>();
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<YouTubeMusicPlaylistProvider>());

        return services;
    }
}
