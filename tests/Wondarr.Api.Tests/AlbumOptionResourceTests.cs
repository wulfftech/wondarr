using FluentAssertions;
using Wondarr.Api.Songs;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Api.Tests;

public sealed class AlbumOptionResourceTests
{
    private const string ReleaseId = "9c1b3a2f-0000-0000-0000-000000000001";
    private const string GroupId = "9c1b3a2f-0000-0000-0000-000000000002";

    [Fact]
    public void A_release_id_builds_the_release_thumbnail_url()
    {
        var resource = Option(release: ReleaseId, group: GroupId).ToResource(isCurrent: false);

        resource.CoverUrl.Should().Be($"https://coverartarchive.org/release/{ReleaseId}/front-250");
    }

    [Fact]
    public void A_release_group_alone_builds_the_release_group_thumbnail_url()
    {
        var resource = Option(release: null, group: GroupId).ToResource(isCurrent: false);

        resource.CoverUrl.Should().Be($"https://coverartarchive.org/release-group/{GroupId}/front-250");
    }

    [Fact]
    public void A_deezer_option_uses_the_cover_it_holds()
    {
        var resource = Option(release: null, group: null, cover: "https://cdn.example/cover.jpg").ToResource(isCurrent: false);

        resource.CoverUrl.Should().Be("https://cdn.example/cover.jpg");
    }

    [Fact]
    public void An_option_with_no_ids_and_no_cover_has_none()
    {
        Option(release: null, group: null).ToResource(isCurrent: false).CoverUrl.Should().BeNull();
    }

    [Fact]
    public void The_extra_facts_are_carried_over()
    {
        var resource = new ReleaseOption
        {
            Key = "k",
            Title = "So Fresh",
            AlbumArtist = "Various Artists",
            IsVariousArtists = true,
            ReleaseGroupFirstDate = "1999-01-01",
            DiscNo = 2,
        }.ToResource(isCurrent: true);

        resource.IsVariousArtists.Should().BeTrue();
        resource.OriginalDate.Should().Be("1999-01-01");
        resource.DiscNo.Should().Be(2);
        resource.IsCurrent.Should().BeTrue();
    }

    private static ReleaseOption Option(string? release, string? group, string? cover = null) => new()
    {
        Key = release ?? "deezer-key",
        MbReleaseId = release,
        MbReleaseGroupId = group,
        Title = "Album",
        AlbumArtist = "Artist",
        CoverUrl = cover,
    };
}
