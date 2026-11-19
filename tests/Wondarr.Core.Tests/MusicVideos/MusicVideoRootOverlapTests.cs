using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.References;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// The music video root belongs to the video feature alone: libraries and reference libraries cannot be created or
/// moved onto it, and the recycle bin's cleanup never walks it.
/// </summary>
public sealed class MusicVideoRootOverlapTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly MusicVideoOptions _videoOptions = new() { Enabled = true, RootPath = "/mnt/videos" };
    private ServiceProvider? _services;

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        _database.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData("/mnt/videos")]
    [InlineData("/mnt/videos/music")]
    [InlineData("/mnt/videos/")]
    [InlineData("/mnt")]
    public async Task A_library_root_that_overlaps_the_video_root_is_refused_on_create(string root)
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);
        var service = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));

        var act = () => service.CreateAsync(NewLibrary(root), CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<ProfileValidationException>()).Which;
        failure.Errors.Should().Contain(error => error.Property == "rootPath" && error.Message.Contains("music video folder"));
    }

    [Fact]
    public async Task A_library_root_moved_onto_the_video_root_is_refused_on_update()
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);
        var service = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));
        var seeded = await service.GetAsync(SeedData.DefaultLibraryId, CancellationToken.None);
        var edited = new Library
        {
            Id = seeded!.Id,
            Name = seeded.Name,
            RootPath = "/mnt/videos/music",
            Layout = seeded.Layout,
            NamingTemplate = seeded.NamingTemplate,
            SidecarOptions = seeded.SidecarOptions,
            AlbumPolicy = seeded.AlbumPolicy,
            MinTracksPerRealAlbum = seeded.MinTracksPerRealAlbum,
            IsDefault = seeded.IsDefault,
        };

        var act = () => service.UpdateAsync(edited, CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<ProfileValidationException>()).Which;
        failure.Errors.Should().Contain(error => error.Property == "rootPath" && error.Message.Contains("music video folder"));
    }

    [Fact]
    public async Task The_default_video_root_takes_no_folder_from_a_library_while_the_feature_is_off()
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);
        var options = new MusicVideoOptions { Enabled = false, RootPath = "/mnt/vid/music-videos" };

        var off = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(options));
        (await off.CreateAsync(NewLibrary("/mnt/vid"), CancellationToken.None)).Id.Should().BeGreaterThan(0);

        options.Enabled = true;
        options.RootPath = "/mnt/vid2/music-videos";
        var on = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(options));
        var act = () => on.CreateAsync(NewLibrary("/mnt/two"), CancellationToken.None);
        await act.Should().NotThrowAsync("/mnt/two does not overlap the video root");

        var overlapping = () => on.CreateAsync(NewLibrary("/mnt/vid2"), CancellationToken.None);
        (await overlapping.Should().ThrowAsync<ProfileValidationException>()).Which
            .Errors.Should().Contain(error => error.Message.Contains("music video folder"));
    }

    [Fact]
    public void The_active_root_is_the_root_only_while_the_feature_is_on()
    {
        new MusicVideoOptions { Enabled = false, RootPath = "/x" }.ActiveRoot.Should().BeNull();
        new MusicVideoOptions { Enabled = true, RootPath = "/x" }.ActiveRoot.Should().Be("/x");
        new MusicVideoOptions { Enabled = true, RootPath = " " }.ActiveRoot.Should().BeNull();
    }

    [Fact]
    public async Task A_library_elsewhere_is_fine_and_so_is_any_root_while_no_video_root_is_set()
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);

        var elsewhere = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));
        (await elsewhere.CreateAsync(NewLibrary("/mnt/other"), CancellationToken.None)).Id.Should().BeGreaterThan(0);

        _videoOptions.RootPath = null;
        var none = new LibraryService(context, musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));
        (await none.CreateAsync(NewLibrary("/mnt/videos"), CancellationToken.None)).Id.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("/mnt/videos")]
    [InlineData("/mnt/videos/mine")]
    [InlineData("/mnt")]
    public async Task A_reference_library_root_that_overlaps_the_video_root_is_refused(string root)
    {
        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);
        var service = new ReferenceLibraryService(
            context,
            new TestOptionsMonitor<ImportOptions>(new ImportOptions()),
            new WondarrPaths(_temp.Full("config")),
            musicVideoOptions: new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));

        var act = () => service.AddAsync(
            new ReferenceLibraryInput("Mine", root, ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which;
        failure.Field.Should().Be("rootPath");
        failure.Detail.Should().Contain("music video folder");
    }

    [Fact]
    public async Task The_recycle_bin_cleanup_never_walks_the_video_root()
    {
        // import.recycle_bin_path: media, with the video root inside it.
        var options = new ImportOptions { RecycleBinPath = _temp.Full("media"), RecycleBinCleanupDays = 7 };
        _temp.CreateFile("media/.wondarr-recycle-bin", string.Empty);
        var recycled = Old("media/Artist/recycled.mp3");
        var video = Old("media/videos/Artist/Artist - Song.mkv");
        _videoOptions.RootPath = _temp.Full("media/videos");

        await _database.MigrateAsync(_time);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IOptionsMonitor<MusicVideoOptions>>(new TestOptionsMonitor<MusicVideoOptions>(_videoOptions));
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        _services = services.BuildServiceProvider();

        var bin = new RecycleBin(
            new DiskOperations(),
            new TestOptionsMonitor<ImportOptions>(options),
            new WondarrPaths(_temp.Full("config")),
            _time,
            NullLogger<RecycleBin>.Instance,
            _services.GetRequiredService<IServiceScopeFactory>());

        (await bin.CleanupAsync(CancellationToken.None)).Should().Be(1);

        File.Exists(recycled).Should().BeFalse("a recycled file past the cleanup age goes");
        File.Exists(video).Should().BeTrue("the video root is not the bin's to delete from");
    }

    private string Old(string relative)
    {
        var path = _temp.CreateFile(relative, "old");
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-30));

        return path;
    }

    private static Library NewLibrary(string root) => new()
    {
        Name = "Videos-test " + Guid.NewGuid().ToString("N")[..6],
        RootPath = root,
        Layout = LibraryLayout.Plexamp,
        NamingTemplate = string.Empty,
        SidecarOptions = "{}",
        AlbumPolicy = AlbumPolicy.FewestAlbums,
        MinTracksPerRealAlbum = 2,
    };
}
