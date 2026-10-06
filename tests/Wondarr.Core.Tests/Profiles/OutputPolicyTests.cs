using FluentAssertions;
using Wondarr.Core.Profiles;
using Xunit;

namespace Wondarr.Core.Tests.Profiles;

/// <summary>
/// The per-library output policy for YouTube-sourced files (ADR-0008): the defaults, the JSON it is
/// stored as, and every value it refuses — each refusal naming the JSON key it came from.
/// </summary>
public sealed class OutputPolicyTests
{
    [Fact]
    public void A_library_without_a_policy_gets_the_default()
    {
        var policy = OutputPolicy.Parse(null);

        policy.Codec.Should().Be(OutputCodec.Aac);
        policy.Mode.Should().Be(OutputMode.Cbr);
        policy.BitrateKbps.Should().Be(256);
        policy.VbrQuality.Should().Be(0);
        policy.SampleRateHz.Should().BeNull();
        policy.Container.Should().Be("m4a");
    }

    [Fact]
    public void An_empty_object_is_the_default_too()
    {
        OutputPolicy.Parse("{}").Should().BeEquivalentTo(OutputPolicy.Parse("  "));
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
    public void KeepOpus_names_the_opus_container()
    {
        OutputPolicy.Parse("""{"codec":"keepOpus"}""").Container.Should().Be("opus");
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
}
