using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// The opt-in live AcoustID lookup. It needs both <c>WONDARR_LIVE_TESTS=1</c> and a real
/// <c>WONDARR_ACOUSTID_KEY</c>, so CI never spends anyone's rate limit.
/// </summary>
public sealed class AcoustIdLiveTests
{
    [AcoustIdLiveFact]
    public async Task Looks_up_the_documented_example_fingerprint()
    {
        var options = new AcoustIdOptions
        {
            ClientKey = Environment.GetEnvironmentVariable("WONDARR_ACOUSTID_KEY"),
            BaseUrl = "https://api.acoustid.org/v2/",
        };

        var gate = new RequestSpacingGate(options.RequestInterval, TimeProvider.System);
        var http = new HttpClient(new RequestSpacingHandler(gate) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(15),
        };

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Wondarr/0.0.0 ( https://github.com/wulfftech/wondarr )");

        var client = new AcoustIdClient(
            http,
            new StaticOptionsMonitor<AcoustIdOptions>(options),
            TimeProvider.System,
            NullLogger<AcoustIdClient>.Instance);

        var fingerprint = File.ReadAllText(Path.Combine(AcoustIdFixtures.Directory, "docs-example.fingerprint.txt")).Trim();

        var result = await client.LookupAsync(fingerprint, durationSeconds: 641, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Ok);
        result.Results.Should().NotBeEmpty();
    }
}

/// <summary>A <see cref="FactAttribute"/> for the AcoustID live test: it also needs a client key.</summary>
public sealed class AcoustIdLiveFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="AcoustIdLiveFactAttribute"/> class.</summary>
    public AcoustIdLiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WONDARR_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Live provider tests run only with WONDARR_LIVE_TESTS=1.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WONDARR_ACOUSTID_KEY")))
        {
            Skip = "Set WONDARR_ACOUSTID_KEY to run the live AcoustID test.";
        }
    }
}