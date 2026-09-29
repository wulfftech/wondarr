using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// One real search against a real slskd, through the runner and its budget. It is skipped unless
/// the machine is set up for it: <c>WONDARR_LIVE_TESTS=1</c> plus <c>WONDARR_SLSKD_URL</c> and
/// <c>WONDARR_SLSKD_API_KEY</c> pointing at a slskd that is logged in. CI never runs it.
/// </summary>
public sealed class SlskdSearchLiveTests
{
    [SlskdLiveFact]
    public async Task Finds_at_least_one_peer_for_get_lucky()
    {
        using var provider = BuildProvider();

        var result = await provider.GetRequiredService<ISlskdSearchRunner>()
            .RunAsync("daft punk get lucky", CancellationToken.None);

        result.FinalState.Should().NotBe("Missing");
        result.Responses.Should().NotBeEmpty("a well-known track should be found on the network");
    }

    private static ServiceProvider BuildProvider()
    {
        var url = Environment.GetEnvironmentVariable("WONDARR_SLSKD_URL")!;
        var apiKey = Environment.GetEnvironmentVariable("WONDARR_SLSKD_API_KEY")!;

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<SoulseekOptions>();
        services.AddSingleton<IValidateOptions<SoulseekOptions>, SoulseekOptionsValidator>();
        services.AddSingleton<ISoulseekSearchBudget, SoulseekSearchBudget>();
        services.AddSingleton<ISlskdSearchRunner, SlskdSearchRunner>();
        services.AddScoped<ISlskdSearchApi>(provider => new SlskdSearchApi(
            new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(120) },
            provider.GetRequiredService<IOptionsMonitor<SoulseekOptions>>(),
            provider.GetRequiredService<SlskdSecretsStore>()));

        // The key comes from the environment rather than the settings table, so it is handed to the
        // store through the repository it reads: a live test must not write to a real config.
        services.AddSingleton<ISettingsRepository>(SecretsRepository(apiKey));
        services.AddScoped<SlskdSecretsStore>(provider => new SlskdSecretsStore(
            provider.GetRequiredService<ISettingsRepository>(),
            Substitute.For<ISecretRegistry>(),
            provider.GetRequiredService<IOptionsMonitor<SoulseekOptions>>()));

        return services.BuildServiceProvider();
    }

    private static ISettingsRepository SecretsRepository(string apiKey)
    {
        var repository = Substitute.For<ISettingsRepository>();
        repository
            .GetAsync<SlskdRuntimeSecrets>(SlskdSecretsStore.SettingKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SlskdRuntimeSecrets?>(new SlskdRuntimeSecrets(apiKey, "wondarr", apiKey)));
        return repository;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the one test that needs a running slskd. It is skipped unless
/// the live-test switch and both connection settings are present, so a bare <c>dotnet test</c> never
/// searches the network.
/// </summary>
public sealed class SlskdLiveFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="SlskdLiveFactAttribute"/> class.</summary>
    public SlskdLiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WONDARR_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Live provider tests run only with WONDARR_LIVE_TESTS=1.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WONDARR_SLSKD_URL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WONDARR_SLSKD_API_KEY")))
        {
            Skip = "Set WONDARR_SLSKD_URL and WONDARR_SLSKD_API_KEY to search a live slskd.";
        }
    }
}
