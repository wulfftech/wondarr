using Wondarr.Core.DownloadClients;
using Wondarr.Core.Messaging;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>Lets go of a deleted download client's qBittorrent login session (R-081).</summary>
public sealed class SessionEvictor : IHandle<DownloadClientDeletedEvent>
{
    private readonly QBittorrentProxy _proxy;

    /// <summary>Initialises a new instance of the <see cref="SessionEvictor"/> class.</summary>
    /// <param name="proxy">The proxy that holds the sessions.</param>
    public SessionEvictor(QBittorrentProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        _proxy = proxy;
    }

    /// <inheritdoc />
    public Task HandleAsync(DownloadClientDeletedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        _proxy.Evict(message.ClientId);

        return Task.CompletedTask;
    }
}
