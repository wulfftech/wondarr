using System.Threading.Channels;
using Wondarr.Core.Albums;
using Wondarr.Core.Authentication;
using Wondarr.Core.Backup;
using Wondarr.Core.Blocklisting;
using Wondarr.Core.Compaction;
using Wondarr.Core.Configuration;
using Wondarr.Core.Decisions;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.History;
using Wondarr.Core.Housekeeping;
using Wondarr.Core.Importing;
using Wondarr.Core.ImportLists;
using Wondarr.Core.ImportLists.Csv;
using Wondarr.Core.ImportLists.Deezer;
using Wondarr.Core.ImportLists.LastFm;
using Wondarr.Core.ImportLists.ListenBrainz;
using Wondarr.Core.ImportLists.References;
using Wondarr.Core.Indexers;
using Wondarr.Core.Jobs;
using Wondarr.Core.Logging;
using Wondarr.Core.Media;
using Wondarr.Core.Messaging;
using Wondarr.Core.CustomFilters;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Apprise;
using Wondarr.Core.Notifications.Discord;
using Wondarr.Core.Notifications.Webhook;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.References;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tagging;
using Wondarr.Core.Updates;
using Wondarr.Core.Verification;
using Wondarr.Core.Wanted;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using Microsoft.Extensions.Options;

namespace Wondarr.Core;

