using global::FakeSlskd; // qualified: this test namespace also ends in "FakeSlskd"
using FluentAssertions;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.FakeSlskd;

/// <summary>
/// The fake yt-dlp the Phase 4 gate runs: its version line, its formats probe, and above all that
/// every failure it can be told to produce is classified by the app's own taxonomy as intended.
/// </summary>
public sealed class FakeYtDlpTests
{
    [Fact]
    public async Task Answers_the_version_probe()
    {
        var (code, output, _) = await RunAsync("--version");

        code.Should().Be(0);
        output.Trim().Should().Be("2026.08.19");
    }

    [Fact]
    public async Task Answers_the_formats_probe_with_format_rows()
    {
        var (code, output, _) = await RunAsync("-F", "--", "https://music.youtube.com/watch?v=gateATV0000");

        code.Should().Be(0);
        output.Should().Contain("251 opus audio only");
    }

    [Theory]
    [InlineData("bot-check", YtDlpErrorKind.BotCheck)]
    [InlineData("rate-limited", YtDlpErrorKind.RateLimited)]
    [InlineData("geo", YtDlpErrorKind.GeoRestricted)]
    [InlineData("age-gated", YtDlpErrorKind.AgeGated)]
    [InlineData("private", YtDlpErrorKind.PrivateOrUnavailable)]
    [InlineData("unavailable", YtDlpErrorKind.PrivateOrUnavailable)]
    public async Task Each_failure_is_classified_by_the_apps_taxonomy(string kind, YtDlpErrorKind expected)
    {
        var scenario = Path.Combine(Path.GetTempPath(), $"fake-ytdlp-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scenario, $$"""{ "videos": { "vid00000001": { "kind": "{{kind}}" } } }""");
        Environment.SetEnvironmentVariable("FAKE_YT_SCENARIO", scenario);

        try
        {
            var (code, _, errors) = await RunAsync("-o", "/tmp/x/%(id)s.%(ext)s", "--", "https://music.youtube.com/watch?v=vid00000001");

            code.Should().Be(1);
            YtDlpErrorTaxonomy.Classify(errors).Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_YT_SCENARIO", null);
            File.Delete(scenario);
        }
    }

    private static async Task<(int Code, string Output, string Errors)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await FakeYtDlp.RunAsync(args, output, errors);

        return (code, output.ToString(), errors.ToString());
    }
}
