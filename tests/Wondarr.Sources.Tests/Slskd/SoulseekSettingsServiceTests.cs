using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Configuration;
using Wondarr.Sources.Slskd;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The settings page may change the Soulseek account, sharing and transfers — and nothing the
/// environment owns, and never the password back to the caller.
/// </summary>
public sealed class SoulseekSettingsServiceTests
{
    [Fact]
    public void Get_never_returns_the_password()
    {
        var options = new SoulseekOptions { Username = "listener", Password = "hunter2" };

        var settings = Service(options).Get();

        settings.Username.Should().Be("listener");
        settings.PasswordSet.Should().BeTrue();

        // The record has no password member at all: everything it can carry is checked here.
        settings.Should().BeOfType<SoulseekSettings>().Which.ToString().Should().NotContain("hunter2");
    }

    [Fact]
    public void Get_reports_no_password_when_none_is_stored()
    {
        Service(new SoulseekOptions { Username = "listener" }).Get().PasswordSet.Should().BeFalse();
    }

    [Fact]
    public void Get_reports_the_fields_the_environment_owns()
    {
        var environment = new Hashtable { ["APP__SOULSEEK__LISTEN_PORT"] = "51000" };

        var settings = Service(new SoulseekOptions(), environment).Get();

        settings.ReadOnlyFields.Should().Equal(["listenPort"]);
    }

    [Fact]
    public async Task Changing_a_field_the_environment_owns_is_refused()
    {
        var environment = new Hashtable { ["APP__SOULSEEK__LISTEN_PORT"] = "51000" };
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions { ListenPort = 51000 }, environment, writer)
            .UpdateAsync(new SoulseekSettingsUpdate(ListenPort: 52000), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().Equal(
            "listenPort is set by the environment variable APP__SOULSEEK__LISTEN_PORT and cannot be changed here");
        writer.Calls.Should().BeEmpty("nothing may be written for a refused change");
    }

    [Fact]
    public async Task Re_posting_an_unchanged_environment_field_is_ignored()
    {
        var environment = new Hashtable { ["APP__SOULSEEK__LISTEN_PORT"] = "51000" };
        var writer = new RecordingWriter();

        // The settings page posts the whole form back, read-only fields included.
        var result = await Service(new SoulseekOptions { ListenPort = 51000 }, environment, writer)
            .UpdateAsync(new SoulseekSettingsUpdate(ListenPort: 51000, UploadSlots: 4), CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls.Should().ContainSingle()
            .Which.Values.Should().ContainKey("upload_slots")
            .WhoseValue.Should().Be(4);
        writer.Calls[0].Values.Should().NotContainKey("listen_port");
    }

    [Fact]
    public async Task An_invalid_change_is_refused_with_the_validators_messages()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions(), writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(ListenPort: 80), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().StartWith("soulseek.listen_port:");
        writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Turning_share_my_library_off_writes_only_that_key()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions { ShareLibrary = true }, writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(ShareLibrary: false), CancellationToken.None);

        result.Success.Should().BeTrue();

        var call = writer.Calls.Should().ContainSingle().Subject;
        call.Section.Should().Be("soulseek");
        call.Values.Should().ContainKey("share_library").WhoseValue.Should().Be(false);
        call.Values.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_null_password_keeps_the_stored_one()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions { Username = "listener", Password = "hunter2" }, writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(Username: "listener-2"), CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls[0].Values.Should().NotContainKey("password");
    }

    [Fact]
    public async Task An_empty_password_clears_the_stored_one()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions { Username = "listener", Password = "hunter2" }, writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(Password: string.Empty), CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls[0].Values.Should().ContainKey("password").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task A_new_password_is_written_but_never_logged()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions { Username = "listener" }, writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(Password: "s3cret"), CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls[0].Values.Should().ContainKey("password").WhoseValue.Should().Be("s3cret");
    }

    [Theory]
    [InlineData("share_library", true)]
    [InlineData("listen_port", true)]
    [InlineData("shared_folders", true)]
    [InlineData("upload_slots", true)]
    [InlineData("distributed_network", true)]
    [InlineData("upload_speed_limit_kib", false)]
    [InlineData("downloads_dir", false)]
    [InlineData("incomplete_dir", false)]
    public async Task Restarts_slskd_only_for_the_settings_it_reads_at_start(string key, bool expected)
    {
        var writer = new RecordingWriter();

        var update = key switch
        {
            "share_library" => new SoulseekSettingsUpdate(ShareLibrary: false),
            "listen_port" => new SoulseekSettingsUpdate(ListenPort: 50301),
            "shared_folders" => new SoulseekSettingsUpdate(SharedFolders: []),
            "upload_slots" => new SoulseekSettingsUpdate(UploadSlots: 3),
            "distributed_network" => new SoulseekSettingsUpdate(DistributedNetwork: false),
            "upload_speed_limit_kib" => new SoulseekSettingsUpdate(UploadSpeedLimitKib: 100),
            "downloads_dir" => new SoulseekSettingsUpdate(DownloadsDir: "/data/other"),
            _ => new SoulseekSettingsUpdate(IncompleteDir: "/data/other/incomplete"),
        };

        var result = await Service(new SoulseekOptions(), writer: writer)
            .UpdateAsync(update, CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls[0].Values.Should().ContainKey(key);
        result.RestartsSlskd.Should().Be(expected);
    }

    [Fact]
    public async Task An_empty_update_writes_nothing()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions(), writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RestartsSlskd.Should().BeFalse();
        writer.Calls.Should().BeEmpty();
    }

    private static SoulseekSettingsService Service(
        SoulseekOptions options,
        IDictionary? environment = null,
        IConfigFileWriter? writer = null) =>
        new(
            SlskdTestData.Monitor(options),
            writer ?? new RecordingWriter(),
            new SoulseekOptionsValidator(),
            environment ?? new Hashtable(),
            NullLogger<SoulseekSettingsService>.Instance);

    /// <summary>Records what would have been written to <c>config.yml</c>.</summary>
    private sealed class RecordingWriter : IConfigFileWriter
    {
        public List<Call> Calls { get; } = [];

        public Task UpdateSectionAsync(
            string section,
            IReadOnlyDictionary<string, object?> values,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(section, values));

            return Task.CompletedTask;
        }

        internal sealed record Call(string Section, IReadOnlyDictionary<string, object?> Values);
    }
}