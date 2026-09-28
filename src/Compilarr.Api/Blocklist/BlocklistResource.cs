using Compilarr.Core.Domain;

namespace Compilarr.Api.Blocklist;

/// <summary>
/// One blocked source candidate. <c>expiresAt</c> is <see langword="null"/> when the entry is
/// permanent; deleting the row is what lets the candidate be grabbed again.
/// </summary>
/// <param name="Id">The blocklist row.</param>
/// <param name="SongId">The song the rejection was for, or <see langword="null"/> when unknown.</param>
/// <param name="SourceType">The source the key belongs to, for example <c>slskd</c>.</param>
/// <param name="BlocklistKey">The source-specific key.</param>
/// <param name="Reason">Why the candidate was blocked.</param>
/// <param name="Date">When it was added: the row's <c>createdAt</c>.</param>
/// <param name="ExpiresAt">When it stops counting, or <see langword="null"/> when permanent.</param>
public sealed record BlocklistResource(
    long Id,
    long? SongId,
    string SourceType,
    string BlocklistKey,
    string Reason,
    DateTime Date,
    DateTime? ExpiresAt);

/// <summary>Maps the stored blocklist row to the shape the API returns.</summary>
public static class BlocklistResourceExtensions
{
    /// <summary>Maps an entry.</summary>
    /// <param name="item">The entry to map.</param>
    public static BlocklistResource ToResource(this BlocklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new BlocklistResource(
            item.Id,
            item.SongId,
            item.SourceType,
            item.BlocklistKey,
            item.Reason,
            item.CreatedAt,
            item.ExpiresAt);
    }
}
