using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Identity;

namespace Wondarr.Core.References;

/// <summary>
/// The identity behind one ranked <c>match_candidate</c>, stored as JSON in the row's <c>identity</c>
/// column (LIBRARY_OUTPUT §7.6). It carries just what the Match queue shows and what resolving the
/// candidate needs: which provider, the ids it is known by, and how it reads.
/// </summary>
/// <param name="Source">The provider the identity came from: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="MbRecordingId">The recording MBID, when known.</param>
/// <param name="DeezerId">The Deezer track id, when known.</param>
/// <param name="Title">The title, including any version suffix.</param>
/// <param name="ArtistCredit">The display credit, for example "Daft Punk feat. Pharrell Williams".</param>
/// <param name="DurationMs">The duration in milliseconds, when the provider knows it.</param>
/// <param name="AlbumTitle">The album the candidate would be filed under, when the provider names one.</param>
public sealed record MatchIdentity(
    string Source,
    string? MbRecordingId,
    long? DeezerId,
    string Title,
    string ArtistCredit,
    int? DurationMs,
    string? AlbumTitle)
{
    /// <summary>camelCase, nulls omitted: the shape <see cref="Domain.MatchCandidate.Identity"/> documents.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the identity off a resolved song.</summary>
    /// <param name="identity">The identity the resolver produced.</param>
    /// <returns>The match identity, with the first release option as the album.</returns>
    public static MatchIdentity From(SongIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new MatchIdentity(
            identity.Source,
            identity.MbRecordingId,
            identity.DeezerId,
            identity.Title,
            identity.ArtistCredit,
            identity.DurationMs,
            identity.ReleaseOptions.Count > 0 ? identity.ReleaseOptions[0].Title : null);
    }

    /// <summary>Reads the identity off a ranked search candidate.</summary>
    /// <param name="candidate">The candidate the resolver ranked.</param>
    /// <returns>The match identity.</returns>
    public static MatchIdentity From(SongCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new MatchIdentity(
            candidate.Source,
            candidate.MbRecordingId,
            candidate.DeezerId,
            candidate.Title,
            candidate.ArtistCredit,
            candidate.DurationMs,
            candidate.AlbumTitle);
    }

    /// <summary>The JSON stored in <c>match_candidate.identity</c>.</summary>
    /// <returns>The camelCase JSON, without null members.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, StoredJson);
}
