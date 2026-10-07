using FluentAssertions;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Profiles;

/// <summary>
/// One output rule and the library policy that holds three of them (ADR-0008): the defaults, the
/// JSON they are stored as, and every value they refuse — each refusal naming the JSON key it
/// came from.
/// </summary>
public sealed class OutputPolicyTests
{
    [Fact]
    public void A_library_without_a_policy_gets_the_default()
    {
        var policy = LibraryOutputPolicy.Parse(null);

        policy.YouTube.Codec.Should().Be(OutputCodec.Aac);
        policy.YouTube.Mode.Should().Be(OutputMode.Cbr);
        policy.YouTube.BitrateKbps.Should().Be(256);
        policy.YouTube.VbrQuality.Should().Be(0);
        policy.YouTube.SampleRateHz.Should().BeNull();
        policy.YouTube.Container.Should().Be("m4a");

        policy.Lossy.Codec.Should().Be(OutputCodec.Keep);
        policy.Lossless.Codec.Should().Be(OutputCodec.Keep);
    }

    [Fact]
    public void An_empty_object_is_the_default_too()
    {
        LibraryOutputPolicy.Parse("{}").ToJson().Should().Be(LibraryOutputPolicy.Parse("  ").ToJson());
    }

    [Fact]
    public void Every_key_round_trips_through_the_stored_json()
    {
        var policy = OutputPolicy.Parse(
            """
            {"codec":"mp3","mode":"vbr","bitrateKbps":320,"vbrQuality":2,"sampleRate":44100}
            """);

        var reparsed = OutputPolicy.Parse(policy.ToJson());

        policy.ToJson().Should().Be(
            """{"codec":"mp3","mode":"vbr","bitrateKbps":320,"vbrQuality":2,"sampleRate":44100}""");
        reparsed.Should().BeEquivalentTo(policy);
    }

    [Fact]
    public void The_default_serialises_with_every_key_spelled_out()
    {
        OutputPolicy.Default.ToJson().Should().Be(
            """{"codec":"aac","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep"}""");
    }

    [Fact]
    public void The_library_default_serialises_as_version_2_with_every_rule_spelled_out()
    {
        LibraryOutputPolicy.Default.ToJson().Should().Be(
            """{"version":2,"youtube":{"codec":"aac","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep"},"lossy":{"codec":"keep","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep","opusContainer":"opus"},"lossless":{"codec":"keep","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep","opusContainer":"opus"}}""");
    }

    [Fact]
    public void A_version_2_policy_round_trips_through_the_stored_json()
    {
        var policy = LibraryOutputPolicy.Parse(
            """
            {"version":2,"youtube":{"codec":"keep","opusContainer":"ogg"},"lossy":{"codec":"mp3","mode":"vbr","vbrQuality":0},"lossless":{"codec":"alac"}}
            """);

        var reparsed = LibraryOutputPolicy.Parse(policy.ToJson());

        policy.ToJson().Should().Be(
            """{"version":2,"youtube":{"codec":"keep","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep","opusContainer":"ogg"},"lossy":{"codec":"mp3","mode":"vbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep"},"lossless":{"codec":"alac","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep"}}""");
        reparsed.Should().BeEquivalentTo(policy);
    }

    [Fact]
    public void A_version_1_policy_is_the_youtube_rule_and_the_rest_keeps()
    {
        var policy = LibraryOutputPolicy.Parse(
            """{"codec":"mp3","mode":"vbr","vbrQuality":2,"sampleRate":44100}""");

        policy.YouTube.Codec.Should().Be(OutputCodec.Mp3);
        policy.YouTube.Mode.Should().Be(OutputMode.Vbr);
        policy.YouTube.VbrQuality.Should().Be(2);
        policy.YouTube.SampleRateHz.Should().Be(44_100);
        policy.Lossy.Codec.Should().Be(OutputCodec.Keep);
        policy.Lossless.Codec.Should().Be(OutputCodec.Keep);
    }

    [Fact]
    public void A_version_1_keepOpus_is_still_accepted_as_keep()
    {
        var policy = LibraryOutputPolicy.Parse("""{"codec":"keepOpus"}""");

        policy.YouTube.Codec.Should().Be(OutputCodec.Keep);
        policy.YouTube.OpusContainer.Should().Be("opus");
    }

    [Fact]
    public void Keep_names_the_opus_container_and_ogg_renames_it()
    {
        OutputPolicy.Parse("""{"codec":"keep"}""").Container.Should().Be("keep");
        OutputPolicy.Parse("""{"codec":"keep","opusContainer":"ogg"}""").OpusContainer.Should().Be("ogg");
        OutputPolicy.Parse("""{"codec":"opus","opusContainer":"ogg"}""").Container.Should().Be("ogg");
    }

    [Fact]
    public void A_lossless_codec_is_refused_with_the_key_named()
    {
        foreach (var lossless in new[] { "flac", "alac", "wav", "ape", "wv" })
        {
            var act = () => OutputPolicy.Parse($$"""{"codec":"{{lossless}}"}""");

            var exception = act.Should().Throw<ProfileValidationException>();
            exception.Which.Errors.Should().ContainSingle()
                .Which.Property.Should().Be("codec");
        }
    }

