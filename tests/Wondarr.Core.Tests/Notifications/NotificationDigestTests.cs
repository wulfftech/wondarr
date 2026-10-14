using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MailKit.Net.Smtp;
using NSubstitute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Email;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// Email digests: events wait in the database, one email goes out when the window ends, and nothing is
/// lost to a restart or a failed send. Time is a fake clock and SMTP is a fake sender.
/// </summary>
public sealed class NotificationDigestTests : IAsyncDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeSmtpSender _smtp = new();
    private readonly RecordingLogger<NotificationDigestService> _log = new();
    private readonly SecretRegistry _secrets = new();
    private readonly ServiceProvider _services;
    private readonly EmailProvider _email;
    private NotificationDigestService _digest;

    public NotificationDigestTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());

        _services = services.BuildServiceProvider();
        _email = new EmailProvider(_smtp, _secrets, NullLogger<EmailProvider>.Instance);
        _digest = NewDigestService();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task Entries_are_stored_and_nothing_is_sent_before_the_window_ends()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));

        _time.Advance(TimeSpan.FromMinutes(29));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().BeEmpty();
        (await CountAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task One_email_goes_out_when_the_window_ends_with_the_right_grouping_and_subject()
    {
        var id = await SeedNotificationAsync(delivery: "digest");

        await AddAsync(id, EmailSamples.Song(id: 1, title: "One"));
        _time.Advance(TimeSpan.FromMinutes(10));
        await AddAsync(id, EmailSamples.Song(NotificationEventNames.Failure, id: 2, title: "Two", body: "no peers"));
        _time.Advance(TimeSpan.FromMinutes(5));
        await AddAsync(id, EmailSamples.Song(id: 3, title: "Three"));
        _time.Advance(TimeSpan.FromMinutes(15));

        await _digest.FlushDueAsync(Token);

        var mail = _smtp.Sent.Should().ContainSingle().Subject.Message;
        mail.Subject.Should().Be("Wondarr: 2 imported, 1 failed (12:00–12:30)");
        mail.TextBody.Should().Contain("Imported (2)").And.Contain("Failed (1)").And.Contain("no peers");
        (await CountAsync(id)).Should().Be(0);

        // The window is over; the next tick has nothing to send.
        _time.Advance(TimeSpan.FromHours(1));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task An_empty_window_sends_nothing()
    {
        await SeedNotificationAsync(delivery: "digest");

        _time.Advance(TimeSpan.FromHours(2));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_window_runs_from_the_first_entry_of_each_notification()
    {
        var early = await SeedNotificationAsync("Early", delivery: "digest", minutes: 30);
        var late = await SeedNotificationAsync("Late", delivery: "digest", minutes: 60);

        await AddAsync(early, EmailSamples.Song(id: 1));
        await AddAsync(late, EmailSamples.Song(id: 1));
        _time.Advance(TimeSpan.FromMinutes(30));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle();
        (await CountAsync(early)).Should().Be(0);
        (await CountAsync(late)).Should().Be(1);

        // A new entry for the flushed notification starts a new window.
        await AddAsync(early, EmailSamples.Song(id: 2));
        _time.Advance(TimeSpan.FromMinutes(29));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle();

        _time.Advance(TimeSpan.FromMinutes(1));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().HaveCount(3, "late's 60 minute window and early's second 30 minute window both ended");
    }

    [Fact]
    public async Task Entries_survive_a_restart_and_are_flushed_afterwards()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));

        // The process stops: nothing but the database remains.
        _digest = NewDigestService();
        _time.Advance(TimeSpan.FromMinutes(45));

        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle().Which.Message.Subject.Should().StartWith("Wondarr: 1 imported");
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_entries_retries_with_a_backoff_and_raises_an_error_on_the_third()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));
        _smtp.Throw = new NotificationSendException("could not reach the mail server; check the host and port.");

        _time.Advance(TimeSpan.FromMinutes(30));
        await _digest.FlushDueAsync(Token);

        (await CountAsync(id)).Should().Be(1);
        _log.Errors.Should().BeEmpty();

        // Too soon: the first failure waits 2 minutes.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().BeEmpty();
        _log.Warnings.Should().ContainSingle();

        _time.Advance(TimeSpan.FromMinutes(1));
        await _digest.FlushDueAsync(Token);
        _log.Warnings.Should().HaveCount(2);

        _time.Advance(TimeSpan.FromMinutes(4));
        await _digest.FlushDueAsync(Token);

        _log.Errors.Should().ContainSingle().Which.Should().Contain("3 times in a row").And.NotContain("app-password");
        (await CountAsync(id)).Should().Be(1, "the entries stay");

        // The server comes back; the next attempt after the backoff sends everything.
        _smtp.Throw = null;
        _time.Advance(TimeSpan.FromMinutes(8));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle();
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task At_most_500_entries_are_kept_and_the_oldest_are_dropped_with_a_warning()
    {
        var id = await SeedNotificationAsync(delivery: "digest");

        for (var index = 1; index <= NotificationDigestService.MaxPendingEntries + 5; index++)
        {
            await AddAsync(id, EmailSamples.Song(id: index));
        }

        await using var database = _database.CreateContext(_time);
        var kept = await database.NotificationDigestEntries
            .Where(entry => entry.NotificationId == id)
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.SongId)
            .ToListAsync(Token);

        kept.Should().HaveCount(500);
        kept[0].Should().Be(6, "songs 1 to 5 were dropped");
        kept[^1].Should().Be(505);
        _log.Warnings.Should().HaveCount(5);
    }

    [Fact]
    public async Task Switching_to_each_flushes_what_is_pending_on_the_next_tick()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));
        await AddAsync(id, EmailSamples.Song(id: 2));

        _time.Advance(TimeSpan.FromMinutes(1));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().BeEmpty();

        await using (var database = _database.CreateContext(_time))
        {
            var notification = await database.Notifications.SingleAsync(Token);
            notification.Settings = EmailSamples.Settings(("delivery", "each")).GetRawText();
            await database.SaveChangesAsync(Token);
        }

        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle().Which.Message.Subject.Should().StartWith("Wondarr: 2 imported");
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_notification_deletes_its_pending_entries()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        var other = await SeedNotificationAsync("Other", delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));
        await AddAsync(other, EmailSamples.Song(id: 1));

        var service = new NotificationService(_database.CreateContext(_time), [_email], NullLogger<NotificationService>.Instance);
        (await service.DeleteAsync(id, Token)).Should().BeTrue();

        (await CountAsync(id)).Should().Be(0);
        (await CountAsync(other)).Should().Be(1);
    }

    [Fact]
    public async Task The_dispatcher_holds_back_imports_but_sends_updates_at_once()
    {
        var id = await SeedNotificationAsync(
            delivery: "digest",
            events: [NotificationEventNames.Import, NotificationEventNames.Update]);

        var songId = await SeedSongAndFileAsync();
        var fileId = await SeedFileIdAsync(songId);

        var dispatcher = new NotificationDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [_email],
            _time,
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.StartAsync(Token);

        try
        {
            await dispatcher.HandleAsync(new Wondarr.Core.Importing.SongImportedEvent(songId, fileId, Upgraded: false), Token);
            await dispatcher.HandleAsync(new UpdateAvailableEvent("0.1.3", "0.2.0", null), Token);

            // The dispatcher handles items in order, so once the update has been mailed the import
            // before it has been stored.
            await _smtp.WaitForSendsAsync(1);
        }
        finally
        {
            await dispatcher.StopAsync(Token);
        }

        _smtp.Sent.Single().Message.Subject.Should().Be("Wondarr: Update available: Wondarr 0.2.0");

        await using var database = _database.CreateContext(_time);
        var entry = await database.NotificationDigestEntries.SingleAsync(Token);
        entry.Event.Should().Be(NotificationEventNames.Import);
        entry.SongId.Should().Be(songId);

        var snapshot = JsonDocument.Parse(entry.Snapshot).RootElement;
        snapshot.GetProperty("song").GetProperty("title").GetString().Should().Be("Wonderwall");
        snapshot.GetProperty("download").GetProperty("sourceType").GetString().Should().Be("soulseek");
        snapshot.GetProperty("download").GetProperty("sizeBytes").GetInt64().Should().Be(30_000_000);
    }

    [Fact]
    public async Task The_dispatcher_holds_back_a_completed_download_in_digest_mode()
    {
        var id = await SeedNotificationAsync(
            delivery: "digest",
            events: [NotificationEventNames.Download, NotificationEventNames.Update]);

        var songId = await SeedSongAndFileAsync();
        var queueItemId = await SeedQueueItemAsync(songId);

        var dispatcher = new NotificationDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [_email],
            _time,
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.StartAsync(Token);

        try
        {
            await dispatcher.HandleAsync(
                new Wondarr.Core.Importing.QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed)
                {
                    PreviousState = QueueItemState.Downloading,
                },
                Token);
            await dispatcher.HandleAsync(new UpdateAvailableEvent("0.1.3", "0.2.0", null), Token);

            // The update is sent at once, and the download before it has been stored by then.
            await _smtp.WaitForSendsAsync(1);
        }
        finally
        {
            await dispatcher.StopAsync(Token);
        }

        _smtp.Sent.Should().ContainSingle().Which.Message.Subject.Should().Be("Wondarr: Update available: Wondarr 0.2.0");

        await using var database = _database.CreateContext(_time);
        var entry = await database.NotificationDigestEntries.SingleAsync(Token);
        entry.Event.Should().Be(NotificationEventNames.Download);
        entry.SongId.Should().Be(songId);

        var snapshot = JsonDocument.Parse(entry.Snapshot).RootElement;
        snapshot.GetProperty("download").GetProperty("sourceType").GetString().Should().Be("soulseek");
        snapshot.GetProperty("download").GetProperty("quality").GetString().Should().Be("FLAC");
        snapshot.GetProperty("download").GetProperty("sizeBytes").GetInt64().Should().Be(30_000_000);

        // The window ends: the digest has a Downloaded group.
        _time.Advance(TimeSpan.FromMinutes(31));
        await NewDigestService().FlushDueAsync(Token);

        _smtp.Sent.Should().HaveCount(2);
        _smtp.Sent[1].Message.Subject.Should().StartWith("Wondarr: 1 downloaded");
        _smtp.Sent[1].Message.TextBody.Should().Contain("Downloaded (1)");
    }

    [Fact]
    public async Task In_each_mode_the_dispatcher_sends_a_completed_download_at_once()
    {
        var id = await SeedNotificationAsync(delivery: "each", events: [NotificationEventNames.Download]);
        var songId = await SeedSongAndFileAsync();
        var queueItemId = await SeedQueueItemAsync(songId);

        var dispatcher = new NotificationDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [_email],
            _time,
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.StartAsync(Token);

        try
        {
            await dispatcher.HandleAsync(
                new Wondarr.Core.Importing.QueueItemChangedEvent(queueItemId, songId, QueueItemState.Completed)
                {
                    PreviousState = QueueItemState.RemotelyQueued,
                },
                Token);
            await _smtp.WaitForSendsAsync(1);
        }
        finally
        {
            await dispatcher.StopAsync(Token);
        }

        var mail = _smtp.Sent.Single().Message;
        mail.Subject.Should().Be("Wondarr: downloaded Oasis – Wonderwall");
        mail.TextBody.Should().Contain("Source: soulseek").And.Contain("Quality: FLAC").And.Contain("Size: 28.6 MB");
        mail.HtmlBody.Should().Contain("src=\"https://img.example.com/morning-glory.jpg\"");
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task In_each_mode_the_dispatcher_sends_the_import_at_once()
    {
        var id = await SeedNotificationAsync(delivery: "each");
        var songId = await SeedSongAndFileAsync();
        var fileId = await SeedFileIdAsync(songId);

        var dispatcher = new NotificationDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [_email],
            _time,
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.StartAsync(Token);

        try
        {
            await dispatcher.HandleAsync(new Wondarr.Core.Importing.SongImportedEvent(songId, fileId, Upgraded: false), Token);
            await _smtp.WaitForSendsAsync(1);
        }
        finally
        {
            await dispatcher.StopAsync(Token);
        }

        _smtp.Sent.Single().Message.Subject.Should().Be("Wondarr: imported Oasis – Wonderwall");
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task A_failure_after_the_message_was_accepted_is_not_a_send_failure()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));

        var client = Substitute.For<ISmtpClient>();
        client.DisconnectAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("the server hung up during QUIT")));

        var email = new EmailProvider(new SmtpSender(() => client), _secrets, NullLogger<EmailProvider>.Instance);
        var digest = new NotificationDigestService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [email],
            _time,
            _log);

        _time.Advance(TimeSpan.FromMinutes(30));
        await digest.FlushDueAsync(Token);

        await client.Received(1).SendAsync(Arg.Any<MimeKit.MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<MailKit.ITransferProgress?>());
        (await CountAsync(id)).Should().Be(0, "the message was accepted");
        _log.Warnings.Should().BeEmpty();
        _log.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task One_failing_add_does_not_break_the_next_one_on_the_same_context()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        var missing = new Notification { Id = id + 100, Name = "Gone", Type = "Email" };

        await using var database = _database.CreateContext(_time);
        var real = await database.Notifications.SingleAsync(row => row.Id == id, Token);

        // The foreign key refuses an entry for a notification that is not there.
        var act = () => NotificationDigestService.AddAsync(database, missing, EmailSamples.Song(id: 1), _log, Token);
        await act.Should().ThrowAsync<DbUpdateException>();

        await NotificationDigestService.AddAsync(database, real, EmailSamples.Song(id: 2), _log, Token);

        (await CountAsync(id)).Should().Be(1);
        (await CountAsync(missing.Id)).Should().Be(0);
    }

    [Fact]
    public async Task An_entry_added_while_a_digest_is_being_sent_survives_and_goes_out_in_the_next_one()
    {
        var id = await SeedNotificationAsync(delivery: "digest", type: "Racing");
        var racing = new RacingProvider();
        var digest = new NotificationDigestService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            [racing],
            _time,
            _log);

        await AddAsync(id, EmailSamples.Song(id: 1, title: "Before"));

        racing.DuringSend = async () => await AddAsync(id, EmailSamples.Song(id: 2, title: "During"));
        _time.Advance(TimeSpan.FromMinutes(30));
        await digest.FlushDueAsync(Token);

        racing.Digests.Should().ContainSingle().Which.Select(entry => entry.Message.Song!.Title).Should().Equal("Before");
        (await CountAsync(id)).Should().Be(1, "the entry that arrived during the send is still stored");

        racing.DuringSend = null;
        _time.Advance(TimeSpan.FromMinutes(30));
        await digest.FlushDueAsync(Token);

        racing.Digests.Should().HaveCount(2);
        racing.Digests[1].Select(entry => entry.Message.Song!.Title).Should().Equal("During");
        (await CountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task The_digest_reports_its_own_window_not_the_moment_it_was_flushed()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));

        // A tick that comes late: the window ended at 12:30.
        _time.Advance(TimeSpan.FromMinutes(47));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Single().Message.Subject.Should().Be("Wondarr: 1 imported (12:00–12:30)");
    }

    [Fact]
    public async Task A_disabled_notification_keeps_its_entries_and_sends_them_when_enabled_again()
    {
        var id = await SeedNotificationAsync(delivery: "digest");
        await AddAsync(id, EmailSamples.Song(id: 1));

        await SetEnabledAsync(false);
        _time.Advance(TimeSpan.FromHours(1));
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().BeEmpty();
        (await CountAsync(id)).Should().Be(1);

        await SetEnabledAsync(true);
        await _digest.FlushDueAsync(Token);

        _smtp.Sent.Should().ContainSingle();
        (await CountAsync(id)).Should().Be(0);
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        await using var database = _database.CreateContext(_time);
        var notification = await database.Notifications.SingleAsync(Token);
        notification.Enabled = enabled;
        await database.SaveChangesAsync(Token);
    }

    private NotificationDigestService NewDigestService() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        [_email],
        _time,
        _log);

    private async Task AddAsync(long notificationId, NotificationMessage message)
    {
        await using var database = _database.CreateContext(_time);
        var notification = await database.Notifications.SingleAsync(row => row.Id == notificationId, Token);

        await NotificationDigestService.AddAsync(database, notification, message, _log, Token);
    }

    private async Task<int> CountAsync(long notificationId)
    {
        await using var database = _database.CreateContext(_time);

        return await database.NotificationDigestEntries.CountAsync(entry => entry.NotificationId == notificationId, Token);
    }

    private async Task<long> SeedNotificationAsync(
        string name = "Mail",
        string delivery = "each",
        int minutes = 30,
        string[]? events = null,
        string type = "Email")
    {
        await using var database = _database.CreateContext(_time);

        var notification = new Notification
        {
            Name = name,
            Type = type,
            Settings = EmailSamples.Settings(("delivery", delivery), ("digestMinutes", minutes)).GetRawText(),
            Events = JsonSerializer.Serialize(events ?? [NotificationEventNames.Import]),
        };

        database.Notifications.Add(notification);
        await database.SaveChangesAsync(Token);

        return notification.Id;
    }

    private async Task<long> SeedSongAndFileAsync()
    {
        await using var database = _database.CreateContext(_time);

        var artist = new Artist { Name = "Oasis", SortName = "Oasis" };
        database.Artists.Add(artist);
        await database.SaveChangesAsync(Token);

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
        await database.SaveChangesAsync(Token);

        database.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            AlbumTitle = "Morning Glory",
            AlbumArtist = artist.Name,
            AlbumKey = $"oasis-{song.Id}",
            CoverUrl = "https://img.example.com/morning-glory.jpg",
        });

        database.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = "/music/Oasis/Morning Glory/01 - Wonderwall.flac",
            Size = 30_000_000,
            Codec = "flac",
            Container = "flac",
            QualityId = 36,
            SourceType = "soulseek",
            ImportedAt = _time.GetUtcNow().UtcDateTime,
        });

        await database.SaveChangesAsync(Token);

        return song.Id;
    }

    /// <summary>A grab of the song's candidate, as the dispatcher reads it back for a completed download.</summary>
    private async Task<long> SeedQueueItemAsync(long songId)
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
        await database.SaveChangesAsync(Token);

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
        await database.SaveChangesAsync(Token);

        var item = new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = "soulseek",
            Destination = "wondarr/grab",
            State = QueueItemState.Completed,
            SizeBytes = 30_000_000,
            StateChangedAt = now,
            LastProgressAt = now,
        };

        database.QueueItems.Add(item);
        await database.SaveChangesAsync(Token);

        return item.Id;
    }

    private async Task<long> SeedFileIdAsync(long songId)
    {
        await using var database = _database.CreateContext(_time);

        return await database.SongFiles.Where(file => file.SongId == songId).Select(file => file.Id).SingleAsync(Token);
    }

    /// <summary>A digest provider whose send can do something else in the middle, as a concurrent import would.</summary>
    private sealed class RacingProvider : IDigestNotificationProvider
    {
        public string Implementation => "Racing";

        public IReadOnlyList<NotificationField> Fields { get; } = [];

        public Func<Task>? DuringSend { get; set; }

        public List<IReadOnlyList<DigestEntry>> Digests { get; } = [];

        public IReadOnlyList<string> Validate(JsonElement settings) => [];

        public Task SendAsync(NotificationMessage message, JsonElement settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool Batches(JsonElement settings, string eventName) => true;

        public TimeSpan? DigestWindow(JsonElement settings) => TimeSpan.FromMinutes(30);

        public async Task SendDigestAsync(
            IReadOnlyList<DigestEntry> entries,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            TimeZoneInfo zone,
            JsonElement settings,
            CancellationToken cancellationToken)
        {
            if (DuringSend is { } during)
            {
                await during();
            }

            Digests.Add(entries);
        }
    }

    /// <summary>Keeps warnings and errors, formatted, so a test can read what was logged.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);

            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(text);
            }
            else if (logLevel >= LogLevel.Error)
            {
                Errors.Add(text);
            }
        }
    }
}
