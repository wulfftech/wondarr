using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// A probe that answers by file name and remembers what it was asked, so a test can say what a video "is" without
/// ffprobe, and can count the probes a scan made.
/// </summary>
internal sealed class ScanProbe : IMediaProbe
{
    private readonly object _gate = new();
    private int _running;

    /// <summary>Gets the paths probed, in the order they were asked.</summary>
    public List<string> Probed { get; } = [];

    /// <summary>Gets the most probes that ran at the same time.</summary>
    public int MaxConcurrent { get; private set; }

    /// <summary>Gets or sets what a probe answers for a path; <see langword="null"/> says the default video.</summary>
    public Func<string, VideoProbeResult>? Answer { get; set; }

    /// <summary>Gets or sets how long each probe takes, so a test can see two overlap.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>A readable video of the given length, with the container's tags.</summary>
    public static VideoProbeResult Video(int durationMs = 284_000, string? tagTitle = null, string? tagArtist = null) =>
        new(true, new VideoInfo(1, 1, 1920, 1080, "h264", "aac", durationMs, 14, "matroska,webm", tagTitle, tagArtist), null);

    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async Task<VideoProbeResult> ProbeVideoAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Probed.Add(path);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            return Answer is null ? Video() : Answer(path);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }
}

/// <summary>Builds the scan side of the music video services over a <see cref="MusicVideoHarness"/>.</summary>
internal static class MusicVideoScanTestKit
{
    public static MusicVideoFileScanner Scanner(this MusicVideoHarness harness, WondarrDbContext context, ScanProbe probe) =>
        new(
            context,
            probe,
            new TestOptionsMonitor<MusicVideoOptions>(harness.Options),
            new TestOptionsMonitor<ImportOptions>(harness.Import),
            new WondarrPaths(harness.Temp.Full("config")),
            new LibraryRoots(new DiskOperations()),
            harness.Time,
            NullLogger<MusicVideoFileScanner>.Instance);

    public static MusicVideoIdentifier Identifier(this MusicVideoHarness harness, WondarrDbContext context) =>
        new(context, new TestOptionsMonitor<MusicVideoOptions>(harness.Options), NullLogger<MusicVideoIdentifier>.Instance);

    public static MusicVideoMatchService Matches(this MusicVideoHarness harness, WondarrDbContext context) =>
        new(
            context,
            new TestOptionsMonitor<MusicVideoOptions>(harness.Options),
            harness.Videos(context),
            NullLogger<MusicVideoMatchService>.Instance);

    /// <summary>Writes a video file (and optionally its <c>.nfo</c>) under the video root.</summary>
    public static string Video(this MusicVideoHarness harness, string relative, string content = "video bytes", string? nfo = null)
    {
        var path = harness.Temp.CreateFile("videos/" + relative, content);

        if (nfo is not null)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".nfo"), nfo);
        }

        return path;
    }

    /// <summary>A Kodi <c>musicvideo</c> document.</summary>
    public static string Nfo(string title, params string[] artists) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><musicvideo><title>"
        + title
        + "</title>"
        + string.Concat(artists.Select(artist => "<artist>" + artist + "</artist>"))
        + "</musicvideo>";

    /// <summary>Scans, then identifies, each in a context of its own, as the command does in one scope.</summary>
    public static async Task<(MusicVideoScanResult Scan, MusicVideoIdentifyResult Identified)> ScanAndIdentifyAsync(
        this MusicVideoHarness harness,
        ScanProbe probe)
    {
        await using var context = harness.NewContext();
        var scan = await harness.Scanner(context, probe).ScanAsync(null, CancellationToken.None);
        var identified = await harness.Identifier(context).IdentifyPendingAsync(null, CancellationToken.None);

        return (scan, identified);
    }

    /// <summary>Every file row, by path.</summary>
    public static async Task<List<MusicVideoFile>> FilesAsync(this MusicVideoHarness harness)
    {
        await using var context = harness.NewContext();

        return await context.MusicVideoFiles.AsNoTracking().OrderBy(file => file.Path).ToListAsync();
    }
}
