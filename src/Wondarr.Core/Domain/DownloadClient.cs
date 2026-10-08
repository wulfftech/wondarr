using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Domain;

/// <summary>
/// One configured download client (ARCHITECTURE §5.7): where grabs are sent. Stored in the
/// <c>download_client</c> table. The settings are a JSON text column so a new client type needs no
/// migration.
/// </summary>
public sealed class DownloadClient : EntityBase
{
    /// <summary>Gets or sets the display name the user gave this client.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the client type's name, for example <c>qbittorrent</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets what this client downloads.</summary>
    public DownloadProtocol Protocol { get; set; }

    /// <summary>Gets or sets the client type's settings as a JSON object.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Gets or sets a value indicating whether grabs are sent to this client.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the pick order when several clients serve a protocol; a lower number is picked first.</summary>
    public int Priority { get; set; } = 1;
}
