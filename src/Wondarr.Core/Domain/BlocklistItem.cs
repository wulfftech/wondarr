using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// A source-specific candidate that must not be grabbed again: the slskd user and path, the YouTube
/// video id, or the torrent infohash and file. Stored in the <c>blocklist</c> table. A row either
/// never expires (<see cref="ExpiresAt"/> is <see langword="null"/>) or expires at an instant.
/// </summary>
public sealed class BlocklistItem : EntityBase
{
    /// <summary>Gets or sets the song the rejection was for, or <see langword="null"/> when unknown.</summary>
    public long? SongId { get; set; }

    /// <summary>Gets or sets the source the key belongs to, for example <c>slskd</c>.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the source-specific key, for example the slskd user and path.</summary>
    public string BlocklistKey { get; set; } = string.Empty;

    /// <summary>Gets or sets why the candidate was blocked.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC instant the entry stops counting, or <see langword="null"/> when permanent.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Gets or sets the song the rejection was for, or <see langword="null"/>.</summary>
    public Song? Song { get; set; }
}
