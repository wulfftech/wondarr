using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
        var logs = new RecordingLogger();

        var result = await Service(new SoulseekOptions { Username = "listener" }, writer: writer, logger: logs)
            .UpdateAsync(new SoulseekSettingsUpdate(Password: "s3cret"), CancellationToken.None);

        result.Success.Should().BeTrue();
        writer.Calls[0].Values.Should().ContainKey("password").WhoseValue.Should().Be("s3cret");

        logs.Messages.Should().NotBeEmpty("the write is logged");
        logs.Messages.Should().NotContain(message => message.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_variable_in_the_environment_locks_the_whole_field()
    {
        var environment = new Hashtable { ["APP__SOULSEEK__SHARED_FOLDERS__0"] = "/mnt/music" };
        var writer = new RecordingWriter();

        Service(new SoulseekOptions(), environment).Get().ReadOnlyFields.Should().Equal(["sharedFolders"]);

        var result = await Service(new SoulseekOptions(), environment, writer)
            .UpdateAsync(new SoulseekSettingsUpdate(SharedFolders: ["/other"]), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().Equal(
            "sharedFolders is set by the environment variable APP__SOULSEEK__SHARED_FOLDERS and cannot be changed here");
        writer.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("share_library", true)]
    [InlineData("shared_folders", true)]
    [InlineData("upload_slots", true)]
    [InlineData("distributed_network", true)]
    [InlineData("username", true)]
    [InlineData("password", true)]
    [InlineData("downloads_dir", true)]
    [InlineData("incomplete_dir", true)]
    [InlineData("listen_port", false)]
    [InlineData("upload_speed_limit_kib", false)]
    public async Task Restarts_slskd_only_for_the_settings_it_reads_at_start(string key, bool expected)
    {
        var writer = new RecordingWriter();

        var update = key switch
        {
            "share_library" => new SoulseekSettingsUpdate(ShareLibrary: false),
            "shared_folders" => new SoulseekSettingsUpdate(SharedFolders: ["/data/other"]),
            "upload_slots" => new SoulseekSettingsUpdate(UploadSlots: 3),
            "distributed_network" => new SoulseekSettingsUpdate(DistributedNetwork: false),
            "username" => new SoulseekSettingsUpdate(Username: "listener"),
            // A password is only valid with a username, so the account is set up in one change.
            "password" => new SoulseekSettingsUpdate(Username: "listener", Password: "s3cret"),
            "downloads_dir" => new SoulseekSettingsUpdate(DownloadsDir: "/data/other"),

            // slskd reloads the listen port and the upload speed limit in a running process.
            "listen_port" => new SoulseekSettingsUpdate(ListenPort: 50301),
            "upload_speed_limit_kib" => new SoulseekSettingsUpdate(UploadSpeedLimitKib: 100),
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

    private static SoulseekOptions External() => new()
    {
        Mode = SoulseekMode.External,
        External = new SoulseekExternalOptions { Url = "http://slskd:5030", ApiKey = "external-key-0123456789" },
    };

    [Fact]
    public void Get_in_external_mode_reports_the_connection_and_shows_slskds_own_fields_read_only()
    {
        var settings = Service(External()).Get();

        settings.Mode.Should().Be("external");
        settings.ExternalUrl.Should().Be("http://slskd:5030");
        settings.ExternalApiKeySet.Should().BeTrue();
        settings.ToString().Should().NotContain("external-key-0123456789");
        settings.ReadOnlyFields.Should().Contain(["username", "listenPort", "sharedFolders", "uploadSlots"])
            .And.NotContain("downloadsDir", "Wondarr's view of the download folder stays Wondarr's");
    }

    [Fact]
    public async Task Saving_in_external_mode_validates_with_the_stored_connection()
    {
        // The validation candidate once lacked the external section, so every save in external mode
        // failed "external url required".
        var writer = new RecordingWriter();

        var result = await Service(External(), writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(DownloadsDir: "/data/slskd-downloads"), CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        writer.Calls.Should().ContainSingle().Which.Values.Should().ContainKey("downloads_dir");
    }

    [Fact]
    public async Task Switching_to_external_writes_the_nested_keys_and_says_wondarr_restarts()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions(), writer: writer).UpdateAsync(
            new SoulseekSettingsUpdate(Mode: "external", ExternalUrl: "http://slskd:5030", ExternalApiKey: "external-key-0123456789"),
            CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.RestartsWondarr.Should().BeTrue();
        result.RestartsSlskd.Should().BeFalse();
        writer.Calls.Should().ContainSingle().Which.Values.Should()
            .Contain("mode", "external").And.Contain("external:url", "http://slskd:5030").And.ContainKey("external:api_key");
    }

    [Fact]
    public async Task Switching_to_external_without_a_url_is_refused()
    {
        var writer = new RecordingWriter();

        var result = await Service(new SoulseekOptions(), writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(Mode: "external"), CancellationToken.None);

        result.Success.Should().BeFalse();
        writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task In_external_mode_slskds_own_settings_are_changed_in_slskd()
    {
        var writer = new RecordingWriter();

        var result = await Service(External(), writer: writer)
            .UpdateAsync(new SoulseekSettingsUpdate(ListenPort: 52000), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("listenPort belongs to your own slskd");
        writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_api_key_clears_it_and_a_missing_one_keeps_it()
    {
        var writer = new RecordingWriter();
        var service = Service(new SoulseekOptions { External = new SoulseekExternalOptions { ApiKey = "external-key-0123456789" } }, writer: writer);

        (await service.UpdateAsync(new SoulseekSettingsUpdate(ExternalUrl: "http://slskd:5030"), CancellationToken.None)).Success.Should().BeTrue();
        writer.Calls.Single().Values.Should().NotContainKey("external:api_key");

        (await service.UpdateAsync(new SoulseekSettingsUpdate(ExternalApiKey: string.Empty), CancellationToken.None)).Success.Should().BeTrue();
        writer.Calls.Last().Values.Should().Contain("external:api_key", null);
    }

    private static SoulseekSettingsService Service(
        SoulseekOptions options,
        IDictionary? environment = null,
        IConfigFileWriter? writer = null,
        ILogger<SoulseekSettingsService>? logger = null) =>
        new(
            SlskdTestData.Monitor(options),
            writer ?? new RecordingWriter(),
            new SoulseekOptionsValidator(),
            environment ?? new Hashtable(),
            logger ?? NullLogger<SoulseekSettingsService>.Instance);

    /// <summary>Keeps every formatted log line so a test can prove a secret is not among them.</summary>
    private sealed class RecordingLogger : ILogger<SoulseekSettingsService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

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
