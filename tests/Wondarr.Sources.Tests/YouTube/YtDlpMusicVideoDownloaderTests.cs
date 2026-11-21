using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wondarr.Core.MusicVideos;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>The adapter between the music video importer and yt-dlp: its work folders and what it hands over.</summary>
public sealed class YtDlpMusicVideoDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-mv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private YtDlpMusicVideoDownloader Build(IYtDlpRunner runner) =>
        new(
            runner,
            new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions { DownloadsDir = _root }),
            NullLogger<YtDlpMusicVideoDownloader>.Instance);

    [Fact]
    public async Task It_downloads_into_a_work_folder_of_its_own_and_hands_over_the_files()
    {
        var runner = Substitute.For<IYtDlpRunner>();
        string? workFolder = null;
        runner.DownloadVideoAsync("u7K72X4eo_s", Arg.Any<string>(), 720, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                workFolder = call.ArgAt<string>(1);
                var video = Path.Combine(workFolder, "u7K72X4eo_s.mkv");
                File.WriteAllText(video, "mkv");
                File.WriteAllText(Path.ChangeExtension(video, ".jpg"), "jpg");

                return new YtDlpVideoDownload("u7K72X4eo_s", video, Path.ChangeExtension(video, ".jpg"), 284_000);
            });

        var download = await Build(runner).DownloadAsync("u7K72X4eo_s", 720, CancellationToken.None);

        workFolder.Should().StartWith(Path.Combine(_root, "music-videos"));
        download.VideoPath.Should().Be(Path.Combine(workFolder!, "u7K72X4eo_s.mkv"));
        download.ThumbnailPath.Should().Be(Path.Combine(workFolder!, "u7K72X4eo_s.jpg"));
        download.ReportedDurationMs.Should().Be(284_000);
    }

    [Fact]
    public async Task A_thumbnail_yt_dlp_did_not_write_is_null()
    {
        var runner = Substitute.For<IYtDlpRunner>();
        runner.DownloadVideoAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var video = Path.Combine(call.ArgAt<string>(1), "abc.mkv");
            File.WriteAllText(video, "mkv");

            return new YtDlpVideoDownload("abc", video, Path.ChangeExtension(video, ".jpg"), null);
        });

        var download = await Build(runner).DownloadAsync("abc", null, CancellationToken.None);

        download.ThumbnailPath.Should().BeNull();
        download.ReportedDurationMs.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_download_removes_its_work_folder_and_rethrows()
    {
        var runner = Substitute.For<IYtDlpRunner>();
        runner.DownloadVideoAsync(default!, default!, default, default).ThrowsAsyncForAnyArgs(
            new YtDlpException("abc", YtDlpErrorKind.Unknown, "boom"));

        var act = () => Build(runner).DownloadAsync("abc", null, CancellationToken.None);

        await act.Should().ThrowAsync<YtDlpException>();
        Directory.GetDirectories(Path.Combine(_root, "music-videos")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_yt_dlp_reported_but_did_not_write_is_a_failure()
    {
        var runner = Substitute.For<IYtDlpRunner>();
        runner.DownloadVideoAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
            new YtDlpVideoDownload("abc", Path.Combine(call.ArgAt<string>(1), "abc.mkv"), Path.Combine(call.ArgAt<string>(1), "abc.jpg"), 1));

        var act = () => Build(runner).DownloadAsync("abc", null, CancellationToken.None);

        await act.Should().ThrowAsync<YtDlpException>();
        Directory.GetDirectories(Path.Combine(_root, "music-videos")).Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_removes_the_work_folder_and_whatever_is_left_in_it()
    {
        var runner = Substitute.For<IYtDlpRunner>();
        runner.DownloadVideoAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var video = Path.Combine(call.ArgAt<string>(1), "abc.mkv");
            File.WriteAllText(video, "mkv");
            File.WriteAllText(Path.Combine(call.ArgAt<string>(1), "abc.f137.mp4.part"), "left over");

            return new YtDlpVideoDownload("abc", video, Path.ChangeExtension(video, ".jpg"), 1);
        });
        var downloader = Build(runner);
        var download = await downloader.DownloadAsync("abc", null, CancellationToken.None);

        downloader.Discard(download);

        Directory.Exists(Path.GetDirectoryName(download.VideoPath)).Should().BeFalse();
    }

    [Fact]
    public void Discard_never_removes_a_folder_outside_the_work_root()
    {
        var outside = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.mkv"), "mine");

        Build(Substitute.For<IYtDlpRunner>()).Discard(new MusicVideoDownload(Path.Combine(outside, "keep.mkv"), null, null));

        File.Exists(Path.Combine(outside, "keep.mkv")).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_youtube_source_replaces_the_core_stand_ins_whichever_is_registered_first(bool youTubeFirst)
    {
        var services = new ServiceCollection();

        if (youTubeFirst)
        {
            services.AddWondarrYouTube();
        }

        // What Wondarr.Core registers: stand-ins that find nothing and download nothing.
        services.TryAddScoped<IMusicVideoFinder, NoMusicVideoFinder>();
        services.TryAddScoped<IMusicVideoDownloader, NoMusicVideoDownloader>();

        if (!youTubeFirst)
        {
            services.AddWondarrYouTube();
        }

        services.AddScoped(_ => Substitute.For<Wondarr.Core.Metadata.MusicBrainz.IMusicBrainzClient>());
        services.AddSingleton(Substitute.For<Wondarr.Core.Media.IProcessRunner>());
        services.AddSingleton(new Wondarr.Core.Configuration.WondarrPaths(Path.GetTempPath()));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMusicVideoDownloader>().Should().BeOfType<YtDlpMusicVideoDownloader>();
        scope.ServiceProvider.GetRequiredService<IMusicVideoFinder>().Should().BeOfType<YouTubeMusicVideoFinder>();
    }
}
