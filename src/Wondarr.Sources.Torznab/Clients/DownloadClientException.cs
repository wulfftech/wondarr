namespace Wondarr.Sources.Torznab.Clients;

/// <summary>
/// A torrent client could not do what was asked of it: unreachable, wrong credentials, a version
/// Wondarr does not support, or a torrent the client does not have. The message is shown to the
/// user, so it never names a secret.
/// </summary>
public class DownloadClientException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="DownloadClientException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    public DownloadClientException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="DownloadClientException"/> class.</summary>
    /// <param name="message">The sentence to show the user.</param>
    /// <param name="inner">What went wrong underneath.</param>
    public DownloadClientException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The torrent the call was about is not in the client any more. <see cref="ITorrentClient.GetAsync"/>
/// turns this into <see langword="null"/>; every other call lets it through as a
/// <see cref="DownloadClientException"/> whose message is "not found".
/// </summary>
public sealed class TorrentNotFoundException : DownloadClientException
{
    /// <summary>Initialises a new instance of the <see cref="TorrentNotFoundException"/> class.</summary>
    public TorrentNotFoundException()
        : base("not found")
    {
    }
}