/// <summary>
/// Registers the services owned by Wondarr.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the domain, decision engine, import pipeline, metadata and persistence services.
    /// </summary>
    public static IServiceCollection AddWondarrCore(this IServiceCollection services)
    {
        services.AddScoped<ICredentialStore, CredentialStore>();

        // Profiles and libraries are edited through the API and read by the decision engine; both use
        // the scoped DbContext, so they are scoped too.
        services.AddScoped<IQualityDefinitionService, QualityDefinitionService>();
        services.AddScoped<IQualityProfileService, QualityProfileService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<ISongLibraryMover, SongLibraryMover>();

        services.TryAddSingleton(TimeProvider.System);

        // DatabaseHealthCheck needs the scoped DbContext; the folder checks only need the paths.
        services.AddScoped<IHealthCheck, DatabaseHealthCheck>();
        services.AddSingleton<IHealthCheck, ConfigFolderHealthCheck>();
        services.AddSingleton<IHealthCheck, LogFolderHealthCheck>();

        // The System pages read the app's own log files through this; it holds no state beyond the
        // resolved paths, so it is a singleton like the folder checks that share them.
        services.AddSingleton<ILogFileReader, LogFileReader>();

        // The media tools only answer once per process (MediaToolAvailability caches), so both it and
        // the check that reads it are singletons like the folder checks.
        services.AddSingleton<IHealthCheck, MediaToolsHealthCheck>();
        services.AddSingleton<HealthCheckService>();

        // What the import pipeline asks of a downloaded file: what is it, does it decode, and what is
        // its fingerprint. Their options (binary paths, timeout, decode check) are bound by
        // AddWondarrMetadata, because AddWondarrCore has no IConfiguration to bind from.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IFingerprinter, Fingerprinter>();
        services.AddSingleton<MediaToolAvailability>();

        // The transcode step of the import pipeline: stateless beyond one ffmpeg run, so it sits
        // with the other media tools.
        services.AddSingleton<ITranscoder, Transcoder>();
        services.AddSingleton<ISpectralAnalyzer, SpectralAnalyzer>();
        services.AddSingleton<IReplayGainAnalyzer, ReplayGainAnalyzer>();

        // Bounds the cover the organizer embeds; it holds no state of its own beyond the temp files
        // of one conversion, so it is a singleton next to the other media tools.
        services.AddSingleton<ICoverImageProcessor, CoverImageProcessor>();

        // Transient, like the cover-art and identity resolvers: it takes the AcoustID typed client,
        // which the factory hands out transient.
        services.AddTransient<IDownloadVerifier, DownloadVerifier>();

        services.AddSingleton<IEventAggregator, EventAggregator>();

        // Notifications: the providers are stateless, the dispatcher is one instance (the four event
        // handles and the hosted service must be the same object, because the queue lives in it), and
        // the CRUD service reads and writes the scoped DbContext.
        services.AddSingleton<INotificationProvider, WebhookProvider>();
        services.AddSingleton<INotificationProvider, DiscordProvider>();
        services.AddSingleton<INotificationProvider, AppriseProvider>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<IHandle<SongGrabbedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<SongImportedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<QueueItemChangedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<HealthCheckCompletedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDispatcher>());

        // Indexers and download clients: the CRUD services read and write the scoped DbContext, like
        // the notification service. The types themselves arrive with the source projects (P7-03,
        // P7-05, P7-06); until then the container resolves the two enumerations as empty and the
        // schema endpoints answer an empty list.
        services.AddScoped<IIndexerService, IndexerService>();
        services.AddScoped<IDownloadClientService, DownloadClientService>();

        // One client for every provider. No redirects (a webhook URL may carry a token, and following
        // one would hand it to whoever the far end names) and no cookie jar (each send is independent).
        services.AddHttpClient(NotificationHttp.ClientName, client => client.Timeout = NotificationHttp.SendTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            });

        // Stateless apart from ATL's global settings, and it only touches the file it is handed.
        services.AddSingleton<ITagWriter, TagWriter>();
        services.AddSingleton<ITagReader, TagReader>();

        // The album policy engine is pure; its only dependency is the source of synthetic album ids.
        services.AddSingleton<IAlbumPolicyEngine>(new AlbumPolicyEngine(Guid.NewGuid));

        // The decision engine is pure and stateless: every candidate is judged from the context alone.
        services.AddSingleton<DecisionEngine>();

        // The queue writes ids here and the executor drains it; both resolve the same instance, so
        // this must stay a singleton.
        services.TryAddSingleton(Channel.CreateUnbounded<long>());
        services.AddSingleton<ICommandQueue, CommandQueue>();

        // File placement: the disk seam and the three services that use it hold no request state, so
        // they are singletons like the rest of the pipeline's stateless parts.
        services.AddSingleton<IDiskOperations, DiskOperations>();
        services.AddSingleton<IRemotePathMapper, RemotePathMapper>();
        services.AddSingleton<IRecycleBin, RecycleBin>();
        services.AddSingleton<IFilePlacer, FilePlacer>();

        // The per-song file guard between imports and compaction: one process-wide instance, so
        // every import and every compaction stage step of the process shares it.
        services.AddSingleton<ISongFileLock, SongFileLock>();

        services.AddScoped<ICommandHandler, HeartbeatCommandHandler>();
        services.AddScoped<ICommandHandler, CheckHealthCommandHandler>();
        services.AddScoped<ICommandHandler, BulkAddSongsCommandHandler>();
        services.AddScoped<ICommandHandler, ConvertFilesCommandHandler>();
        services.AddScoped<IFileConverter, FileConverter>();
        services.AddScoped<ICommandHandler, ApplyReplayGainCommandHandler>();
        services.AddScoped<IReplayGainApplier, ReplayGainApplier>();
        services.AddScoped<ICommandHandler, ImportListSyncCommandHandler>();
        services.AddScoped<ICommandHandler, MissingSearchCommandHandler>();
        services.AddScoped<ICommandHandler, UpgradeSearchCommandHandler>();
        services.AddScoped<ICommandHandler, SongSearchCommandHandler>();
        services.AddScoped<ICommandHandler, ReferenceLibraryScanCommandHandler>();
        services.AddScoped<ICommandHandler, ReferenceAdoptCommandHandler>();
        services.AddScoped<ICommandHandler, CompactLibraryCommandHandler>();
        services.AddScoped<ICommandHandler, MoveSongsCommandHandler>();

        // The weekly Backup task: one scheduled backup, then the retention pass.
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<ICommandHandler, BackupCommandHandler>();

        // The daily Housekeeping task: command, search-run, blocklist and recycle-bin retention, then a vacuum.
        services.AddScoped<ICommandHandler, HousekeepingCommandHandler>();

        // The twice-a-day CheckForUpdates task, the notification on a new version, and the card that switches it off.
        services.AddWondarrUpdates();
        services.AddScoped<ICommandHandler, CheckForUpdatesCommandHandler>();
        services.AddSingleton<IHandle<UpdateAvailableEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());

        // Lets the executor tell a handler DI cannot build from the ones it can.
        services.TryAddSingleton(new CommandHandlerRegistrations(services));

        // The stop a staged restore asks for, behind an interface so a test host can stub it out.
        services.AddSingleton<IApplicationShutdown, HostApplicationShutdown>();


        // The scan writes the reference_file rows through the scoped DbContext.
        services.AddScoped<IReferenceScanner, ReferenceScanner>();

        // Identification runs on those same rows, in the same scope, right after a scan.
        services.AddScoped<IReferenceIdentifier, ReferenceIdentifier>();

        // The reference libraries themselves, and the Match queue that settles what identification could not.
        services.AddScoped<IReferenceLibraryService, ReferenceLibraryService>();
        services.AddScoped<IReferenceMatchService, ReferenceMatchService>();

        // Adoption hands an adopt-mode library's identified files over to its target library, through
        // the same organizer the import uses, in the same scope.
        services.AddScoped<IReferenceAdopter, ReferenceAdopter>();

        // The pasted-list pipeline: stored by the API, processed by the BulkAddSongs command.
        services.AddScoped<IPasteListService, PasteListService>();
        services.AddScoped<IImportListService, ImportListService>();
        services.AddScoped<IPlaylistWriter, PlaylistWriter>();
        services.AddSingleton<CsvImportListProvider>();
        services.AddSingleton<IImportListProvider>(provider => provider.GetRequiredService<CsvImportListProvider>());

        // The Deezer providers read through the shared IDeezerClient, a typed HttpClient and therefore
        // scoped; ImportListService takes IEnumerable<IImportListProvider>, so mixing lifetimes is fine.
        services.AddScoped<DeezerPlaylistProvider>();
        services.AddScoped<DeezerArtistTopProvider>();
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<DeezerPlaylistProvider>());
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<DeezerArtistTopProvider>());

        // The Last.fm and ListenBrainz providers read through the named clients that
        // AddWondarrMetadata registers, and the reference-library provider reads the scoped
        // DbContext; all three families are scoped for the same reason the Deezer ones are.
        services.AddScoped<LastFmLovedProvider>();
        services.AddScoped<LastFmTopProvider>();
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<LastFmLovedProvider>());
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<LastFmTopProvider>());
        services.AddScoped<ListenBrainzLovedProvider>();
        services.AddScoped<ListenBrainzPlaylistProvider>();
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<ListenBrainzLovedProvider>());
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<ListenBrainzPlaylistProvider>());
        services.AddScoped<ReferenceLibraryListProvider>();
        services.AddScoped<IImportListProvider>(provider => provider.GetRequiredService<ReferenceLibraryListProvider>());

        // The write side of the song lifecycle: resolved identities become songs and album contexts.
        services.AddScoped<ISongService, SongService>();
        services.AddScoped<ISongEditorService, SongEditorService>();

        // The song page's data: the stored identity, Deezer and the reference-library row (read only),
        // and the lyrics. The in-memory cache holds the Deezer (1 h) and LRCLIB (1 day) answers.
        services.AddMemoryCache();
        services.AddScoped<ISongDetailsService, SongDetailsService>();
        services.AddScoped<ISongLyricsService, SongLyricsService>();
        services.AddScoped<ICustomFilterService, CustomFilterService>();

        // The album add: search, releases, tracklist, and the tracks as one pinned batch of songs.
        services.AddScoped<IAlbumService, AlbumService>();
        services.AddScoped<ICommandHandler, AddAlbumCommandHandler>();

        // The Compact library task's dry run: it re-plans through the song service and reads the files
        // it would move, in the same scope as they are.
        services.AddScoped<ICompactPlanner, CompactPlanner>();

        // The Compact library task's executor: it applies that plan through the same organizer, and
        // writes its per-file rows through the same scoped DbContext.
        services.AddScoped<ICompactExecutor, CompactExecutor>();
        services.AddScoped<IArtistService, ArtistService>();

        // The read side of the song lifecycle: wanted lists, history and the blocklist.
        services.AddScoped<IWantedService, WantedService>();
        services.AddScoped<IHistoryService, HistoryService>();
        services.AddScoped<IBlocklistService, BlocklistService>();

        // What the search-and-grab loop records: the runs and their candidates, the download queue, and
        // what we know about the Soulseek peers. All three read and write the scoped DbContext.
        services.AddScoped<ISearchRunService, SearchRunService>();
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<ISoulseekUserService, SoulseekUserService>();

        // The search-and-grab loop itself: it reads all of the above and writes the runs, the queue and
        // the history in one unit of work, so it shares their scope.
        services.AddScoped<SlotWaitContext>();
        services.AddSingleton<SlotWaiters>();
        services.AddScoped<ISongSearchService, SongSearchService>();

        // The import pipeline: it turns a finished download into a library file through the same
        // scoped DbContext, and it grabs the next candidate through the search service above.
        services.AddScoped<ILibraryOrganizer, LibraryOrganizer>();
        services.AddScoped<IImportService, ImportService>();

        // What the user can do to a queue item, through the same scoped DbContext as the API.
        services.AddScoped<IQueueActions, QueueActions>();

        // The queue poll runs for the life of the process, so it is one instance: the hosted service,
        // the singleton behind IQueueTracker (which the slskd completion handler wakes) and the
        // registration the DI container resolves are the same object.
        services.AddSingleton<QueueTracker>();
        services.AddSingleton<IQueueTracker>(provider => provider.GetRequiredService<QueueTracker>());
        services.AddHostedService(provider => provider.GetRequiredService<QueueTracker>());

        // The cover client itself is registered by AddWondarrMetadata, next to the other named HTTP
        // clients; the fetcher only needs the factory to ask for it.
        services.AddTransient<ICoverFetcher, CoverFetcher>();

        services.AddSingleton<IScheduledTaskCatalog, ScheduledTaskCatalog>();
        services.AddScoped<IHandle<CommandUpdatedEvent>, JobTableUpdater>();

        // The scheduler is a RAM store: the durable state is the job table, which ScheduledTaskService
        // reads back at every start (ARCHITECTURE §5.5).
        services.AddQuartz();

        // Same ordering reason as the executor below: the schedule is built after the migration
        // hosted service has created the job table, and the Quartz hosted service that starts the
        // scheduler runs after this one so every trigger is registered before it does.
        services.AddHostedService<ScheduledTaskService>();
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        // Registered here rather than in AddWondarrPersistence so it starts after the migration
        // hosted service (hosted services start in registration order) and the command table exists.
        services.AddHostedService<CommandExecutor>();

        return services;
    }

    /// <summary>
    /// Registers the resolved paths and the validated, bound <see cref="ServerOptions"/>.
    /// </summary>
    public static IServiceCollection AddWondarrConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);

        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection("Server"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        services.AddSingleton<IPostConfigureOptions<ServerOptions>, ServerOptionsPostConfigure>();

        // The settings pages write changes back into config.yml. It is a singleton because writers
        // are serialised against each other process-wide, and it needs the configuration root to
        // reload it so bound options see the change.
        services.AddSingleton<IConfigFileWriter, ConfigFileWriter>();

        // Bound here rather than in AddWondarrCore because binding needs the configuration, which
        // that method is not given; the services that read it are registered there.
        services.AddOptions<ImportOptions>()
            .Bind(configuration.GetSection("Import"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ImportOptions>, ImportOptionsValidator>();

        services.AddOptions<LogOptions>()
            .Bind(configuration.GetSection("Log"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<LogOptions>, LogOptionsValidator>();

        // The search limits are read by the scoped search service and by the missing-song command.
        services.AddOptions<SearchOptions>()
            .Bind(configuration.GetSection("Search"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>();

        // The reference-library identification: how sure is enough, and how far a length may differ.
        services.AddOptions<ReferenceOptions>()
            .Bind(configuration.GetSection("Reference"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ReferenceOptions>, ReferenceOptionsValidator>();

        // The queue poll's intervals and the timeouts it gives up on a peer after.
        services.AddOptions<QueueOptions>()
            .Bind(configuration.GetSection("Queue"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<QueueOptions>, QueueOptionsValidator>();

        // The backup schedule and retention: read by the Backup command and the task catalog.
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection("Backup"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();

        // Whether Wondarr asks GitHub for new releases (update.check_enabled).
        services.AddOptions<UpdateOptions>()
            .Bind(configuration.GetSection("Update"));

        services.AddSingleton<ISecretRegistry, SecretRegistry>();

        return services;
    }

    /// <summary>
    /// Adds the SQLite database, the settings repository and the startup migrator.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="connectionString">The SQLite connection string (for example <c>Data Source=/config/wondarr.db</c>).</param>
    public static IServiceCollection AddWondarrPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<DatabaseMigrator>();
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }
}
