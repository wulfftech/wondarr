using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Indexers;
using Wondarr.Core.Notifications;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// A stored secret is reused only for the target it was saved for (R-038), and the target is whatever
/// the type's fields flag <see cref="NotificationField.Target"/>. A new type that stores a secret but
/// forgets to flag its address would send that secret anywhere, so this walks every registered type.
/// </summary>
public sealed class SecretTargetGuardTests : IDisposable
{
    /// <summary>Import lists that read a fixed public API: the key goes to that host whatever the settings say.</summary>
    private static readonly string[] FixedHostLists = ["lastfmLoved", "lastfmTop"];

    private readonly WondarrAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Every_registered_type_with_a_secret_flags_its_address_as_the_target()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var kinds = new List<(string Kind, string Name, IReadOnlyList<NotificationField> Fields)>();
        kinds.AddRange(services.GetServices<INotificationProvider>().Select(p => ("notification", p.Implementation, p.Fields)));
        kinds.AddRange(services.GetServices<IIndexerType>().Select(t => ("indexer", t.Type, t.Fields)));
        kinds.AddRange(services.GetServices<IDownloadClientType>().Select(t => ("download client", t.Type, t.Fields)));
        kinds.AddRange(services.GetServices<IImportListProvider>().Select(p => ("import list", p.Type, p.Fields)));

        kinds.Should().NotBeEmpty();
        kinds.Select(kind => kind.Kind).Distinct().Should().HaveCount(4, "every kind must be walked");

        var unguarded = kinds
            .Where(kind => kind.Fields.Any(field => field.Secret))

            // A secret that is itself the address (Discord's webhook URL) has no target to compare.
            .Where(kind => !kind.Fields.Any(field => field.Secret && field.Type == "url"))
            .Where(kind => !(kind.Kind == "import list" && FixedHostLists.Contains(kind.Name)))
            .Where(kind => !kind.Fields.Any(field => field.Target && !field.Secret))
            .Select(kind => $"{kind.Kind} '{kind.Name}'")
            .ToList();

        unguarded.Should().BeEmpty("a type that stores a secret needs a field flagged Target, or its secret can be sent to any host");
    }
}
