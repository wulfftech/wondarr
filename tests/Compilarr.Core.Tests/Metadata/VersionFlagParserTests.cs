using System.Text.Json;
using Compilarr.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace Compilarr.Core.Tests.Metadata;

public class VersionFlagParserTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "version-flags.json");

    private static readonly JsonSerializerOptions FixtureOptions = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string, string?, string, string, string> Cases()
    {
        var data = new TheoryData<string, string?, string, string, string>();

        using var stream = File.OpenRead(FixturePath);
        var cases = JsonSerializer.Deserialize<List<FixtureCase>>(stream, FixtureOptions) ?? [];

        foreach (var testCase in cases)
        {
            data.Add(
                testCase.Title,
                testCase.Disambiguation,
                Join(testCase.SecondaryTypes),
                Join(testCase.Flags),
                testCase.BaseTitle);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reads_the_version_flags_of_a_title_and_strips_them_from_the_base_title(
        string title,
        string? disambiguation,
        string secondaryTypes,
        string expectedFlags,
        string expectedBaseTitle)
    {
        var info = VersionFlagParser.Parse(title, disambiguation, Split(secondaryTypes));

        info.Flags.Should().Be(ToFlags(expectedFlags));
        info.BaseTitle.Should().Be(expectedBaseTitle);
    }

    [Theory]
    [InlineData("live", VersionFlags.Live)]
    [InlineData("remix", VersionFlags.Remix)]
    [InlineData("acoustic", VersionFlags.Acoustic)]
    [InlineData("instrumental", VersionFlags.Instrumental)]
    [InlineData("acapella", VersionFlags.Acapella)]
    [InlineData("radio_edit", VersionFlags.RadioEdit)]
    [InlineData("edit", VersionFlags.Edit)]
    [InlineData("extended", VersionFlags.Extended)]
    [InlineData("remaster", VersionFlags.Remaster)]
    [InlineData("explicit", VersionFlags.Explicit)]
    [InlineData("clean", VersionFlags.Clean)]
    [InlineData("cover", VersionFlags.Cover)]
    [InlineData("karaoke", VersionFlags.Karaoke)]
    [InlineData("demo", VersionFlags.Demo)]
    [InlineData("slowed", VersionFlags.Slowed)]
    [InlineData("8d", VersionFlags.EightD)]
    [InlineData("bassboosted", VersionFlags.BassBoosted)]
    public void Round_trips_every_wire_name(string wire, VersionFlags flag)
    {
        VersionFlagNames.TryParse(wire, out var parsed).Should().BeTrue();

        parsed.Should().Be(flag);
        VersionFlagNames.ToWireName(flag).Should().Be(wire);
    }

    [Fact]
    public void Returns_the_wire_names_of_a_flag_set_in_enum_order()
    {
        VersionFlagNames.ToWireNames(VersionFlags.Live | VersionFlags.RadioEdit)
            .Should().Equal("live", "radio_edit");
    }

    [Fact]
    public void Rejects_an_unknown_wire_name()
    {
        VersionFlagNames.TryParse("nope", out var flags).Should().BeFalse();

        flags.Should().Be(VersionFlags.None);
    }

    [Fact]
    public void HardFlags_holds_every_flag_except_remaster_explicit_and_clean()
    {
        var soft = VersionFlags.Remaster | VersionFlags.Explicit | VersionFlags.Clean;

        VersionFlagNames.HardFlags.Should().Be(
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
            VersionFlags.BassBoosted);
        (VersionFlagNames.HardFlags & soft).Should().Be(VersionFlags.None);
    }

    private static VersionFlags ToFlags(string wireNames)
    {
        var flags = VersionFlags.None;

        foreach (var wire in wireNames.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            VersionFlagNames.TryParse(wire, out var flag).Should().BeTrue($"'{wire}' is a wire name");
            flags |= flag;
        }

        return flags;
    }

    private static string Join(IReadOnlyList<string>? values) =>
        values is null ? string.Empty : string.Join(',', values);

    private static string[]? Split(string values) =>
        values.Length == 0 ? null : values.Split(',');

    private sealed record FixtureCase(
        string Title,
        string? Disambiguation,
        IReadOnlyList<string>? SecondaryTypes,
        IReadOnlyList<string>? Flags,
        string BaseTitle);
}