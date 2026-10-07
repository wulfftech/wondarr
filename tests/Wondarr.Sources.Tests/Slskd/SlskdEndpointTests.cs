using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>The endpoint: where every request goes and which key it carries, per mode.</summary>
public class SlskdEndpointTests
{
    private const string ExternalKey = "external-key-0123456789abcdef";

    [Fact]
    public async Task Bundled_mode_resolves_loopback_and_the_generated_key()
    {
        var endpoint = Endpoint(SlskdTestData.Monitor(new SoulseekOptions()));

        var (baseAddress, apiKey) = await endpoint.ResolveAsync(CancellationToken.None);

        baseAddress.ToString().Should().Be("http://127.0.0.1:5030/");
        apiKey.Should().Be(SlskdTestData.Secrets.ApiKey);
    }

    [Fact]
    public async Task Bundled_mode_uses_the_configured_web_port()
    {
        var endpoint = Endpoint(SlskdTestData.Monitor(new SoulseekOptions { WebPort = 5031 }));

        var (baseAddress, _) = await endpoint.ResolveAsync(CancellationToken.None);

        baseAddress.ToString().Should().Be("http://127.0.0.1:5031/");
    }

    [Fact]
    public async Task External_mode_resolves_the_configured_url_and_key()
    {
        var endpoint = Endpoint(SlskdTestData.Monitor(ExternalOptions()));

        var (baseAddress, apiKey) = await endpoint.ResolveAsync(CancellationToken.None);

        baseAddress.ToString().Should().Be("http://slskd.example:5030/");
        apiKey.Should().Be(ExternalKey);
    }

    [Fact]
    public async Task External_mode_adds_the_trailing_slash()
    {
        var options = ExternalOptions();
        options.External.Url = "http://slskd.example:5030";

        var endpoint = Endpoint(SlskdTestData.Monitor(options));

        var (baseAddress, _) = await endpoint.ResolveAsync(CancellationToken.None);

        baseAddress.ToString().Should().Be("http://slskd.example:5030/");
    }

    [Fact]
    public async Task A_settings_change_applies_to_the_next_call()
    {
        var monitor = new FakeOptionsMonitor<SoulseekOptions>(new SoulseekOptions());
        var endpoint = Endpoint(monitor);

        (await endpoint.ResolveAsync(CancellationToken.None)).BaseAddress.ToString()
            .Should().Be("http://127.0.0.1:5030/");

        monitor.Set(ExternalOptions());

        var (baseAddress, apiKey) = await endpoint.ResolveAsync(CancellationToken.None);

        baseAddress.ToString().Should().Be("http://slskd.example:5030/");
        apiKey.Should().Be(ExternalKey);
    }

    [Fact]
    public async Task External_mode_registers_the_key_and_web_password_as_secrets()
    {
        var registry = Substitute.For<ISecretRegistry>();
        var options = ExternalOptions();
        options.External.WebPassword = "web-password-0123456789";

        var endpoint = Endpoint(SlskdTestData.Monitor(options), registry);

        await endpoint.ResolveAsync(CancellationToken.None);

        registry.Received(1).Register(ExternalKey);
        registry.Received(1).Register("web-password-0123456789");
    }

    private static SoulseekOptions ExternalOptions() => new()
    {
        Mode = SoulseekMode.External,
        External = new SoulseekExternalOptions
        {
            Url = "http://slskd.example:5030",
            ApiKey = ExternalKey,
        },
    };

    private static SlskdEndpoint Endpoint(
        IOptionsMonitor<SoulseekOptions> monitor,
        ISecretRegistry? registry = null)
    {
        registry ??= Substitute.For<ISecretRegistry>();

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsRepository>(SlskdTestData.RepositoryWithRuntimeSecrets());
        services.AddSingleton(registry);
        services.AddSingleton(monitor);
        services.AddScoped<SlskdSecretsStore>();

        return new SlskdEndpoint(monitor, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), registry);
    }
}