using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>The three music video commands and the scheduled task's source (ADR-0014).</summary>
public sealed class MusicVideoCommandTests : IDisposable
{
    private const string VideoId = "u7K72X4eo_s";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static CommandContext ContextFor(string? body, CommandTrigger trigger = CommandTrigger.Scheduled) =>
        new(1, body, trigger, _ => Task.CompletedTask);

    [Fact]
    public async Task The_missing_search_stops_at_the_limit_and_goes_through_the_oldest_songs_first()
    {
        await _harness.InitAsync();
        _harness.Options.ScheduledSearchLimit = 2;
        var start = _harness.Time.GetUtcNow();
        var ids = new List<long>();

        // Song 0 is the newest, and has the lowest id: the order is the age, not the id.
        for (var index = 0; index < 4; index++)
        {
            ids.Add((await _harness.AddSongAsync($"Song {index}", $"/music/{index}.flac")).Id);
        }

        for (var index = 0; index < 4; index++)
        {
            await _harness.SetAddedAsync(ids[index], start.AddDays(-index));
        }

        var message = await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);

        _harness.Finder.Queries.Select(query => query.Title).Should().Equal("Song 3", "Song 2");
        message.Should().Contain("2 of 2");
    }

    [Fact]
    public async Task The_missing_search_downloads_only_confident_matches_and_remembers_the_rest()
    {
        await _harness.InitAsync();
        var confident = await _harness.AddSongAsync("Sure", "/music/a.flac");
        var unsure = await _harness.AddSongAsync("Unsure", "/music/b.flac");
        var nothing = await _harness.AddSongAsync("Nothing", "/music/c.flac");
        _harness.Finder.Answer = query => query.Title switch
        {
            "Sure" => [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz)],
            "Unsure" => [FakeVideoFinder.Candidate("CCCCCCCCCCC", confident: false)],
            _ => [],
        };

        var message = await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);

        message.Should().Contain("queued 1 download").And.Contain("2 without a confident match");

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.SongId.Should().Be(confident.Id);
        row.YouTubeVideoId.Should().Be(VideoId);
        row.Status.Should().Be(MusicVideoStatus.Queued);

        (await fresh.MusicVideoLastSearches.Select(search => search.SongId).ToListAsync())
            .Should().BeEquivalentTo([unsure.Id, nothing.Id], "the unsure and the fruitless are remembered, the downloaded one is not");

        await _harness.Commands.Received(1).EnqueueAsync(
            MusicVideoDownloadCommandHandler.CommandName,
            Arg.Any<string?>(),
            CommandTrigger.Scheduled,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fruitless_song_is_not_searched_again_for_30_days()
    {
        await _harness.InitAsync();
        await _harness.AddSongAsync("Nothing", "/music/c.flac");

        await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);
        _harness.Finder.Queries.Should().HaveCount(1);

        _harness.Time.Advance(TimeSpan.FromDays(10));
        await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);
        _harness.Finder.Queries.Should().HaveCount(1, "it was searched 10 days ago");

        _harness.Time.Advance(TimeSpan.FromDays(21));
        await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);
        _harness.Finder.Queries.Should().HaveCount(2, "31 days have passed");
    }

    [Fact]
    public async Task A_failing_source_is_not_remembered_as_fruitless_and_stops_the_run_after_three_in_a_row()
    {
        await _harness.InitAsync();
        for (var index = 0; index < 5; index++)
        {
            await _harness.AddSongAsync($"Song {index}", $"/music/{index}.flac");
        }

        _harness.Finder.Failure = new InvalidOperationException("YouTube is down");

        var message = await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);

        _harness.Finder.Queries.Should().HaveCount(MusicVideoMissingSearchCommandHandler.MaxConsecutiveErrors);
        message.Should().Contain("3 search(es) failed");

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideoLastSearches.CountAsync()).Should().Be(0, "a failed lookup is not an answer");
    }

    [Fact]
    public async Task The_missing_search_does_nothing_while_music_videos_are_off()
    {
        await _harness.InitAsync();
        _harness.Options.Enabled = false;
        await _harness.AddSongAsync("Song", "/music/a.flac");

        var message = await Handler().ExecuteAsync(ContextFor(null), CancellationToken.None);

        message.Should().Contain("turned off");
        _harness.Finder.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_search_with_auto_download_queues_a_confident_match()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz)];

        await using var context = _harness.NewContext();
        var message = await SearchHandler(context).ExecuteAsync(
            ContextFor(MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Search(song.Id, AutoDownload: true)), CommandTrigger.Manual),
            CancellationToken.None);

        message.Should().Contain("queued the download");
        (await context.MusicVideos.SingleAsync()).Status.Should().Be(MusicVideoStatus.Queued);
    }

    [Fact]
    public async Task A_song_search_leaves_a_non_confident_candidate_to_the_person()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: false)];

        await using var context = _harness.NewContext();
        var message = await SearchHandler(context).ExecuteAsync(
            ContextFor(MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Search(song.Id, AutoDownload: true)), CommandTrigger.Manual),
            CancellationToken.None);

        message.Should().Contain("no confident match");
        (await context.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_song_search_without_auto_download_downloads_nothing()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz)];

        await using var context = _harness.NewContext();
        var message = await SearchHandler(context).ExecuteAsync(
            ContextFor(MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Search(song.Id, AutoDownload: false)), CommandTrigger.Manual),
            CancellationToken.None);

        message.Should().Contain("not downloaded");
        (await context.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_download_command_needs_a_body_that_names_the_video()
    {
        var handler = new MusicVideoDownloadCommandHandler(Substitute.For<IMusicVideoImporter>());

        var noBody = () => handler.ExecuteAsync(ContextFor(null), CancellationToken.None);
        var noVideo = () => handler.ExecuteAsync(
            ContextFor(MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(1, string.Empty, MusicVideoFoundBy.Manual))),
            CancellationToken.None);

        await noBody.Should().ThrowAsync<ArgumentException>();
        await noVideo.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_download_command_hands_the_body_to_the_importer()
    {
        var importer = Substitute.For<IMusicVideoImporter>();
        importer.ImportAsync(7, VideoId, MusicVideoFoundBy.Manual, Arg.Any<CancellationToken>()).Returns("Song 7: filed.");
        var handler = new MusicVideoDownloadCommandHandler(importer);

        var message = await handler.ExecuteAsync(
            ContextFor(MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(7, VideoId, MusicVideoFoundBy.Manual))),
            CancellationToken.None);

        message.Should().Be("Song 7: filed.");
    }

    [Fact]
    public void The_command_body_reads_the_json_the_api_writes()
    {
        var body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(7, VideoId, MusicVideoFoundBy.MusicBrainz));

        body.Should().Be("{\"songId\":7,\"youTubeVideoId\":\"u7K72X4eo_s\",\"foundBy\":\"musicBrainz\"}");
        MusicVideoCommandBodies.Read<MusicVideoCommandBodies.Download>(body, "x").Should()
            .Be(new MusicVideoCommandBodies.Download(7, VideoId, MusicVideoFoundBy.MusicBrainz));
    }

    [Fact]
    public void The_scheduled_task_is_absent_while_the_feature_or_the_schedule_is_off()
    {
        var options = new MusicVideoOptions { Enabled = false, ScheduledSearchEnabled = false };
        var source = new MusicVideoScheduledTasks(new TestOptionsMonitor<MusicVideoOptions>(options));

        source.Tasks.Should().BeEmpty("both switches are off");

        options.ScheduledSearchEnabled = true;
        source.Tasks.Should().BeEmpty("music videos are off");

        options.Enabled = true;
        options.ScheduledSearchEnabled = false;
        source.Tasks.Should().BeEmpty("the schedule is off");
    }

    [Fact]
    public void The_scheduled_task_runs_at_its_interval_while_both_are_on()
    {
        var options = new MusicVideoOptions { Enabled = true, ScheduledSearchEnabled = true, ScheduledSearchIntervalHours = 6 };
        var source = new MusicVideoScheduledTasks(new TestOptionsMonitor<MusicVideoOptions>(options));

        var task = source.Tasks.Should().ContainSingle().Subject;

        task.CommandName.Should().Be(MusicVideoMissingSearchCommandHandler.CommandName);
        task.Interval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void The_catalog_lists_the_task_only_while_it_is_switched_on_and_tells_the_scheduler_when_that_changes()
    {
        var options = new MusicVideoOptions { Enabled = true };
        var monitor = new ChangingMonitor<MusicVideoOptions>(options);
        var catalog = new ScheduledTaskCatalog(
            new TestOptionsMonitor<Wondarr.Core.Searching.SearchOptions>(new Wondarr.Core.Searching.SearchOptions()),
            new TestOptionsMonitor<Wondarr.Core.Backup.BackupOptions>(new Wondarr.Core.Backup.BackupOptions()),
            [new MusicVideoScheduledTasks(monitor)]);
        var changes = 0;

        using var subscription = catalog.OnChange(() => changes++);

        catalog.Tasks.Should().NotContain(task => task.CommandName == MusicVideoMissingSearchCommandHandler.CommandName);

        options.ScheduledSearchEnabled = true;
        monitor.Raise();

        changes.Should().Be(1);
        catalog.Tasks.Should().ContainSingle(task => task.CommandName == MusicVideoMissingSearchCommandHandler.CommandName)
            .Which.Interval.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void The_scheduled_search_is_off_and_daily_by_default()
    {
        var options = new MusicVideoOptions();

        options.ScheduledSearchEnabled.Should().BeFalse();
        options.ScheduledSearchIntervalHours.Should().Be(24);
        new MusicVideoScheduledTasks(new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = true }))
            .Tasks.Should().BeEmpty();
    }

    [Fact]
    public void The_interval_is_validated()
    {
        var validator = new MusicVideoOptionsValidator();

        validator.Validate(null, new MusicVideoOptions { ScheduledSearchIntervalHours = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new MusicVideoOptions { ScheduledSearchIntervalHours = 721 }).Failed.Should().BeTrue();
        validator.Validate(null, new MusicVideoOptions { ScheduledSearchIntervalHours = 1 }).Failed.Should().BeFalse();
    }

    private MusicVideoMissingSearchCommandHandler Handler()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _harness.NewContext());
        services.AddScoped<IMusicVideoSearchService>(provider => _harness.Search(provider.GetRequiredService<WondarrDbContext>()));

        return new MusicVideoMissingSearchCommandHandler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
            NullLogger<MusicVideoMissingSearchCommandHandler>.Instance);
    }

    private MusicVideoSearchCommandHandler SearchHandler(WondarrDbContext context) =>
        new(
            _harness.Search(context),
            new TestOptionsMonitor<MusicVideoOptions>(_harness.Options),
            NullLogger<MusicVideoSearchCommandHandler>.Instance);

    /// <summary>An options monitor that can be told a change happened.</summary>
    private sealed class ChangingMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        public ChangingMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);

            return null;
        }

        public void Raise()
        {
            foreach (var listener in _listeners)
            {
                listener(CurrentValue, null);
            }
        }
    }
}
