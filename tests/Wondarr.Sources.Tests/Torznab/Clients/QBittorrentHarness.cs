using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Sources.Torznab.Clients;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>
/// A qBittorrent proxy, client and type over one <see cref="QbittorrentFake"/>, with the settings a
/// client row stores.
/// </summary>
public sealed class QBittorrentHarness
{
    public const string InfoHash = "0123456789abcdef0123456789abcdef01234567";

    public static readonly string Magnet = "magnet:?xt=urn:btih:" + InfoHash + "&dn=Some+Release";

    /// <summary>The settings JSON a client row stores.</summary>
    public const string SettingsJson = """
        {"host":"127.0.0.1","port":8080,"username":"admin","password":"fixture-password-1234"}
        """;

    public QBittorrentHarness(QbittorrentFake? fake = null)
    {
        Fake = fake ?? new QbittorrentFake();
        ProxyLog = new RecordingLogger<QBittorrentProxy>();
        Proxy = new QBittorrentProxy(new StubHttpClientFactory(Fake), ProxyLog);
        Secrets = new SecretRegistry();
        Client = new QBittorrentClient(Proxy, Secrets, new RecordingLogger<QBittorrentClient>());
        Type = new QBittorrentClientType(Proxy);
    }

    public QbittorrentFake Fake { get; }

    public RecordingLogger<QBittorrentProxy> ProxyLog { get; }

    public QBittorrentProxy Proxy { get; }

    public SecretRegistry Secrets { get; }

    public QBittorrentClient Client { get; }

    public QBittorrentClientType Type { get; }

    /// <summary>The settings, parsed the way every call parses them.</summary>
    public static QBittorrentSettings Settings => QBittorrentSettings.FromJson(NotificationSecrets.Read(SettingsJson));

    /// <summary>The session a client row's calls use.</summary>
    public QBittorrentSession Session => Proxy.Session(QBittorrentProxy.SessionKey(Row().Id, SettingsJson));

    /// <summary>A client row with the standard settings.</summary>
    public static DownloadClient Row() => new()
    {
        Id = 7,
        Name = "qbit",
        Type = "qbittorrent",
        Settings = SettingsJson,
    };

    /// <summary>A client row whose settings JSON is given.</summary>
    public static DownloadClient Row(string settingsJson) => new()
    {
        Id = 7,
        Name = "qbit",
        Type = "qbittorrent",
        Settings = settingsJson,
    };
}