    [Fact]
    public void A_lossless_codec_is_accepted_where_a_lossless_source_makes_it_honest()
    {
        OutputPolicy.Parse("""{"codec":"flac"}""", allowLossless: true).Codec.Should().Be(OutputCodec.Flac);
        OutputPolicy.Parse("""{"codec":"alac"}""", allowLossless: true).Codec.Should().Be(OutputCodec.Alac);

        // The raw formats are never a target Wondarr writes, whatever the source.
        foreach (var refused in new[] { "wav", "ape", "wv" })
        {
            var act = () => OutputPolicy.Parse($$"""{"codec":"{{refused}}"}""", allowLossless: true);

            act.Should().Throw<ProfileValidationException>();
        }
    }

    [Fact]
    public void A_lossless_rule_is_refused_in_the_youtube_and_lossy_rules()
    {
        foreach (var rule in new[] { "youtube", "lossy" })
        {
            var act = () => LibraryOutputPolicy.Parse(
                $$$"""{"version":2,"{{{rule}}}":{"codec":"flac"}}""");

            var exception = act.Should().Throw<ProfileValidationException>();
            exception.Which.Errors.Should().ContainSingle()
                .Which.Property.Should().Be($$"""{{rule}}.codec""");
        }

        LibraryOutputPolicy.Parse("""{"version":2,"lossless":{"codec":"flac"}}""")
            .Lossless.Codec.Should().Be(OutputCodec.Flac);
    }

    [Fact]
    public void An_unknown_top_level_key_is_refused()
    {
        var act = () => LibraryOutputPolicy.Parse("""{"version":2,"loudness":-14}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("outputPolicy");
    }

    [Fact]
    public void A_wrong_version_is_refused()
    {
        var act = () => LibraryOutputPolicy.Parse("""{"version":3}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("version");
    }

    [Fact]
    public void A_bad_rule_value_names_the_rule_in_its_errors()
    {
        var act = () => LibraryOutputPolicy.Parse("""{"version":2,"lossless":{"bitrateKbps":9001}}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("lossless.bitrateKbps");
    }

    [Fact]
    public void A_bad_version_1_value_names_the_youtube_rule_in_its_errors()
    {
        var act = () => LibraryOutputPolicy.Parse("""{"codec":"aac","mode":"vbr"}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("youtube.mode");
    }

    [Fact]
    public void An_opus_container_is_validated()
    {
        var act = () => OutputPolicy.Parse("""{"codec":"keep","opusContainer":"ogv"}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("opusContainer");
    }

    [Theory]
    [InlineData("bitrateKbps", 63)]
    [InlineData("bitrateKbps", 321)]
    [InlineData("vbrQuality", -1)]
    [InlineData("vbrQuality", 10)]
    public void Out_of_range_numbers_are_refused_with_the_key_named(string key, int value)
    {
        var act = () => OutputPolicy.Parse($$"""{"{{key}}":{{value}}}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be(key);
    }

    [Fact]
    public void Aac_with_vbr_is_refused_because_the_lame_scale_is_mp3s()
    {
        var act = () => OutputPolicy.Parse("""{"codec":"aac","mode":"vbr"}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("mode");
    }

    [Fact]
    public void A_sample_rate_of_zero_is_refused()
    {
        var act = () => OutputPolicy.Parse("""{"sampleRate":0}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("sampleRate");
    }

    [Fact]
    public void A_sample_rate_in_hertz_is_kept()
    {
        OutputPolicy.Parse("""{"sampleRate":48000}""").SampleRateHz.Should().Be(48_000);
    }

    [Fact]
    public void Not_json_is_refused()
    {
        var act = () => OutputPolicy.Parse("aac");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("outputPolicy");
    }

    [Fact]
    public void An_unknown_key_is_refused()
    {
        var act = () => OutputPolicy.Parse("""{"loudness":-14}""");

        var exception = act.Should().Throw<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("outputPolicy");
    }

    [Fact]
    public void The_rule_for_a_download_is_picked_by_its_source_class()
    {
        var policy = LibraryOutputPolicy.Parse("""{"version":2,"lossless":{"codec":"alac"}}""");

        policy.RuleFor(SourceTypes.YouTube, sourceIsLossless: false).Should().BeSameAs(policy.YouTube);
        policy.RuleFor(SourceTypes.YouTube, sourceIsLossless: true).Should().BeSameAs(policy.YouTube);
        policy.RuleFor(SourceTypes.Soulseek, sourceIsLossless: false).Should().BeSameAs(policy.Lossy);
        policy.RuleFor(SourceTypes.Soulseek, sourceIsLossless: true).Should().BeSameAs(policy.Lossless);
    }

    [Fact]
    public void A_file_already_in_the_rules_target_codec_is_recognised()
    {
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"mp3"}"""), "mp3").Should().BeTrue();
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"aac"}"""), "aac").Should().BeTrue();
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"opus"}"""), "opus").Should().BeTrue();
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"flac"}""", allowLossless: true), "flac").Should().BeTrue();
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"alac"}""", allowLossless: true), "alac").Should().BeTrue();

        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"mp3"}"""), "flac").Should().BeFalse();
        LibraryOutputPolicy.SameCodec(OutputPolicy.Parse("""{"codec":"keep"}"""), "mp3").Should().BeFalse();
    }
}
