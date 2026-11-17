using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// The music video settings: what a change may be, and what saving a root does to the folder. A real migrated
/// SQLite database holds the libraries, real temporary folders hold the roots, and the writer is a fake that applies
/// the keys to the options, as the real one does by reloading the file.
/// </summary>
public sealed class MusicVideoSettingsServiceTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private readonly MusicVideoOptions _options = new();
    private readonly ApplyingWriter _writer;
    private WondarrDbContext? _context;

    public MusicVideoSettingsServiceTests() => _writer = new ApplyingWriter(_options);

    public void Dispose()
    {
        _context?.Dispose();
        _temp.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_relative_root_is_refused_with_a_field_error()
    {
        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: "videos/music"));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Field.Should().Be("rootPath");
        _writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_root_inside_the_data_directory_is_refused()
    {
        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: "/config/videos"));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Field.Should().Be("rootPath");
        result.Errors[0].Message.Should().Contain("data directory");
    }

    [Theory]
    [InlineData("/data/music")]
    [InlineData("/data/music/videos")]
    public async Task A_root_inside_a_library_is_refused(string root)
    {
        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("overlaps the library 'Music'");
        _writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_root_inside_a_reference_library_is_refused()
    {
        await using (var context = await ContextAsync())
        {
            context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = "/mnt/mine" });
            await context.SaveChangesAsync();
        }

        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: "/mnt/mine/videos"));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("reference library 'Mine'");
    }

    [Fact]
    public async Task A_layout_other_than_the_two_is_refused()
    {
        var result = await UpdateAsync(new MusicVideoSettingsUpdate(Layout: "nested"));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Field.Should().Be("layout");
    }

    [Theory]
    [InlineData("{Artist}/{Track}")]
    [InlineData("{artist} - {Title}")]
    [InlineData("{Artist} - {Title")]
    [InlineData("{Artist} - {Title}}")]
    [InlineData("")]
    public async Task A_template_with_a_token_that_is_not_allowed_is_refused(string template)
    {
        var result = await UpdateAsync(new MusicVideoSettingsUpdate(NamingTemplate: template));

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Field.Should().Be("namingTemplate");
    }

    [Fact]
    public async Task Every_allowed_token_is_accepted_in_a_template()
    {
        var result = await UpdateAsync(
            new MusicVideoSettingsUpdate(NamingTemplate: "{AlbumArtist}/{Album} ({Year})/{Artist} - {Title}"));

        result.Success.Should().BeTrue();
        _options.NamingTemplate.Should().Be("{AlbumArtist}/{Album} ({Year})/{Artist} - {Title}");
    }

    [Fact]
    public async Task Only_the_changed_keys_are_written_and_the_defaults_are_the_documented_ones()
    {
        _options.Enabled.Should().BeFalse();
        _options.RootPath.Should().BeNull();
        _options.Layout.Should().Be("folder");
        _options.NamingTemplate.Should().Be("{Artist}/{Artist} - {Title}");
        _options.BesideTrackSuffix.Should().Be("-video");
        _options.MaxHeight.Should().BeNull();
        _options.WriteNfo.Should().BeTrue();
        _options.WriteThumbnail.Should().BeTrue();
        _options.ScheduledSearchLimit.Should().Be(25);

        var result = await UpdateAsync(
            new MusicVideoSettingsUpdate(Enabled: true, Layout: "beside_track", MaxHeight: 1080, WriteNfo: true));

        result.Success.Should().BeTrue();
        _writer.Writes.Should().ContainSingle();
        _writer.Writes[0].Keys.Should().BeEquivalentTo(["enabled", "layout", "max_height"], "write_nfo did not change");
    }

    [Fact]
    public async Task A_height_of_zero_removes_the_cap()
    {
        _options.MaxHeight = 720;

        var result = await UpdateAsync(new MusicVideoSettingsUpdate(MaxHeight: 0));

        result.Success.Should().BeTrue();
        _options.MaxHeight.Should().BeNull();
    }

    [Fact]
    public async Task A_field_the_environment_owns_is_refused_naming_the_variable()
    {
        var service = Service(new Hashtable { ["APP__MUSIC_VIDEOS__ENABLED"] = "false" });

        var result = await service.UpdateAsync(new MusicVideoSettingsUpdate(Enabled: true), CancellationToken.None);
        var settings = await service.GetAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("APP__MUSIC_VIDEOS__ENABLED");
        settings.ReadOnlyFields.Should().BeEquivalentTo(["enabled"]);
    }

    [Fact]
    public async Task A_new_root_that_is_an_existing_empty_folder_is_marked()
    {
        var root = _temp.Full("videos");
        Directory.CreateDirectory(root);

        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));
        var settings = await GetAsync();

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(root, LibraryRoots.MarkerFileName)).Should().BeTrue();
        settings.RootStatus.Should().Be(LibraryRootStatus.Ready);
        settings.RootNeedsConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_root_that_already_holds_the_marker_needs_nothing()
    {
        var root = _temp.Full("videos");
        new LibraryRoots(new DiskOperations()).Mark(root);
        File.WriteAllText(Path.Combine(root, "old.mkv"), "x");

        await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));
        var settings = await GetAsync();

        settings.RootStatus.Should().Be(LibraryRootStatus.Ready);
        settings.RootNeedsConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_root_that_holds_files_without_a_marker_is_left_for_the_user_to_confirm()
    {
        var root = _temp.Full("videos");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "something.mkv"), "x");

        var result = await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));
        var settings = await GetAsync();

        result.Success.Should().BeTrue("the root is saved; it is only not marked");
        File.Exists(Path.Combine(root, LibraryRoots.MarkerFileName)).Should().BeFalse();
        settings.RootStatus.Should().Be(LibraryRootStatus.Unmarked);
        settings.RootNeedsConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_root_that_does_not_exist_is_not_created_until_the_user_confirms()
    {
        var root = _temp.Full("not-yet");

        await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));
        var settings = await GetAsync();

        Directory.Exists(root).Should().BeFalse();
        settings.RootStatus.Should().Be(LibraryRootStatus.Missing);
        settings.RootNeedsConfirmation.Should().BeTrue();

        var confirmed = await Service().ConfirmRootAsync(CancellationToken.None);

        confirmed.Success.Should().BeTrue();
        File.Exists(Path.Combine(root, LibraryRoots.MarkerFileName)).Should().BeTrue();
        (await GetAsync()).RootNeedsConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Confirming_marks_a_non_empty_unmarked_root()
    {
        var root = _temp.Full("videos");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "something.mkv"), "x");
        await UpdateAsync(new MusicVideoSettingsUpdate(RootPath: root));

        var confirmed = await Service().ConfirmRootAsync(CancellationToken.None);

        confirmed.Success.Should().BeTrue();
        File.Exists(Path.Combine(root, LibraryRoots.MarkerFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task Confirming_without_a_saved_root_is_a_field_error()
    {
        var confirmed = await Service().ConfirmRootAsync(CancellationToken.None);

        confirmed.Success.Should().BeFalse();
        confirmed.Errors.Should().ContainSingle().Which.Field.Should().Be("rootPath");
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private MusicVideoSettingsService Service(IDictionary? environment = null)
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();
        var disk = new DiskOperations();

        return new MusicVideoSettingsService(
            new TestOptionsMonitor<MusicVideoOptions>(_options),
            _writer,
            [new MusicVideoOptionsValidator()],
            _context ??= _database.CreateContext(_time),
            new LibraryRoots(disk),
            disk,
            new WondarrPaths("/config"),
            new TestOptionsMonitor<ImportOptions>(new ImportOptions()),
            environment ?? new Hashtable(),
            NullLogger<MusicVideoSettingsService>.Instance);
    }

    private Task<MusicVideoSettingsResult> UpdateAsync(MusicVideoSettingsUpdate update) =>
        Service().UpdateAsync(update, CancellationToken.None);

    private Task<MusicVideoSettings> GetAsync() => Service().GetAsync(CancellationToken.None);

    /// <summary>Applies the written keys to the options, as reloading <c>config.yml</c> does.</summary>
    private sealed class ApplyingWriter(MusicVideoOptions options) : IConfigFileWriter
    {
        public List<IReadOnlyDictionary<string, object?>> Writes { get; } = [];

        public Task UpdateSectionAsync(
            string section,
            IReadOnlyDictionary<string, object?> values,
            CancellationToken cancellationToken)
        {
            section.Should().Be(MusicVideoSettingsService.Section);
            Writes.Add(values);

            foreach (var (key, value) in values)
            {
                switch (key)
                {
                    case "enabled": options.Enabled = (bool)value!; break;
                    case "root_path": options.RootPath = (string?)value; break;
                    case "layout": options.Layout = (string)value!; break;
                    case "naming_template": options.NamingTemplate = (string)value!; break;
                    case "beside_track_suffix": options.BesideTrackSuffix = (string)value!; break;
                    case "max_height": options.MaxHeight = (int?)value; break;
                    case "write_nfo": options.WriteNfo = (bool)value!; break;
                    case "write_thumbnail": options.WriteThumbnail = (bool)value!; break;
                    case "scheduled_search_limit": options.ScheduledSearchLimit = (int)value!; break;
                    default: throw new InvalidOperationException($"Unexpected key {key}");
                }
            }

            return Task.CompletedTask;
        }
    }
}
