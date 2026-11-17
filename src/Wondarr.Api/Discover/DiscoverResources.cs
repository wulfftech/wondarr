using Wondarr.Core.Discover;

namespace Wondarr.Api.Discover;

/// <summary>What Discover answers: ranked suggestions and which sources contributed.</summary>
/// <param name="Mode"><c>radio</c> (seeded by one song) or <c>mix</c> (seeded by a random sample of the library).</param>
/// <param name="MixSeed">For a mix, the sample seed; send it back as <c>mixSeed</c> to get the same sample. <see langword="null"/> for a radio.</param>
/// <param name="Seeds">The library songs the result was drawn from.</param>
/// <param name="Sources">Each source and whether it answered.</param>
/// <param name="Suggestions">The suggestions, best first.</param>
public sealed record DiscoverResource(
    string Mode,
    int? MixSeed,
    IReadOnlyList<DiscoverSeedResource> Seeds,
    IReadOnlyList<DiscoverSourceResource> Sources,
    IReadOnlyList<DiscoverSuggestionResource> Suggestions);

/// <summary>A library song a result was seeded from.</summary>
/// <param name="SongId">The song id.</param>
/// <param name="Title">The title.</param>
/// <param name="Artist">The main artist.</param>
public sealed record DiscoverSeedResource(long SongId, string Title, string Artist);

/// <summary>Whether one source contributed to a result.</summary>
/// <param name="Name"><c>listenbrainz</c>, <c>lastfm</c> or <c>deezer</c>.</param>
/// <param name="Status"><c>answered</c>, <c>skipped</c> (nothing to ask) or <c>failed</c>.</param>
public sealed record DiscoverSourceResource(string Name, string Status);

/// <summary>One suggested song. Nothing is resolved against MusicBrainz until the user adds it.</summary>
/// <param name="Title">The title.</param>
/// <param name="Artist">The artist credit.</param>
/// <param name="Album">The album, or <see langword="null"/>.</param>
/// <param name="CoverUrl">A cover image URL, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The recording MBID, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id, or <see langword="null"/>.</param>
/// <param name="DurationMs">The duration in milliseconds, or <see langword="null"/>.</param>
/// <param name="Sources">The sources that suggested it.</param>
/// <param name="Score">The summed, weighted rank score; higher is better.</param>
public sealed record DiscoverSuggestionResource(
    string Title,
    string Artist,
    string? Album,
    string? CoverUrl,
    string? MbRecordingId,
    string? Isrc,
    long? DeezerId,
    int? DurationMs,
    IReadOnlyList<string> Sources,
    double Score);

/// <summary>A suggestion the user turned down.</summary>
/// <param name="Id">The dismissal id; delete it to undo.</param>
/// <param name="MbRecordingId">The recording MBID, or <see langword="null"/> when the suggestion had none.</param>
/// <param name="ArtistKey">The normalised artist name.</param>
/// <param name="TitleKey">The normalised title.</param>
/// <param name="Created">When it was dismissed (UTC).</param>
public sealed record DiscoverDismissalResource(
    long Id,
    string? MbRecordingId,
    string ArtistKey,
    string TitleKey,
    DateTime Created);

/// <summary>What to dismiss.</summary>
/// <param name="MbRecordingId">The suggestion's recording MBID, or <see langword="null"/>.</param>
/// <param name="Artist">The suggestion's artist.</param>
/// <param name="Title">The suggestion's title.</param>
public sealed record DiscoverDismissalInputResource(string? MbRecordingId, string Artist, string Title);

/// <summary>Maps the Discover core types onto the resources above.</summary>
internal static class DiscoverResourceExtensions
{
    /// <summary>Maps a result.</summary>
    /// <param name="result">The core result.</param>
    public static DiscoverResource ToResource(this DiscoverResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new DiscoverResource(
            result.Mode,
            result.MixSeed,
            [.. result.Seeds.Select(seed => new DiscoverSeedResource(seed.SongId, seed.Title, seed.Artist))],
            [.. result.Sources.Select(source => new DiscoverSourceResource(source.Name, source.Status))],
            [
                .. result.Suggestions.Select(suggestion => new DiscoverSuggestionResource(
                    suggestion.Title,
                    suggestion.Artist,
                    suggestion.Album,
                    suggestion.CoverUrl,
                    suggestion.MbRecordingId,
                    suggestion.Isrc,
                    suggestion.DeezerId,
                    suggestion.DurationMs,
                    suggestion.Sources,
                    suggestion.Score)),
            ]);
    }

    /// <summary>Maps a dismissal.</summary>
    /// <param name="row">The stored row.</param>
    public static DiscoverDismissalResource ToResource(this DiscoverDismissal row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new DiscoverDismissalResource(row.Id, row.MbRecordingId, row.ArtistKey, row.TitleKey, row.CreatedAt);
    }
}
