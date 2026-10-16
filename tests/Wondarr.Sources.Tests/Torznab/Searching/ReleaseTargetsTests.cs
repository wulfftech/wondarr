using FluentAssertions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>The releases a song is searched for when MusicBrainz knows nothing of it.</summary>
public sealed class ReleaseTargetsTests
{
    [Theory]
    [InlineData(AlbumContextKind.PseudoSingles, "Singles")]
    [InlineData(AlbumContextKind.Compilation, "My Music Library")]
    public async Task A_pseudo_album_with_no_musicbrainz_data_is_no_release_to_ask_for(AlbumContextKind kind, string title)
    {
        var targets = await ReleaseTargets.FindAsync(Substitute.For<IMusicBrainzClient>(), Request(kind, title), _ => { }, CancellationToken.None);

        targets.Should().BeEmpty("\"Singles\" is not a release, whatever an indexer would answer to it");
    }

    [Theory]
    [InlineData(AlbumContextKind.Album)]
    [InlineData(null)]
    public async Task A_real_album_with_no_musicbrainz_data_is_still_a_release_to_ask_for(AlbumContextKind? kind)
    {
        // A real album that only Deezer knows: it has no MusicBrainz release id.
        var targets = await ReleaseTargets.FindAsync(Substitute.For<IMusicBrainzClient>(), Request(kind, "Come Away with Me"), _ => { }, CancellationToken.None);

        targets.Should().Equal(new ReleaseTarget("Norah Jones", "Come Away with Me", null, 2));
    }

    private static SongSearchRequest Request(AlbumContextKind? kind, string album) =>
        new(1, "Seven Years", "Norah Jones", ["Norah Jones"], album, 150_000, VersionFlags.None)
        {
            AlbumKind = kind,
            AlbumTrackNo = 2,
        };
}
