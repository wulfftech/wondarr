using System.Text.RegularExpressions;
using Compilarr.Core.Logging;
using Compilarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Compilarr.Sources.Tests.Slskd;

public class SlskdSecretsStoreTests
{
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    [Fact]
    public async Task Generates_the_secrets_once_and_reuses_them_in_a_later_scope()
    {
        var repository = new InMemorySettingsRepository();

        var first = await Store(repository, out _).GetOrCreateAsync(CancellationToken.None);

        // A new scope means a new store over the same settings table.
        var second = await Store(repository, out _).GetOrCreateAsync(CancellationToken.None);

        first.Should().Be(second);
        first.ApiKey.Should().MatchRegex(Hex64);
        first.WebPassword.Should().MatchRegex(Hex64);
        first.WebUsername.Should().Be(SlskdSecretsStore.WebUsername);
    }

    [Fact]
    public async Task Registers_the_api_key_the_web_password_and_the_soulseek_password()
    {
        var options = new SoulseekOptions { Username = "compilarr-soulseek", Password = "hunter2-not-a-real-password" };

        var secrets = await Store(new InMemorySettingsRepository(), out var registry, options)
            .GetOrCreateAsync(CancellationToken.None);

        registry.Received(1).Register(secrets.ApiKey);
        registry.Received(1).Register(secrets.WebPassword);
        registry.Received(1).Register("hunter2-not-a-real-password");
    }

    private static SlskdSecretsStore Store(
        InMemorySettingsRepository repository,
        out ISecretRegistry registry,
        SoulseekOptions? options = null)
    {
        registry = Substitute.For<ISecretRegistry>();
        return new SlskdSecretsStore(repository, registry, SlskdTestData.Monitor(options ?? new SoulseekOptions()));
    }
}
