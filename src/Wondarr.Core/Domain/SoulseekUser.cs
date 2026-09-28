using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// What we know about one Soulseek peer: how often it delivered, when it last failed, and whether the
/// user has ignored it. Stored in the <c>soulseek_user</c> table (MATCHING_ENGINE §6.3).
/// </summary>
public sealed class SoulseekUser : EntityBase
{
    /// <summary>Gets or sets the Soulseek username. Unique, case-insensitively.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets how many times this peer delivered a file that verified.</summary>
    public int Successes { get; set; }

    /// <summary>Gets or sets how many times this peer failed us (offline, stalled, a file that did not verify).</summary>
    public int Failures { get; set; }

    /// <summary>Gets or sets the UTC instants of the most recent failures, newest last, at most the last ten.</summary>
    public List<DateTime> RecentFailures { get; set; } = [];

    /// <summary>Gets or sets the UTC instant this peer last delivered a verified file, or <see langword="null"/>.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the user put this peer on the ignore list.</summary>
    public bool Ignored { get; set; }

    /// <summary>Gets or sets why the peer is ignored (the user's note, or "failed repeatedly").</summary>
    public string? IgnoredReason { get; set; }
}
