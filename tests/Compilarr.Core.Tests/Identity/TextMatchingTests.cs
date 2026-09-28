using Compilarr.Core.Identity;
using FluentAssertions;
using Xunit;

namespace Compilarr.Core.Tests.Identity;

/// <summary>
/// Golden tests for the normalisation and similarity rules of MATCHING_ENGINE.md §6.1.
/// </summary>
public sealed class TextMatchingTests
{
    [Theory]
    [InlineData("Beyoncé & Jay-Z", "beyonce and jay z")]
    [InlineData("Harder, Better, Faster, Stronger", "harder better faster stronger")]
    [InlineData("  The   Beatles  ", "the beatles")]
    [InlineData("Don't Stop Me Now", "dont stop me now")]
    [InlineData("Don’t Stop Me Now", "dont stop me now")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("!!!", "")]
    public void Normalize_folds_case_accents_and_punctuation(string input, string expected) =>
        TextMatching.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_strips_diacritics()
    {
        TextMatching.Normalize("Sigur Rós").Should().Be("sigur ros");
        TextMatching.Normalize("Motörhead").Should().Be("motorhead");
    }

    [Theory]
    [InlineData("The Beatles", "beatles")]
    [InlineData("The The", "the")]
    [InlineData("Therapy?", "therapy")]
    [InlineData("Queen", "queen")]
    public void NormalizeArtist_drops_a_leading_article(string input, string expected) =>
        TextMatching.NormalizeArtist(input).Should().Be(expected);

    [Fact]
    public void Similarity_is_one_for_the_same_words_in_any_order() =>
        TextMatching.Similarity("Harder, Better, Faster, Stronger", "Harder Better Faster Stronger")
            .Should().Be(1.0);

    [Fact]
    public void Similarity_is_one_for_two_empty_strings() =>
        TextMatching.Similarity(string.Empty, string.Empty).Should().Be(1.0);

    [Fact]
    public void Similarity_is_zero_when_nothing_overlaps() =>
        TextMatching.Similarity("Get Lucky", string.Empty).Should().Be(0.0);

    [Fact]
    public void Similarity_stays_below_the_title_bar_for_a_longer_title() =>
        TextMatching.Similarity("Get Lucky", "Get Lucky Tonight").Should().BeLessThan(0.85);

    [Fact]
    public void Similarity_scores_a_one_word_difference_partially() =>
        TextMatching.Similarity("Bohemian Rhapsody", "Bohemian Rhapsody (Live Aid)")
            .Should().BeLessThan(0.85)
            .And.BeGreaterThan(0.5);
}
