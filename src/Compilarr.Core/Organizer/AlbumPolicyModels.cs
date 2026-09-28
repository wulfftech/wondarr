using System.Text.Json;
using System.Text.Json.Serialization;
using Compilarr.Core.Domain;
using Compilarr.Core.Metadata;

namespace Compilarr.Core.Organizer;

/// <summary>
/// One release a song could be filed under, as produced by metadata resolution (MusicBrainz browse,
/// or a synthetic id the caller builds for a Deezer album).
/// </summary>
public sealed record ReleaseOption
{
    /// <summary>The opaque album identity: a MusicBrainz release id, or a caller-built synthetic id.</summary>
    public required string Key { get; init; }

    /// <summary>The MusicBrainz release id, when the option came from MusicBrainz.</summary>
    public string? MbReleaseId { get; init; }

    /// <summary>The MusicBrainz release-group id, when known.</summary>
    public string? MbReleaseGroupId { get; init; }

    /// <summary>The release title.</summary>
    public required string Title { get; init; }

    /// <summary>The release's album artist credit.</summary>
    public required string AlbumArtist { get; init; }

    /// <summary>True when the release is credited to Various Artists.</summary>
    public bool IsVariousArtists { get; init; }

    /// <summary>The MusicBrainz primary type name: <c>Album</c>, <c>Single</c>, <c>EP</c>, <c>Broadcast</c> or <c>Other</c>.</summary>
    public string? PrimaryType { get; init; }

    /// <summary>The MusicBrainz secondary type names, for example <c>Compilation</c> or <c>Live</c>.</summary>
    public IReadOnlyList<string> SecondaryTypes { get; init; } = [];

    /// <summary>The release status; only <c>Official</c> is eligible.</summary>
    public string? Status { get; init; } = "Official";

    /// <summary>The release date.</summary>
    public string? Date { get; init; }

    /// <summary>The first release date of the release group, when it differs from the release date.</summary>
    public string? ReleaseGroupFirstDate { get; init; }

    /// <summary>The track's number on the release.</summary>
    public int? TrackNo { get; init; }

    /// <summary>The disc the track sits on.</summary>
    public int? DiscNo { get; init; } = 1;

    /// <summary>The release's track count.</summary>
    public int? TotalTracks { get; init; }
}

/// <summary>A song waiting for an album context.</summary>
public sealed record SongToPlace
{
    /// <summary>The caller's song identifier; it is echoed back on the assignment.</summary>
    public required string Ref { get; init; }

    /// <summary>The owning artist's key.</summary>
    public required string ArtistKey { get; init; }

    /// <summary>The owning artist's display name.</summary>
    public required string ArtistName { get; init; }

    /// <summary>The recording's first release date.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>The version hints the song carries (MATCHING_ENGINE.md §6.2).</summary>
    public VersionFlags Flags { get; init; } = VersionFlags.None;

    /// <summary>The releases the song appears on, best first.</summary>
    public IReadOnlyList<ReleaseOption> Options { get; init; } = [];
}

/// <summary>An album the library already holds, as loaded by the caller (P1-07).</summary>
public sealed record ExistingAlbum
{
    /// <summary>The album's key, as stored.</summary>
    public required string AlbumKey { get; init; }

    /// <summary>The album context's shape.</summary>
    public AlbumContextKind Kind { get; init; }

    /// <summary>The key of the artist the album belongs to.</summary>
    public required string ArtistKey { get; init; }

    /// <summary>The album's title.</summary>
    public required string AlbumTitle { get; init; }

    /// <summary>The album's album artist.</summary>
    public required string AlbumArtist { get; init; }

    /// <summary>The album's date.</summary>
    public string? Date { get; init; }

    /// <summary>The MusicBrainz release id, when the album is a real release.</summary>
    public string? MbReleaseId { get; init; }

    /// <summary>The MusicBrainz release-group id, when known.</summary>
    public string? MbReleaseGroupId { get; init; }

    /// <summary>How many tracks the album already holds.</summary>
    public int TrackCount { get; init; }

    /// <summary>The highest track number in use, so new tracks continue after it.</summary>
    public int MaxTrackNo { get; init; }

    /// <summary>True when the album is credited to Various Artists.</summary>
    public bool IsVariousArtists { get; init; }
}

/// <summary>The input of one album-policy run.</summary>
public sealed record AlbumPolicyInput
{
    /// <summary>The library's album policy.</summary>
    public AlbumPolicy Policy { get; init; }

    /// <summary>How many owned tracks a real album must hold to be used at all.</summary>
    public int MinTracksPerRealAlbum { get; init; }

    /// <summary>The library's name; it titles a new <see cref="AlbumPolicy.Compilation"/> album.</summary>
    public required string LibraryName { get; init; }

    /// <summary>The songs to place, in the order the assignment must follow.</summary>
    public IReadOnlyList<SongToPlace> Songs { get; init; } = [];

    /// <summary>The albums the library already holds.</summary>
    public IReadOnlyList<ExistingAlbum> ExistingAlbums { get; init; } = [];
}

/// <summary>The album context chosen for one song.</summary>
public sealed record AlbumAssignment
{
    /// <summary>The song this assignment belongs to.</summary>
    public required string SongRef { get; init; }

    /// <summary>The album context's shape.</summary>
    public AlbumContextKind Kind { get; init; }

    /// <summary>The album title to file and tag with.</summary>
    public required string AlbumTitle { get; init; }

    /// <summary>The album artist to file and tag with.</summary>
    public required string AlbumArtist { get; init; }

    /// <summary>The album's key: a release id or a synthetic pseudo-album UUID.</summary>
    public required string AlbumKey { get; init; }

    /// <summary>The MusicBrainz release id, for a real album.</summary>
    public string? MbReleaseId { get; init; }

    /// <summary>The MusicBrainz release-group id, for a real album.</summary>
    public string? MbReleaseGroupId { get; init; }

    /// <summary>The track's number within the album.</summary>
    public int? TrackNo { get; init; }

    /// <summary>The disc the track sits on.</summary>
    public int? DiscNo { get; init; }

    /// <summary>The album's track total.</summary>
    public int? TotalTracks { get; init; }

    /// <summary>The album date; it is identical for every song of the album.</summary>
    public string? Date { get; init; }

    /// <summary>The song's own first release date.</summary>
    public string? OriginalDate { get; init; }

    /// <summary>True when the album is credited to Various Artists.</summary>
    public bool IsVariousArtists { get; init; }
}

/// <summary>Reads and writes <see cref="VersionFlags"/> as an array of wire names (<see cref="VersionFlagNames"/>).</summary>
public sealed class VersionFlagsJsonConverter : JsonConverter<VersionFlags>
{
    /// <inheritdoc />
    public override VersionFlags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return VersionFlags.None;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Version flags must be an array of wire names.");
        }

        var flags = VersionFlags.None;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return flags;
            }

            if (reader.TokenType != JsonTokenType.String ||
                !VersionFlagNames.TryParse(reader.GetString() ?? string.Empty, out var parsed))
            {
                throw new JsonException("Version flags must be an array of wire names.");
            }

            flags |= parsed;
        }

        throw new JsonException("Version flags must be an array of wire names.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VersionFlags value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();

        foreach (var name in VersionFlagNames.ToWireNames(value))
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }
}