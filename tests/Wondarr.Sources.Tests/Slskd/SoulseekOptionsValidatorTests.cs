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
}
