using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Importing;
using Wondarr.Core.Messaging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Updates;
using Xunit;
using HealthResult = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The dispatcher: what it sends, to whom, and — the point of the whole design — that it never makes
/// the publisher wait. Providers are fakes over a real SQLite database, so nothing here touches HTTP.
/// </summary>
public sealed class NotificationDispatcherTests : IAsyncDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeNotificationProvider _webhook = new("Webhook");
    private readonly FakeNotificationProvider _discord = new("Discord");
    private readonly FakeNotificationProvider _flush = new("Flush");
    private int _flushes;
    private readonly ServiceProvider _services;
    private readonly NotificationDispatcher _dispatcher;
    private readonly EventAggregator _aggregator;

    public NotificationDispatcherTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<INotificationProvider>(_webhook);
        services.AddSingleton<INotificationProvider>(_discord);
        services.AddSingleton<INotificationProvider>(_flush);
        services.AddSingleton(provider => new NotificationDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetServices<INotificationProvider>(),
            _time,
            NullLogger<NotificationDispatcher>.Instance));

        services.AddSingleton<IHandle<SongGrabbedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<SongImportedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<QueueItemChangedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<HealthCheckCompletedEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHandle<UpdateAvailableEvent>>(provider => provider.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IEventAggregator, EventAggregator>();

        _services = services.BuildServiceProvider();
        _dispatcher = _services.GetRequiredService<NotificationDispatcher>();
        _aggregator = (EventAggregator)_services.GetRequiredService<IEventAggregator>();

        _dispatcher.StartAsync(Token).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Publishing_returns_before_a_slow_provider_has_finished()
    {
        var songId = await SeedSongAsync();
        var fileId = await SeedFileAsync(songId);
        await SeedNotificationAsync("Slow", "Webhook", NotificationEventNames.Import);

        var blocked = new TaskCompletionSource();
        _webhook.Handler = (_, _, _) => blocked.Task;

        // If the dispatcher sent on the publisher's thread this would never return.
        await _aggregator
            .PublishAsync(new SongImportedEvent(songId, fileId, Upgraded: false), Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => _webhook.Sent.Count == 1, "the import reached the provider");

        blocked.SetResult();
    }

    [Fact]
    public async Task Only_enabled_notifications_subscribed_to_the_event_receive_it()
    {
        var songId = await SeedSongAsync();
        var fileId = await SeedFileAsync(songId);

        await SeedNotificationAsync("Wanted", "Webhook", NotificationEventNames.Import);
        await SeedNotificationAsync("Disabled", "Webhook", NotificationEventNames.Import, enabled: false);
        await SeedNotificationAsync("Wants grabs", "Webhook", NotificationEventNames.Grab);

        await _aggregator.PublishAsync(new SongImportedEvent(songId, fileId, Upgraded: false), Token);

        await WaitUntilAsync(() => _webhook.Sent.Count >= 1, "the import to be sent");
        await FlushAsync();

        _webhook.Sent.Should().ContainSingle();
        _webhook.Sent[0].Event.Should().Be(NotificationEventNames.Import);
    }

    [Fact]
    public async Task One_provider_throwing_does_not_stop_the_next()
    {
        var songId = await SeedSongAsync();
        var fileId = await SeedFileAsync(songId);

        await SeedNotificationAsync("First", "Webhook", NotificationEventNames.Import);
        await SeedNotificationAsync("Second", "Discord", NotificationEventNames.Import);

        _webhook.Throw = new InvalidOperationException("the endpoint said no");

        await _aggregator.PublishAsync(new SongImportedEvent(songId, fileId, Upgraded: false), Token);

        await WaitUntilAsync(() => _discord.Sent.Count == 1, "the second notification to be sent");

        _webhook.Sent.Should().ContainSingle();
        _discord.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task The_first_health_result_set_is_the_baseline_and_a_later_one_sends_only_new_issues()
    {
        await SeedNotificationAsync("Health", "Webhook", NotificationEventNames.Health);

        var baseline = new HealthCheckCompletedEvent(
        [
            new HealthResult("DownloadFolder", HealthCheckResult.Warning, "the folder is gone", null),
            new HealthResult("Database", HealthCheckResult.Ok, "fine", null),
            new HealthResult("Plex", HealthCheckResult.Notice, "no server", null),
        ]);

        await _aggregator.PublishAsync(baseline, Token);
        await FlushAsync();

        _webhook.Sent.Should().BeEmpty("the first result set is the baseline");

        // The same issue again: still nothing. A new one: one message.
        await _aggregator.PublishAsync(baseline, Token);
        await FlushAsync();

        _webhook.Sent.Should().BeEmpty();

        await _aggregator.PublishAsync(
            new HealthCheckCompletedEvent(
            [
                new HealthResult("DownloadFolder", HealthCheckResult.Warning, "the folder is gone", null),
                new HealthResult("Indexer", HealthCheckResult.Error, "the indexer is unreachable", null),
                new HealthResult("Database", HealthCheckResult.Ok, "fine", null),
            ]),
            Token);

        await WaitUntilAsync(() => _webhook.Sent.Count == 1, "the new health issue to be sent");

        var sent = _webhook.Sent[0];
        sent.Event.Should().Be(NotificationEventNames.Health);
        sent.Health!.Source.Should().Be("Indexer");
        sent.Health.Level.Should().Be("error");
        sent.Health.Message.Should().Be("the indexer is unreachable");
    }

    [Fact]
    public async Task A_new_version_is_sent_to_the_notifications_that_subscribe_to_update()
    {
        await SeedNotificationAsync("Updates", "Webhook", NotificationEventNames.Update);
        await SeedNotificationAsync("Imports", "Discord", NotificationEventNames.Import);

        await _aggregator.PublishAsync(
            new UpdateAvailableEvent("0.1.0", "0.2.0", "https://github.com/wulfftech/wondarr/releases/tag/v0.2.0"),
            Token);

        await WaitUntilAsync(() => _webhook.Sent.Count == 1, "the update to be sent");

        var sent = _webhook.Sent[0];
        sent.Event.Should().Be(NotificationEventNames.Update);
        sent.Body.Should().Contain("Wondarr 0.2.0 is available (you have 0.1.0)")
            .And.Contain("https://github.com/wulfftech/wondarr/releases/tag/v0.2.0");
        sent.Update!.LatestVersion.Should().Be("0.2.0");
        _discord.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_queue_item_that_failed_sends_a_failure_and_one_that_only_moved_does_not()
    {
        var songId = await SeedSongAsync();
        var queueItemId = await SeedQueueItemAsync(songId, QueueItemState.Failed, "the peer went offline");

        await SeedNotificationAsync("Failures", "Webhook", NotificationEventNames.Failure);

        await _aggregator.PublishAsync(
            new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Downloading),
            Token);
        await FlushAsync();

        _webhook.Sent.Should().BeEmpty();

        await _aggregator.PublishAsync(new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Failed), Token);

        await WaitUntilAsync(() => _webhook.Sent.Count == 1, "the failure to be sent");

        _webhook.Sent[0].Event.Should().Be(NotificationEventNames.Failure);
        _webhook.Sent[0].Song!.Title.Should().Be("Wonderwall");
    }

    [Theory]
    [InlineData(QueueItemState.Queued)]
    [InlineData(QueueItemState.RemotelyQueued)]
    [InlineData(QueueItemState.Downloading)]
    public async Task A_source_reporting_the_file_complete_sends_a_download_with_the_source_quality_and_size(
        QueueItemState before)
    {
        var songId = await SeedSongAsync();
        var queueItemId = await SeedQueueItemAsync(songId, QueueItemState.Completed, null);

        await SeedNotificationAsync("Downloads", "Webhook", NotificationEventNames.Download);
        await SeedNotificationAsync("Grabs", "Discord", NotificationEventNames.Grab);

        await _aggregator.PublishAsync(
            new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed) { PreviousState = before },
            Token);

        await WaitUntilAsync(() => _webhook.Sent.Count == 1, "the download to be sent");
        await FlushAsync();

        var sent = _webhook.Sent.Should().ContainSingle().Subject;
        sent.Event.Should().Be(NotificationEventNames.Download);
        sent.Title.Should().Be("Downloaded: Oasis – Wonderwall");
        sent.Body.Should().Be("Wonderwall.flac from soulseek (FLAC)");
        sent.Song!.Title.Should().Be("Wonderwall");
        sent.Release.Should().Be(new NotificationRelease("Wonderwall.flac", "soulseek", "FLAC", 30_000_000));
        sent.Download.Should().NotBeNull();
        sent.Download!.SourceType.Should().Be("soulseek");
        sent.Download.Quality.Should().Be("FLAC");
        sent.Download.SizeBytes.Should().Be(30_000_000);
        sent.Download.CoverPath.Should().BeNull("the file is not in the library yet");
        _discord.Sent.Should().BeEmpty("a notification that only wants grabs is not subscribed to the new event");
    }

    [Fact]
    public async Task A_change_back_to_completed_that_did_not_come_from_a_source_sends_no_download()
    {
        var songId = await SeedSongAsync();
        var queueItemId = await SeedQueueItemAsync(songId, QueueItemState.Completed, null);

        await SeedNotificationAsync("Downloads", "Webhook", NotificationEventNames.Download);

        // A deferred import hands the item back from Importing; a restart's recovery and a plain
        // re-publication carry no previous state at all.
        await _aggregator.PublishAsync(
            new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed) { PreviousState = QueueItemState.Importing },
            Token);
        await _aggregator.PublishAsync(new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed), Token);
        await _aggregator.PublishAsync(new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Downloading), Token);
        await FlushAsync();

        _webhook.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_whose_import_fails_gets_both_a_download_and_a_failure()
    {
        var songId = await SeedSongAsync();
        var queueItemId = await SeedQueueItemAsync(songId, QueueItemState.Failed, "the file failed verification");

        await SeedNotificationAsync("Both", "Webhook", NotificationEventNames.Download);
        await SeedNotificationAsync("Failures", "Discord", NotificationEventNames.Failure);

        await _aggregator.PublishAsync(
            new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed) { PreviousState = QueueItemState.Downloading },
            Token);
        await _aggregator.PublishAsync(new QueueItemChangedEvent(queueItemId, songId, QueueItemState.Failed), Token);

        await WaitUntilAsync(() => _webhook.Sent.Count == 1 && _discord.Sent.Count == 1, "both messages to be sent");

        _webhook.Sent[0].Event.Should().Be(NotificationEventNames.Download);
        _discord.Sent[0].Event.Should().Be(NotificationEventNames.Failure);
    }

    [Fact]
    public async Task An_upgrade_goes_to_upgrade_subscribers_and_not_to_import_ones()
    {
        var songId = await SeedSongAsync();
        var fileId = await SeedFileAsync(songId);

        await SeedNotificationAsync("Imports", "Webhook", NotificationEventNames.Import);
        await SeedNotificationAsync("Upgrades", "Discord", NotificationEventNames.Upgrade);

        await _aggregator.PublishAsync(new SongImportedEvent(songId, fileId, Upgraded: true), Token);

        await WaitUntilAsync(() => _discord.Sent.Count == 1, "the upgrade to be sent");
        await FlushAsync();

        _webhook.Sent.Should().BeEmpty();
        _discord.Sent[0].Event.Should().Be(NotificationEventNames.Upgrade);
        _discord.Sent[0].IsUpgrade.Should().BeTrue();
        _discord.Sent[0].File!.Quality.Should().Be("FLAC");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _dispatcher.StopAsync(Token);
        _dispatcher.Dispose();
        await _services.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    /// Waits until the dispatcher has gone through everything published before this call. It works
    /// off the dispatcher's own queue being first-in-first-out: a sentinel event, subscribed to by a
    /// notification no test asserts on, can only be sent after every earlier event has been fully
    /// handled. So "nothing was sent" is checked after the dispatcher has demonstrably got past it,
    /// not after a guess at how long that takes.
    /// </summary>
    private async Task FlushAsync()
    {
        if (_flushes == 0)
        {
            await SeedNotificationAsync("Flush", "Flush", NotificationEventNames.Update);
        }

        var expected = ++_flushes;

        await _aggregator.PublishAsync(new UpdateAvailableEvent("0.0.0", "0.0.1", "https://example.invalid/flush"), Token);
        await WaitUntilAsync(() => _flush.Sent.Count == expected, "the dispatcher to get past the earlier events");
    }

    /// <summary>Polls until <paramref name="condition"/> holds, failing after a generous bound rather than hanging.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;

        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new InvalidOperationException($"Timed out waiting for {what}.");
            }

            await Task.Delay(5);
        }
    }

    private async Task<long> SeedSongAsync()
    {
        await using var database = _database.CreateContext(_time);

        var artist = new Artist { Name = "Oasis", SortName = "Oasis" };
        database.Artists.Add(artist);
        await database.SaveChangesAsync();

        var song = new Song
        {
            Title = "Wonderwall",
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            Monitored = true,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        database.Songs.Add(song);
        await database.SaveChangesAsync();

        database.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            AlbumTitle = "(What's the Story) Morning Glory?",
            AlbumArtist = artist.Name,
            AlbumKey = $"oasis-{song.Id}",
            TrackNo = 1,
        });

        await database.SaveChangesAsync();

        return song.Id;
    }

    private async Task<long> SeedFileAsync(long songId)
    {
        await using var database = _database.CreateContext(_time);

        var file = new SongFile
        {
            SongId = songId,
            Path = "/music/Oasis/Morning Glory/01 - Wonderwall.flac",
            Size = 30_000_000,
            Codec = "flac",
            Container = "flac",
            QualityId = 36,
            SourceType = "soulseek",
            ImportedAt = _time.GetUtcNow().UtcDateTime,
        };

        database.SongFiles.Add(file);
        await database.SaveChangesAsync();

        return file.Id;
    }

    private async Task<long> SeedQueueItemAsync(long songId, QueueItemState state, string? message)
    {
        await using var database = _database.CreateContext(_time);
        var now = _time.GetUtcNow().UtcDateTime;

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = now,
            Outcome = SearchOutcome.Grabbed,
        };

        database.SearchRuns.Add(run);
        await database.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = "soulseek",
            BlocklistKey = "slsk:peer:/music/Wonderwall.flac",
            DisplayName = "Wonderwall.flac",
            RemotePath = "/music/Wonderwall.flac",
            QualityId = 36,
            SizeBytes = 30_000_000,
        };

        database.Candidates.Add(candidate);
        await database.SaveChangesAsync();

        var item = new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = "soulseek",
            Destination = "wondarr/grab",
            State = state,
            Message = message,
            SizeBytes = 30_000_000,
            StateChangedAt = now,
            LastProgressAt = now,
            FinishedAt = now,
        };

        database.QueueItems.Add(item);
        await database.SaveChangesAsync();

        return item.Id;
    }

    private async Task SeedNotificationAsync(
        string name,
        string implementation,
        string @event,
        bool enabled = true)
    {
        await using var database = _database.CreateContext(_time);

        database.Notifications.Add(new Notification
        {
            Name = name,
            Type = implementation,
            Enabled = enabled,
            Settings = """{"url":"http://hooks.local/wondarr"}""",
            Events = JsonSerializer.Serialize(new[] { @event }),
        });

        await database.SaveChangesAsync();
    }

    /// <summary>A provider that records what it was asked to send and can be told to block or throw.</summary>
    private sealed class FakeNotificationProvider : INotificationProvider
    {
        private readonly Lock _gate = new();
        private readonly List<NotificationMessage> _sent = [];

        public FakeNotificationProvider(string implementation) => Implementation = implementation;

        public string Implementation { get; }

        public IReadOnlyList<NotificationField> Fields { get; } = [new("url", "URL", "url", Required: true)];

        /// <summary>What the send does, when the test wants it to block or wait.</summary>
        public Func<NotificationMessage, JsonElement, CancellationToken, Task>? Handler { get; set; }

        /// <summary>Thrown by every send when set.</summary>
        public Exception? Throw { get; set; }

        /// <summary>What this provider was sent, in order.</summary>
        public IReadOnlyList<NotificationMessage> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task SendAsync(
            NotificationMessage message,
            JsonElement settings,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add(message);
            }

            if (Throw is { } failure)
            {
                throw failure;
            }

            return Handler?.Invoke(message, settings, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
