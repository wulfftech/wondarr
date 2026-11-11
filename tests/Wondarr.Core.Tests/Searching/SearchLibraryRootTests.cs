using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Searching;
using Wondarr.Core.Tests.Support;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The automatic searches leave out the songs of a library whose root is missing or unmarked: a grab
/// for them would only wait in the queue behind an import that cannot happen.
/// </summary>
public sealed class SearchLibraryRootTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private static CommandContext Context(string? body = null, CommandTrigger trigger = CommandTrigger.Scheduled) =>
        new(1, body, trigger, _ => Task.CompletedTask);

    private static string Marker(SearchTestHost host) => Path.Combine(host.LibraryRoot, LibraryRoots.MarkerFileName);

    [Fact]
    public async Task Missing_search_skips_the_songs_of_an_unmarked_library_until_the_marker_is_back()
    {
        await using var host = await SearchTestHost.CreateAsync();
        await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var handler = new MissingSearchCommandHandler(host.Scopes, host.Monitor, host.Time, TestLibraryRoots.Real, NullLogger<MissingSearchCommandHandler>.Instance);
        File.Delete(Marker(host));

        var message = await handler.ExecuteAsync(Context(), Token);

        message.Should().Be("0 songs: 0 grabbed, 0 without an acceptable result, 0 skipped");
        host.Provider.Requests.Should().BeEmpty();

        // No run is recorded for a song that was never searched, so it is first in line when the share returns.
        TestLibraryRoots.Real.Mark(host.LibraryRoot);

        (await handler.ExecuteAsync(Context(), Token)).Should().Be("1 songs: 1 grabbed, 0 without an acceptable result, 0 skipped");
    }

    [Fact]
    public async Task Missing_search_for_named_songs_search_on_add_skips_them_too()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var handler = new MissingSearchCommandHandler(host.Scopes, host.Monitor, host.Time, TestLibraryRoots.Real, NullLogger<MissingSearchCommandHandler>.Instance);
        Directory.Delete(host.LibraryRoot, recursive: true);

        var message = await handler.ExecuteAsync(Context($"{{\"name\":\"MissingSearch\",\"songIds\":[{songId}]}}"), Token);

        message.Should().StartWith("0 songs:");
        host.Provider.Requests.Should().BeEmpty();
        Directory.Exists(host.LibraryRoot).Should().BeFalse();
    }

    [Fact]
    public async Task Upgrade_search_skips_the_songs_of_an_unmarked_library()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 17);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var handler = new UpgradeSearchCommandHandler(host.Scopes, host.Monitor, host.Time, TestLibraryRoots.Real, NullLogger<UpgradeSearchCommandHandler>.Instance);
        File.Delete(Marker(host));

        var message = await handler.ExecuteAsync(Context(), Token);

        message.Should().StartWith("0 songs:");
        host.Provider.Requests.Should().BeEmpty();

        TestLibraryRoots.Real.Mark(host.LibraryRoot);

        (await handler.ExecuteAsync(Context(), Token)).Should().StartWith("1 songs:");
    }

    [Fact]
    public async Task Search_on_add_for_one_song_skips_an_unmarked_library_but_a_manual_search_now_does_not()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var handler = new SongSearchCommandHandler(
            host.Search,
            host.SlotWait,
            NullLogger<SongSearchCommandHandler>.Instance,
            host.Context,
            TestLibraryRoots.Real);
        var body = $"{{\"name\":\"SongSearch\",\"songId\":{songId}}}";
        File.Delete(Marker(host));

        var skipped = await handler.ExecuteAsync(Context(body, CommandTrigger.Unspecified), Token);

        skipped.Should().Contain("Skipped").And.Contain("has no .wondarr-library file");
        host.Provider.Requests.Should().BeEmpty();

        var manual = await handler.ExecuteAsync(Context(body, CommandTrigger.Manual), Token);

        manual.Should().NotContain("Skipped", "a person pressing search now is not held back: the grab waits in the queue");
        host.Provider.Requests.Should().NotBeEmpty();
    }
}
