using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.References;
using Wondarr.Core.Tagging;
using Wondarr.Core.Tests.Media;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// A reference scan with the real ffprobe and ffmpeg over damaged MP3s: a few bad frames are scanned and
/// noted, anything worse stays unreadable. Skipped where the tools are not installed.
/// </summary>
public sealed class ReferenceScanToleranceLiveTests : IDisposable
{
    private static readonly JsonSerializerOptions StoredJson = new() { PropertyNameCaseInsensitive = true };

    private readonly string _root =Directory.CreateTempSubdirectory("wondarr-tolerance-scan-").FullName;
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    [FfmpegFact]
    public async Task A_scan_reads_files_with_a_few_bad_frames_and_leaves_the_ruined_ones_unreadable()
    {
        int[] ruined = [.. Enumerable.Range(0, 30).Select(index => 100 + (index * 30))];

        DamagedMp3.Write(_root, "Eddy Grant - Electric Avenue.mp3", await DamagedMp3.CreateAsync(wholeFrames: true, 100, 200, 300, 400, 500, 600));
        DamagedMp3.Write(_root, "Supertramp - Give A Little Bit.mp3", await DamagedMp3.CreateAsync(wholeFrames: false, 300));
        DamagedMp3.Write(_root, "ruined.mp3", await DamagedMp3.CreateAsync(wholeFrames: false, ruined));
        DamagedMp3.Write(_root, "cut.mp3", DamagedMp3.Truncate(await DamagedMp3.CreateAsync(wholeFrames: false, 100)));
        DamagedMp3.Write(_root, "cover.mp3", await File.ReadAllBytesAsync(MediaFixtures.File(Path.Combine("covers", "cover-300x300.jpg"))));
        DamagedMp3.Write(_root, "tone.mp3", await File.ReadAllBytesAsync(MediaFixtures.File("tone-320.mp3")));

        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);

        var library = new ReferenceLibrary { Name = "Music", RootPath = _root };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        var log = new RecordingLogger<ReferenceScanner>();
        var scanner = new ReferenceScanner(context, DamagedMp3.RealProbe(), Substitute.For<ITagReader>(), _time, log);

        var result = await scanner.ScanAsync(library.Id, null, CancellationToken.None);

        result.Seen.Should().Be(6);
        result.Unreadable.Should().Be(3, "the ruined, the cut and the cover");

        var rows = await context.ReferenceFiles.AsNoTracking().ToDictionaryAsync(row => row.RelativePath);

        foreach (var name in new[] { "Eddy Grant - Electric Avenue.mp3", "Supertramp - Give A Little Bit.mp3" })
        {
            rows[name].State.Should().Be(ReferenceFileState.Pending, name);
            rows[name].Probe.Should().NotBeNull(name);
            rows[name].Message.Should().MatchRegex(@"^Decodes with \d+ errors$", name);
        }

        rows["tone.mp3"].State.Should().Be(ReferenceFileState.Pending);
        rows["tone.mp3"].Message.Should().BeNull();

        foreach (var name in new[] { "ruined.mp3", "cut.mp3", "cover.mp3" })
        {
            rows[name].State.Should().Be(ReferenceFileState.Unreadable, name);
            rows[name].Probe.Should().BeNull(name);
        }

        rows["cover.mp3"].Message.Should().Be("the file has no audio stream");

        log.Entries
            .Where(entry => entry.Level == LogLevel.Information && entry.Message.Contains("scanned anyway", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .Should().HaveCount(2)
            .And.OnlyContain(message => message.Contains(" decodes with ", StringComparison.Ordinal))
            .And.Contain(message => message.StartsWith(Path.Combine(_root, "Eddy Grant - Electric Avenue.mp3"), StringComparison.Ordinal));
    }

    [FfmpegFact(fpcalc: true)]
    public async Task A_file_the_scan_tolerated_reaches_a_fingerprint()
    {
        DamagedMp3.Write(_root, "Eddy Grant - Electric Avenue.mp3", await DamagedMp3.CreateAsync(wholeFrames: true, 100, 200, 300, 400, 500, 600));
        DamagedMp3.Write(_root, "Supertramp - Give A Little Bit.mp3", await DamagedMp3.CreateAsync(wholeFrames: false, 300));

        await _database.MigrateAsync(_time);
        await using var context = _database.CreateContext(_time);

        var library = new ReferenceLibrary { Name = "Music", RootPath = _root };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        var scanner = new ReferenceScanner(
            context,
            DamagedMp3.RealProbe(),
            Substitute.For<ITagReader>(),
            _time,
            new RecordingLogger<ReferenceScanner>());

        (await scanner.ScanAsync(library.Id, null, CancellationToken.None)).Unreadable.Should().Be(0);

        var rows = await context.ReferenceFiles.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(row => row.State == ReferenceFileState.Pending);

        // What the identifier does next with each Pending row: the start of the file, for AcoustID.
        var fingerprinter = DamagedMp3.RealFingerprinter();

        foreach (var row in rows)
        {
            var probe = JsonSerializer.Deserialize<MediaInfo>(row.Probe!, StoredJson)!;

            var fingerprint = await fingerprinter.FingerprintAsync(
                Path.Combine(_root, row.RelativePath),
                FingerprintWindow.Start,
                probe.DurationMs,
                CancellationToken.None);

            fingerprint.Success.Should().BeTrue($"{row.RelativePath}: {fingerprint.Error}");
            fingerprint.Fingerprint.Should().NotBeNullOrEmpty(row.RelativePath);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder under the OS temp directory is harmless.
        }
    }
}
