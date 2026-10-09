using FluentAssertions;
using Wondarr.Core.Songs;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// The texts a library title may contain to be considered for a similar track: they have to survive
/// the differences in punctuation and accents that the exact comparison later forgives.
/// </summary>
public sealed class SongTitleNeedleTests
{
    [Theory]
    [InlineData("Good Times", "Times|Good")]
    [InlineData("Don't Stop", "Stop")]
    [InlineData("Dont Stop", "Dont|Stop")]
    [InlineData("Don't", "Don")]
    [InlineData("Lose Yourself to Dance", "Yourself|Dance")]
    [InlineData("Édith", "dith")]
    [InlineData("ひとり", "ひとり")]
    [InlineData("  ", "")]
    public void The_needles_are_the_longest_ascii_runs_or_the_whole_title(string title, string expected) =>
        string.Join('|', SongDetailsService.TitleNeedles(title)).Should().Be(expected);
}
