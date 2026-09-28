using FluentAssertions;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class NamingTemplateTests
{
    /// <summary>The characters a song title may contain but a file name may not.</summary>
    private const string HostileCharacters = "abc .:/\\?*\"<>|";

    [Fact]
    public void Every_preset_renders()
    {
        NamingTemplate.PresetTemplates.Should().HaveCount(4);

        foreach (var (layout, template) in NamingTemplate.PresetTemplates)
        {
            var result = NamingTemplate.Validate(template);

            result.IsValid.Should().BeTrue($"preset {layout} must validate: {string.Join("; ", result.Errors)}");

            var path = NamingTemplate.Render(template, SampleValues());
            path.Should().NotBeNullOrWhiteSpace($"preset {layout}");
        }
    }

    [Fact]
    public void Never_renders_an_empty_dotted_or_space_padded_segment()
    {
        var random = new Random(20260929);

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var values = SampleValues() with
            {
                ArtistName = Hostile(random),
                AlbumArtistName = Hostile(random),
                AlbumTitle = Hostile(random),
                TrackTitle = Hostile(random),
                TrackNo = random.Next(1, 30),
                DiscNo = random.Next(1, 4),
                DiscCount = random.Next(1, 4),
            };

            foreach (var (layout, template) in NamingTemplate.PresetTemplates)
            {
                var path = NamingTemplate.Render(template, values);

                foreach (var segment in path.Split('/'))
                {
                    segment.Should().NotBeEmpty($"iteration {iteration}, preset {layout}");
                    segment.Should().NotBe(".").And.NotBe("..", $"iteration {iteration}, preset {layout}");
                    segment.Should().NotStartWith(".", $"iteration {iteration}, preset {layout}");
                    segment.Should().NotEndWith(".").And.NotEndWith(" ", $"iteration {iteration}, preset {layout}");
                }
            }
        }
    }

    [Fact]
    public void Never_adds_a_path_level_for_a_slash_in_a_value()
    {
        var values = SampleValues() with
        {
            AlbumArtistName = @"A\B",
            AlbumTitle = "One/Two",
            TrackTitle = "Three/Four",
        };

        var path = NamingTemplate.Render("{Album Artist Name}/{Album Title}/{track:00} - {Track Title}", values);

        path.Should().Be("A+B/One+Two/08 - Three+Four");
        path.Split('/').Should().HaveCount(3);
    }

    [Fact]
    public void Optional_groups_vanish_with_an_empty_token_and_literal_brackets_survive()
    {
        var values = SampleValues() with { Version = null };

        NamingTemplate.Render("{Track Title}[ ({Version})] - {Source}", values)
            .Should().Be("Get Lucky - Soulseek");

        NamingTemplate.Render("{Track Title} [[{Source}]]", values)
            .Should().Be("Get Lucky [Soulseek]");
    }

    /// <summary>A random string of the characters a hostile title can carry, always with one letter in it.</summary>
    private static string Hostile(Random random)
    {
        var length = random.Next(0, 12);
        var characters = new char[length];
        var hasLetter = false;

        for (var index = 0; index < length; index++)
        {
            characters[index] = HostileCharacters[random.Next(HostileCharacters.Length)];
            hasLetter |= char.IsLetterOrDigit(characters[index]);
        }

        var value = new string(characters);

        return hasLetter ? value : "a" + value;
    }

    internal static NamingValues SampleValues() => new()
    {
        ArtistName = "Daft Punk",
        AlbumArtistName = "Daft Punk",
        AlbumTitle = "Random Access Memories",
        AlbumType = "Album",
        ReleaseYear = 2013,
        OriginalYear = 2013,
        TrackTitle = "Get Lucky",
        TrackArtistName = "Daft Punk feat. Pharrell Williams & Nile Rodgers",
        TrackNo = 8,
        DiscNo = 1,
        DiscCount = 1,
        ArtistMbId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56",
        AlbumMbId = "aa997ea0-2936-40bd-884d-3af8a0e064dc",
        RecordingMbId = "833f00e1-781f-4edd-90e4-e52712618862",
        ReleaseMbId = "5000a285-b67e-4cfc-b54b-2b98f1810d2e",
        Isrc = "USQX91300108",
        QualityTitle = "FLAC",
        QualityFull = "FLAC",
        AudioCodec = "FLAC",
        AudioBitRate = 1025,
        AudioSampleRate = 44100,
        AudioBitsPerSample = 16,
        Source = "Soulseek",
    };
}
