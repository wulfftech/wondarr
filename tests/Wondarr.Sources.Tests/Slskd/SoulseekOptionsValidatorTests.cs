using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SoulseekOptionsValidatorTests
{
    private const string Password = "hunter2-not-a-real-password";

    private readonly SoulseekOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_defaults()
    {
        _validator.Validate(null, new SoulseekOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_privileged_listen_port_with_the_yaml_key_as_prefix()
    {
        var result = _validator.Validate(null, new SoulseekOptions { ListenPort = 80 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.listen_port:");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Rejects_an_upload_slot_count_outside_one_to_a_hundred(int slots)
    {
        var result = _validator.Validate(null, new SoulseekOptions { UploadSlots = slots });

        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.upload_slots:");
    }

    [Fact]
    public void Rejects_a_negative_upload_speed_limit_but_allows_zero()
    {
        _validator.Validate(null, new SoulseekOptions { UploadSpeedLimitKib = -1 })
            .Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.upload_speed_limit_kib:");
        _validator.Validate(null, new SoulseekOptions { UploadSpeedLimitKib = 0 }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Never_echoes_the_password_in_a_failure_message()
    {
        var result = _validator.Validate(null, new SoulseekOptions
        {
            ListenPort = 80,
            Username = null,
            Password = Password,
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().NotContain(message => message.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_the_default_search_budget()
    {
        _validator.Validate(null, new SoulseekOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_search_budget_above_what_soulseek_allows()
    {
        var result = _validator.Validate(null, new SoulseekOptions
        {
            Search = new SoulseekSearchOptions { MaxSearches = 31 },
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.search.max_searches:");
    }

    [Fact]
    public void Rejects_a_submission_spacing_below_what_soulseek_allows()
    {
        var result = _validator.Validate(null, new SoulseekOptions
        {
            Search = new SoulseekSearchOptions { MinSpacingSeconds = 4 },
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.search.min_spacing_seconds:");
    }

    [Fact]
    public void Rejects_every_search_knob_outside_its_range()
    {
        var result = _validator.Validate(null, new SoulseekOptions
        {
            Search = new SoulseekSearchOptions
            {
                MaxSearches = 0,
                WindowSeconds = 239,
                MaxOutstanding = 3,
                MinSpacingSeconds = 0,
                SearchTimeoutMs = 4999,
                ResponseLimit = 501,
                FileLimit = 0,
                WallClockSeconds = 121,
                PollIntervalMs = 99,
            },
        });

        result.Failures.Should().HaveCount(9);
        result.Failures.Should().OnlyContain(message => message.StartsWith("soulseek.search.", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_password_without_a_username()
    {
        var result = _validator.Validate(null, new SoulseekOptions { Username = " ", Password = Password });

        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.username:");
    }

    [Fact]
    public void External_mode_requires_a_url_and_a_key()
    {
        var result = _validator.Validate(null, new SoulseekOptions { Mode = SoulseekMode.External });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(
            message => message.StartsWith("soulseek.external.url:", StringComparison.Ordinal));
        result.Failures.Should().Contain(
            message => message.StartsWith("soulseek.external.api_key:", StringComparison.Ordinal));
    }

    [Fact]
    public void External_mode_accepts_a_full_url_and_key()
    {
        var result = _validator.Validate(null, External());

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("slskd.example:5030")]          // not absolute
    [InlineData("ftp://slskd.example:5030")]    // not http(s)
    [InlineData("http://user:pass@slskd")]      // user info
    [InlineData("http://slskd?x=1")]            // query
    public void External_mode_rejects_a_bad_url(string url)
    {
        var options = External();
        options.External.Url = url;

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.external.url:");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456")]
    public void External_mode_rejects_a_key_outside_its_length_range(string key)
    {
        var options = External();
        options.External.ApiKey = key;

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.external.api_key:");
    }

    [Fact]
    public void Bundled_mode_ignores_the_external_section()
    {
        var result = _validator.Validate(null, new SoulseekOptions
        {
            Mode = SoulseekMode.Bundled,
            External = new SoulseekExternalOptions { Url = "not a url", ApiKey = "short" },
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void External_failures_never_echo_the_key()
    {
        var options = External();
        options.External.Url = null;

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().NotContain(
            message => message.Contains(options.External.ApiKey!, StringComparison.Ordinal));
    }

    private static SoulseekOptions External() => new()
    {
        Mode = SoulseekMode.External,
        External = new SoulseekExternalOptions
        {
            Url = "http://slskd.example:5030",
            ApiKey = new string('k', 32),
        },
    };
}
