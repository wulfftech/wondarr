using Compilarr.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace Compilarr.Core.Tests.Metadata;

public sealed class TextFoldingTests
{
    [Theory]
    [InlineData("Björk", "Bjork")]
    [InlineData("Sigur Rós - Hoppípolla", "Sigur Ros - Hoppipolla")]
    [InlineData("Beyoncé", "Beyonce")]
    [InlineData("Tití Me Preguntó", "Titi Me Pregunto")]
    [InlineData("Straße", "Strasse")]
    [InlineData("Mötley Crüe", "Motley Crue")]
    [InlineData("Łódź", "Lodz")]
    [InlineData("Æther Œuvre", "AEther OEuvre")]
    public void Folds_accented_latin_letters_to_their_base_letters(string input, string expected) =>
        TextFolding.RemoveDiacritics(input).Should().Be(expected);

    [Theory]
    [InlineData("AC/DC")]
    [InlineData("")]
    [InlineData("坂本龍一")]
    [InlineData("Мумий Тролль")]
    public void Leaves_text_without_latin_accents_unchanged(string input) =>
        TextFolding.RemoveDiacritics(input).Should().BeSameAs(input);
}
