using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Importing;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Conversion on demand (DECISIONS build session 7 #6): the original is recycled by the organizer, the
/// song keeps its quality, the temporary file never stays behind, and a reference file, a same-codec
/// file, a lossy-to-lossless rule and a busy song are left alone.
/// </summary>
public sealed class FileConverterTests : IDisposable
{
    private const long FlacQualityId = 36;

    private const long Mp3QualityId = 23;

    private const string Mp3Rule = """{"codec":"mp3","bitrateKbps":320}""";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeTranscoder _transcoder = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeOrganizer _organizer;
    private readonly FakeReplayGainAnalyzer _replay = new();
    private readonly IPlexLibraryUpdater _updater = Substitute.For<IPlexLibraryUpdater>();
    private readonly string _root;
    private readonly string _bin;

    public FileConverterTests()
    {
        var work = Path.Combine(Path.GetTempPath(), "wondarr-convert-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(work, "music");
        _bin = Path.Combine(work, "recycle");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_bin);
        _organizer = new FakeOrganizer(_bin);
    }

    [Fact]
    public async Task A_flac_is_converted_the_original_recycled_and_the_quality_kept()
    {
        await using var context = await ContextAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");
        var (songId, path) = await SeedAsync(context, "flac", FlacQualityId, lyrics: true);

        var result = await Converter(context).ConvertAsync(songId, null, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Converted, result.Reason);
        var file = await context.SongFiles.AsNoTracking().SingleAsync();
        file.Path.Should().EndWith(".mp3");
        file.Codec.Should().Be("mp3");
        file.QualityId.Should().Be(FlacQualityId, "a converted file is ranked as what was downloaded (#5)");
        File.Exists(file.Path).Should().BeTrue();
        File.Exists(path).Should().BeFalse("the organizer recycled the original");
        File.Exists(Path.Combine(_bin, Path.GetFileName(path))).Should().BeTrue();
        File.Exists(Path.ChangeExtension(file.Path, ".lrc")).Should().BeTrue("the lyrics follow the file");
        Directory.GetFiles(Path.Combine(_root, FileConverter.WorkFolderName)).Should().BeEmpty();

        _organizer.Requests.Single().ReplacesPath.Should().Be(path);
        (await context.History.AsNoTracking().SingleAsync()).EventType.Should().Be(HistoryEventType.Converted);
        _updater.Received().RequestFolder(SeedData.DefaultLibraryId, Path.GetDirectoryName(file.Path)!);
    }

    [Fact]
    public async Task A_converted_file_is_measured_again_when_the_library_has_replaygain_on()
    {
        await using var context = await ContextAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");
        (await context.Libraries.SingleAsync()).ReplayGain = true;
        await context.SaveChangesAsync();
        var (songId, _) = await SeedAsync(context, "flac", FlacQualityId);
        await context.SongFiles.ExecuteUpdateAsync(update => update
            .SetProperty(file => file.ReplayGainDb, -3.0)
            .SetProperty(file => file.ReplayGainPeak, 0.5));

        var result = await Converter(context).ConvertAsync(songId, null, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Converted, result.Reason);
        _replay.Measured.Should().ContainSingle().Which.Should().Contain(FileConverter.WorkFolderName, "the converted temporary is what is measured");
        var request = _organizer.Requests.Single();
        request.ReplayGainDb.Should().Be(-8.52);
        request.ReplayGainPeak.Should().Be(1.047129);
        var file = await context.SongFiles.AsNoTracking().SingleAsync();
        file.ReplayGainDb.Should().Be(-8.52, "the encoded file's own measurement replaces the old one");
        file.ReplayGainPeak.Should().Be(1.047129);
    }

    [Fact]
    public async Task A_converted_file_is_not_measured_when_the_library_has_replaygain_off()
    {
        await using var context = await ContextAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");
        var (songId, _) = await SeedAsync(context, "flac", FlacQualityId);

        var result = await Converter(context).ConvertAsync(songId, null, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Converted, result.Reason);
        _replay.Measured.Should().BeEmpty();
        _organizer.Requests.Single().ReplayGainDb.Should().BeNull();
    }

    [Fact]
    public async Task A_file_already_in_the_target_codec_is_skipped_without_a_transcode()
    {
        await using var context = await ContextAsync(null);
        var (songId, _) = await SeedAsync(context, "mp3", Mp3QualityId);

        var result = await Converter(context).ConvertAsync(songId, Mp3Rule, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Skipped);
        _transcoder.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_reference_file_is_never_touched()
    {
        await using var context = await ContextAsync(null);
        var (songId, path) = await SeedAsync(context, "flac", FlacQualityId, sourceType: SourceTypes.Reference);

        var result = await Converter(context).ConvertAsync(songId, Mp3Rule, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Skipped);
        File.Exists(path).Should().BeTrue();
        _transcoder.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_lossless_rule_for_a_lossy_file_is_refused()
    {
        await using var context = await ContextAsync(null);
        var (songId, path) = await SeedAsync(context, "mp3", Mp3QualityId);

        var result = await Converter(context).ConvertAsync(songId, """{"codec":"flac"}""", CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Refused);
        File.Exists(path).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_root, FileConverter.WorkFolderName)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_converted_file_of_the_wrong_length_fails_and_leaves_everything_as_it_was()
    {
        await using var context = await ContextAsync(null);
        var (songId, path) = await SeedAsync(context, "flac", FlacQualityId);
        _probe.ConvertedDurationOffsetMs = 3_000;

        var result = await Converter(context).ConvertAsync(songId, Mp3Rule, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Failed);
        File.Exists(path).Should().BeTrue();
        (await context.SongFiles.AsNoTracking().SingleAsync()).Path.Should().Be(path);
        Directory.GetFiles(Path.Combine(_root, FileConverter.WorkFolderName)).Should().BeEmpty();
        _organizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_with_an_unfinished_compaction_move_is_refused()
    {
        await using var context = await ContextAsync(null);
        var (songId, path) = await SeedAsync(context, "flac", FlacQualityId);
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = songId,
            FromPath = path,
            ToPath = path + ".moved",
            Proposed = "{}",
            State = CompactMoveState.Planned,
        });
        await context.SaveChangesAsync();

        var result = await Converter(context).ConvertAsync(songId, Mp3Rule, CancellationToken.None);

        result.Outcome.Should().Be(ConvertOutcome.Refused);
        _transcoder.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_plan_counts_and_estimates_from_the_stored_rows_without_probing()
    {
        await using var context = await ContextAsync("""{"version":2,"lossless":{"codec":"mp3","bitrateKbps":320}}""");
        await SeedAsync(context, "flac", FlacQualityId);
        await SeedAsync(context, "mp3", Mp3QualityId);

        var plan = await Converter(context).PlanAsync(
            new ConvertRequest(null, SeedData.DefaultLibraryId, null),
            CancellationToken.None);

        plan.Convert.Should().Be(1);
        plan.Skip.Should().Be(1, "the lossy rule keeps the MP3");
        plan.EstimatedSize.Should().Be(320L * 125 * 369_000 / 1000);
        _probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_request_names_songs_or_a_library_but_not_both()
    {
        await using var context = await ContextAsync(null);

        var both = async () => await Converter(context).ResolveSongsAsync(
            new ConvertRequest([1], SeedData.DefaultLibraryId, null),
            CancellationToken.None);
        var unknown = async () => await Converter(context).ResolveSongsAsync(
            new ConvertRequest(null, 999, null),
            CancellationToken.None);

        (await both.Should().ThrowAsync<ConvertRequestException>()).Which.Field.Should().Be("songIds");
        (await unknown.Should().ThrowAsync<ConvertRequestException>()).Which.Field.Should().Be("libraryId");
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private FileConverter Converter(WondarrDbContext context) =>
        new(
            context,
            _transcoder,
            _probe,
            _organizer,
            _replay,
            new SongFileLock(),
            _updater,
            NullLogger<FileConverter>.Instance);

    private async Task<WondarrDbContext> ContextAsync(string? outputPolicy)
    {
        await _database.MigrateAsync(_timeProvider);

        var context = _database.CreateContext(_timeProvider);
        var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);
        library.RootPath = _root;
        library.OutputPolicy = outputPolicy;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return context;
    }

    private async Task<(long SongId, string Path)> SeedAsync(
        WondarrDbContext context,
        string codec,
        long qualityId,
        bool lyrics = false,
        string sourceType = SourceTypes.Soulseek)
    {
        var index = await context.Songs.CountAsync() + 1;
        var artist = new Artist { Name = "Artist " + index, SortName = "Artist " + index };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Song " + index,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            MbRecordingId = "m-" + index,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.Single,
            AlbumTitle = "Single " + index,
            AlbumArtist = artist.Name,
            AlbumKey = "s-" + index,
            TrackNo = 1,
            DiscNo = 1,
            TotalTracks = 1,
        });

        var path = Path.Combine(_root, artist.Name, "Single " + index, $"01 - Song {index}.{codec}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "the audio bytes");

        if (lyrics)
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:01.00] la");
        }

        context.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = path,
            Size = 30_000_000,
            Codec = codec,
            Container = codec,
            BitrateKbps = codec == "flac" ? 1000 : 320,
            DurationMs = 369_000,
            QualityId = qualityId,
            SourceType = sourceType,
            ImportedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        _probe.Known[path] = new MediaInfo(codec, codec, codec == "flac" ? 1000 : 320, 44_100, 16, 2, 369_000, codec == "flac", 30_000_000);

        return (song.Id, path);
    }

    /// <summary>Writes a file at the destination, as ffmpeg would; refuses lossless from lossy, as the real one does.</summary>
    private sealed class FakeTranscoder : ITranscoder
    {
        public int Calls { get; private set; }

        public async Task<TranscodeResult> TranscodeAsync(
            string sourcePath,
            OutputPolicy policy,
            string destinationPath,
            bool sourceIsLossless,
            CancellationToken cancellationToken)
        {
            Calls++;

            if (policy.Codec is OutputCodec.Flac or OutputCodec.Alac && !sourceIsLossless)
            {
                throw new TranscodePolicyException("Never lossless from lossy.");
            }

            await File.WriteAllTextAsync(destinationPath, "converted bytes", cancellationToken);

            return new TranscodeResult(destinationPath, policy.Container);
        }
    }

    /// <summary>Answers the seeded files, and anything else as the converted file.</summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public Dictionary<string, MediaInfo> Known { get; } = new(StringComparer.Ordinal);

        public int ConvertedDurationOffsetMs { get; set; }

        public int Calls { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;

            if (Known.TryGetValue(path, out var info))
            {
                return Task.FromResult(new MediaProbeResult(true, info, null));
            }

            var extension = Path.GetExtension(path).TrimStart('.');

            return Task.FromResult(new MediaProbeResult(
                true,
                new MediaInfo(extension, extension, 320, 44_100, null, 2, 369_000 + ConvertedDurationOffsetMs, false, 14_000_000),
                null));
        }
    }

    /// <summary>
    /// The organizer's contract without the tag writer: recycle what it replaces, then move the source
    /// beside it under the replaced file's name and the source's extension.
    /// </summary>
    private sealed class FakeOrganizer(string bin) : ILibraryOrganizer
    {
        public List<OrganizeRequest> Requests { get; } = [];

        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var replaces = request.ReplacesPath!;
            var target = Path.ChangeExtension(replaces, "." + request.Extension);
            var recycled = Path.Combine(bin, Path.GetFileName(replaces));

            File.Move(replaces, recycled);
            File.Move(request.SourcePath, target);

            return Task.FromResult(new OrganizeResult(
                OrganizeFailure.None,
                null,
                target,
                recycled,
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }
}
