using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The query strategy (MATCHING_ENGINE.md §6.4): what gets searched for, in which order, and what is
/// left out because it would only repeat a query or flood the network with one word.
/// </summary>
public class SoulseekQueryBuilderTests
{
    [Fact]
    public void Builds_the_artist_title_folded_title_and_album_queries_in_that_order()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Get Lucky", album: "Random Access Memories"));

        queries.Should().Equal("daft punk get lucky", "get lucky", "daft punk random access memories");
    }

    [Fact]
    public void Folds_the_second_query_when_the_text_carries_diacritics()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Hoppípolla", artist: "Sigur Rós"));

        queries.Should().Equal("sigur rós hoppípolla", "sigur ros hoppipolla", "hoppípolla");
    }

    [Fact]
    public void Adds_a_word_for_each_version_flag()
    {
        var queries = SoulseekQueryBuilder.Build(
            Song("Get Lucky", flags: VersionFlags.Live | VersionFlags.Acoustic));

        queries.Should().Equal("daft punk get lucky live acoustic", "get lucky live acoustic");
    }

    [Fact]
    public void Leaves_out_the_title_only_query_when_the_title_is_too_short()
    {
        var queries = SoulseekQueryBuilder.Build(Song("One", artist: "U2"));

        queries.Should().Equal("u2 one");
    }

    [Fact]
    public void Leaves_out_the_album_query_for_the_singles_pseudo_album()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Get Lucky", album: "Singles"));

        queries.Should().Equal("daft punk get lucky", "get lucky");
    }

    [Fact]
    public void Leaves_out_the_album_query_when_the_album_is_the_title()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Get Lucky", album: "get lucky"));

        queries.Should().Equal("daft punk get lucky", "get lucky");
    }

    [Fact]
    public void Turns_special_characters_into_word_separators()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Back in Black", artist: "AC/DC"));

        queries.Should().Equal("ac dc back in black", "back in black");
    }

    [Fact]
    public void Drops_the_leading_dash_of_a_token()
    {
        var queries = SoulseekQueryBuilder.Build(Song("Get Lucky -ish"));

        queries.Should().Equal("daft punk get lucky ish", "get lucky ish");
    }

    [Fact]
    public void Falls_back_to_the_credit_line_without_its_feat_clause()
    {
        var queries = SoulseekQueryBuilder.Build(
            Song("Get Lucky", mainArtists: [], credit: "Daft Punk feat. Pharrell Williams"));

        queries.Should().Equal("daft punk get lucky", "get lucky");
    }

    [Fact]
    public void De_duplicates_queries_case_insensitively()
    {
        // "Get Lucky" folds to itself, and its title-only query is a different text: nothing repeats.
        var queries = SoulseekQueryBuilder.Build(Song("Get Lucky", artist: "daft punk"));

        queries.Should().OnlyHaveUniqueItems();
    }

    private static SongSearchRequest Song(
        string title,
        string artist = "Daft Punk",
        string? credit = null,
        string? album = null,
        VersionFlags flags = VersionFlags.None,
        IReadOnlyList<string>? mainArtists = null) =>
        new(1, title, credit ?? artist, mainArtists ?? [artist], album, null, flags);
}
