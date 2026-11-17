using System.Net;
using FluentAssertions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// <see cref="MusicBrainzClient.GetRecordingRelationsAsync"/> against the two responses recorded on 2026-11-17: the
/// audio recording of Teardrop and the video recording it links to.
/// </summary>
public sealed class MusicBrainzRelationsTests
{
    /// <summary>The audio recording of Teardrop.</summary>
    internal const string TeardropAudioId = "f3bba4cd-8018-468b-902e-bc8f029593e5";

    /// <summary>The video recording of Teardrop.</summary>
    internal const string TeardropVideoId = "72f0aa36-3a7b-49fc-ae93-42c54c72dc0d";

    [Fact]
    public async Task The_audio_recording_links_forward_to_its_video()
    {
        var (client, handler) = CreateClient();

        var relations = await client.GetRecordingRelationsAsync(TeardropAudioId.ToUpperInvariant());

        relations.Should().NotBeNull();
        relations!.Id.Should().Be(TeardropAudioId);
        relations.Video.Should().BeFalse();
        relations.LengthMs.Should().Be(330026);

        var link = relations.MusicVideos.Should().ContainSingle().Subject;
        link.TargetRecordingId.Should().Be(TeardropVideoId);
        link.TargetIsVideo.Should().BeTrue();
        link.Direction.Should().Be("forward");

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be($"/ws/2/recording/{TeardropAudioId}");
        uri.Query.Should().Contain("inc=recording-rels+url-rels").And.Contain("fmt=json");
    }

    [Fact]
    public async Task The_audio_recordings_url_relations_are_read_with_their_types()
    {
        var (client, _) = CreateClient();

        var relations = await client.GetRecordingRelationsAsync(TeardropAudioId);

        relations!.Urls.Should().Contain(new MbUrlRelation("free streaming", "https://www.youtube.com/watch?v=u7K72X4eo_s"));
        relations.Urls.Should().Contain(new MbUrlRelation("free streaming", "https://music.youtube.com/watch?v=3h-JYx76QNM"));
        relations.Urls.Should().Contain(url => url.Resource.Contains("spotify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_video_recording_is_a_video_with_its_youtube_link_and_a_backward_link_to_the_audio()
    {
        var (client, _) = CreateClient();

        var relations = await client.GetRecordingRelationsAsync(TeardropVideoId);

        relations!.Video.Should().BeTrue();
        relations.LengthMs.Should().Be(284000);
        relations.Urls.Should().ContainSingle().Which.Should().Be(
            new MbUrlRelation("free streaming", "https://www.youtube.com/watch?v=u7K72X4eo_s"));

        var back = relations.MusicVideos.Should().ContainSingle().Subject;
        back.TargetRecordingId.Should().Be(TeardropAudioId);
        back.TargetIsVideo.Should().BeFalse();
        back.Direction.Should().Be("backward");
    }

    [Fact]
    public async Task Relations_that_are_not_music_videos_or_urls_are_left_out()
    {
        var (client, _) = CreateClient();

        var relations = await client.GetRecordingRelationsAsync(TeardropAudioId);

        // The recording also has edit, DJ-mix and mash-up relations: none of them is a music video link.
        relations!.MusicVideos.Should().OnlyContain(link => link.TargetRecordingId == TeardropVideoId);
    }

    [Fact]
    public async Task A_repeat_lookup_is_served_from_the_cache()
    {
        var (client, handler) = CreateClient();

        await client.GetRecordingRelationsAsync(TeardropAudioId);
        await client.GetRecordingRelationsAsync(TeardropAudioId);

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_recording_is_null()
    {
        var handler = new FixtureHttpMessageHandler(_ => MusicBrainzFixtures.NotFound());
        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            new InMemoryMetadataCache());

        (await client.GetRecordingRelationsAsync(TeardropAudioId)).Should().BeNull();
    }

    [Fact]
    public async Task A_malformed_id_is_refused_before_any_request()
    {
        var (client, handler) = CreateClient();

        var act = () => client.GetRecordingRelationsAsync("not-a-guid");

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_error_is_a_metadata_provider_exception()
    {
        var handler = new FixtureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            new InMemoryMetadataCache());

        var act = () => client.GetRecordingRelationsAsync(TeardropAudioId);

        await act.Should().ThrowAsync<MetadataProviderException>();
    }

    /// <summary>A client over a handler that serves the recorded response for the recording asked for.</summary>
    internal static (MusicBrainzClient Client, FixtureHttpMessageHandler Handler) CreateClient()
    {
        var handler = new FixtureHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith(TeardropVideoId, StringComparison.Ordinal)
            ? MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-rels-teardrop-video.json"))
            : MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-rels-teardrop.json")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };

        return (new MusicBrainzClient(http, new InMemoryMetadataCache()), handler);
    }
}
