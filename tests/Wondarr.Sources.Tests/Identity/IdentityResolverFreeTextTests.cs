using FluentAssertions;
using Wondarr.Core.Identity;
using Wondarr.Sources.Tests.Metadata;
using Xunit;

namespace Wondarr.Sources.Tests.Identity;

/// <summary>
/// Owner question 19: a free-text line resolves on its own only when the chosen hit's artist and title
/// closely match what was typed (<see cref="IdentityResolver.FreeTextFloor"/>). The search page is the
/// recorded shape of Deezer's answer for "hallelujah": Jeff Buckley, Leonard Cohen, Pentatonix.
/// </summary>
public sealed class IdentityResolverFreeTextTests
{
    private const long BuckleyId = 3001;
    private const long CohenId = 3002;

    [Fact]
    public async Task A_title_alone_never_resolves_and_offers_the_candidates()
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync("Hallelujah");

        result.Status.Should().Be(ResolveStatus.Unresolved);
        result.Candidates.Should().NotBeEmpty("the user can pick from what Deezer found");
        result.Candidates.Select(candidate => candidate.ArtistCredit).Should().Contain("Jeff Buckley");
    }

    [Fact]
    public async Task Two_words_that_are_both_the_title_are_still_no_artist()
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync("Hallelujah Hallelujah");

        result.Status.Should().Be(ResolveStatus.Unresolved);
    }

    [Theory]
    [InlineData("Jeff Buckley - Hallelujah")]
    [InlineData("Jeff Buckley Hallelujah")]
    [InlineData("hallelujah jeff buckley")]
    [InlineData("jeff   buckley   HALLELUJAH")]
    public async Task A_line_with_the_artist_and_the_title_resolves_to_that_artists_hit(string line)
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync(line);

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);
        result.Identity!.DeezerId.Should().Be(BuckleyId);
    }

    // Deezer returns Jeff Buckley first; the hit that is what was typed wins over the hit that merely came first.
    [Fact]
    public async Task The_hit_that_matches_the_typed_artist_is_chosen_over_an_earlier_one()
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync("Leonard Cohen Hallelujah");

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);
        result.Identity!.DeezerId.Should().Be(CohenId);
    }

    // Two typos in a 23-character "artist title": 1 - 2/23 = 0.91, above the 0.85 floor.
    [Theory]
    [InlineData("Jeff Bucley Halelujah")]
    [InlineData("Jeff Buckley Halelujah")]
    public async Task A_typo_within_the_floor_still_resolves(string line)
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync(line);

        result.Status.Should().Be(ResolveStatus.ResolvedDeezerOnly);
        result.Identity!.DeezerId.Should().Be(BuckleyId);
    }

    // A wrong artist: "bob dylan hallelujah" against "jeff buckley hallelujah" is about 0.5.
    [Theory]
    [InlineData("Bob Dylan Hallelujah")]
    [InlineData("Bob Dylan - Hallelujah")]
    public async Task A_wrong_artist_does_not_resolve(string line)
    {
        var harness = Harness();

        var result = await harness.Resolver.ResolveAsync(line);

        result.Status.Should().Be(ResolveStatus.Unresolved);
    }

    [Theory]
    [InlineData("Jeff Buckley Hallelujah", 1.0)]
    [InlineData("Jeff Bucley Halelujah", 0.91)]
    [InlineData("Hallelujah", 0.43)]
    [InlineData("Bob Dylan Hallelujah", 0.52)]
    public void The_floor_separates_the_examples(string typed, double expectedAround)
    {
        // The similarity the floor is compared against, for the record: the whole line against the hit's
        // "artist title", token-sorted.
        var similarity = TextMatching.Similarity(typed, "Jeff Buckley Hallelujah");

        similarity.Should().BeApproximately(expectedAround, 0.05);
        (similarity >= IdentityResolver.FreeTextFloor).Should().Be(expectedAround >= IdentityResolver.FreeTextFloor);
    }

    private static IdentityHarness Harness()
    {
        var harness = IdentityHarness.Create();
        harness.Deezer.Map("/search", DeezerFixtures.Read("search-hallelujah.json"), "hallelujah");
        harness.Deezer.Map("/search", DeezerFixtures.Read("search-hallelujah.json"), "halelujah");
        harness.Deezer.Map($"/track/{BuckleyId}", Track(BuckleyId, "Jeff Buckley", "Grace", "USSM19402001", 414));
        harness.Deezer.Map($"/track/{CohenId}", Track(CohenId, "Leonard Cohen", "Various Positions", "USSM18400002", 277));

        return harness;
    }

    private static string Track(long id, string artist, string album, string isrc, int seconds) =>
        $$$"""
        {"id":{{{id}}},"title":"Hallelujah","title_short":"Hallelujah","title_version":"","isrc":"{{{isrc}}}","duration":{{{seconds}}},
         "explicit_lyrics":false,"track_position":1,"disk_number":1,"release_date":"1994-08-23",
         "artist":{"id":{{{id + 2000}}},"name":"{{{artist}}}"},
         "contributors":[{"id":{{{id + 2000}}},"name":"{{{artist}}}","role":"Main"}],
         "album":{"id":{{{id + 3000}}},"title":"{{{album}}}"}}
        """;
}
