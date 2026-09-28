using System.Diagnostics.CodeAnalysis;

namespace Compilarr.Core.Metadata;

/// <summary>
/// Version hints a track can carry, on the title, the MusicBrainz recording disambiguation or the
/// release group's secondary types.
/// </summary>
/// <remarks>
/// The values are stored on <c>song.version_flags</c> as the wire names in <see cref="VersionFlagNames"/>.
/// The hard/soft split follows <c>docs/architecture/MATCHING_ENGINE.md</c> §6.2: a hard flag the song
/// or the candidate does not have is a version mismatch, <c>remaster</c> is soft (remasters share the
/// MusicBrainz recording) and <c>explicit</c>/<c>clean</c> only adjust the score.
/// </remarks>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the task spec; these are version hints, not a System.Flags collection type.")]
public enum VersionFlags
{
    None = 0,
    Live = 1 << 0,
    Remix = 1 << 1,
    Acoustic = 1 << 2,
    Instrumental = 1 << 3,
    Acapella = 1 << 4,
    RadioEdit = 1 << 5,
    Edit = 1 << 6,
    Extended = 1 << 7,
    Remaster = 1 << 8,
    Explicit = 1 << 9,
    Clean = 1 << 10,
    Cover = 1 << 11,
    Karaoke = 1 << 12,
    Demo = 1 << 13,
    Slowed = 1 << 14,
    EightD = 1 << 15,
    BassBoosted = 1 << 16,
}

/// <summary>Maps <see cref="VersionFlags"/> to and from the wire names stored in the database.</summary>
public static class VersionFlagNames
{
    /// <summary>
    /// Every flag except the soft ones (<see cref="VersionFlags.Remaster"/>, <see cref="VersionFlags.Explicit"/>,
    /// <see cref="VersionFlags.Clean"/>): the flags whose presence on one side and absence on the other is a
    /// version mismatch (MATCHING_ENGINE.md §6.2).
    /// </summary>
    public const VersionFlags HardFlags =
        VersionFlags.Live |
        VersionFlags.Remix |
        VersionFlags.Acoustic |
        VersionFlags.Instrumental |
        VersionFlags.Acapella |
        VersionFlags.RadioEdit |
        VersionFlags.Edit |
        VersionFlags.Extended |
        VersionFlags.Cover |
        VersionFlags.Karaoke |
        VersionFlags.Demo |
        VersionFlags.Slowed |
        VersionFlags.EightD |
        VersionFlags.BassBoosted;

    /// <summary>Wire names in enum order; the order defines the order of <see cref="ToWireNames"/>.</summary>
    private static readonly (VersionFlags Flag, string Wire)[] Entries =
    [
        (VersionFlags.Live, "live"),
        (VersionFlags.Remix, "remix"),
        (VersionFlags.Acoustic, "acoustic"),
        (VersionFlags.Instrumental, "instrumental"),
        (VersionFlags.Acapella, "acapella"),
        (VersionFlags.RadioEdit, "radio_edit"),
        (VersionFlags.Edit, "edit"),
        (VersionFlags.Extended, "extended"),
        (VersionFlags.Remaster, "remaster"),
        (VersionFlags.Explicit, "explicit"),
        (VersionFlags.Clean, "clean"),
        (VersionFlags.Cover, "cover"),
        (VersionFlags.Karaoke, "karaoke"),
        (VersionFlags.Demo, "demo"),
        (VersionFlags.Slowed, "slowed"),
        (VersionFlags.EightD, "8d"),
        (VersionFlags.BassBoosted, "bassboosted"),
    ];

    /// <summary>Returns the wire name of a single flag.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="single"/> is not exactly one known flag.</exception>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Parameter name fixed by the task spec; a single version flag, not a System.Single.")]
    public static string ToWireName(VersionFlags single)
    {
        foreach (var entry in Entries)
        {
            if (entry.Flag == single)
            {
                return entry.Wire;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(single), single, "Not a single known version flag.");
    }

    /// <summary>Parses one wire name (case-insensitive).</summary>
    public static bool TryParse(string wire, out VersionFlags flags)
    {
        flags = VersionFlags.None;

        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Wire, wire, StringComparison.OrdinalIgnoreCase))
            {
                flags = entry.Flag;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the wire names of the set flags, in enum order.</summary>
    public static IReadOnlyList<string> ToWireNames(VersionFlags flags)
    {
        var names = new List<string>();

        foreach (var entry in Entries)
        {
            if ((flags & entry.Flag) == entry.Flag)
            {
                names.Add(entry.Wire);
            }
        }

        return names;
    }
}
