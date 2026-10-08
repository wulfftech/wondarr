namespace Wondarr.Core.Sources;

/// <summary>How a source delivers a release: as a torrent or from usenet. Stored as its name.</summary>
public enum DownloadProtocol
{
    /// <summary>A torrent file or magnet link.</summary>
    Torrent,

    /// <summary>An nzb.</summary>
    Usenet,
}
