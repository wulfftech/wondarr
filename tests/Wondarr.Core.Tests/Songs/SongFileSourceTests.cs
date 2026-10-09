using Wondarr.Core.Songs;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>Only the safe fields of <c>song_file.source_ref</c> ever leave it.</summary>
public sealed class SongFileSourceTests
{
    [Fact]
    public void A_download_keeps_its_provider_the_file_name_and_the_queue_item_but_not_the_peers_folders()
    {
        var source = SongFileSource.Parse(
            "soulseek",
            """{"provider":"peer","remotePath":"\\\\peer\\Music\\Album\\01 Song.flac","candidateId":3,"queueItemId":9,"searchRunId":4}""");

        source.Should().Be(new SongFileSource("download", "soulseek", "01 Song.flac", null, null, 9));
    }

    [Fact]
    public void A_slash_separated_soulseek_path_is_cut_the_same_way()
    {
        SongFileSource.Parse("soulseek", """{"provider":"peer","remotePath":"Music/Album/01 Song.flac"}""")
            .Name.Should().Be("01 Song.flac");
    }

    [Fact]
    public void An_indexer_grab_keeps_the_users_indexer_name_and_a_release_title_whole()
    {
        var source = SongFileSource.Parse(
            "torznab",
            """{"provider":"My Indexer","remotePath":"AC/DC - Back in Black [FLAC]","queueItemId":2}""");

        source.Should().Be(new SongFileSource("download", "My Indexer", "AC/DC - Back in Black [FLAC]", null, null, 2));
    }

    [Fact]
    public void A_reference_file_keeps_its_library_and_row_ids_only()
    {
        var source = SongFileSource.Parse("reference", """{"referenceLibraryId":2,"referenceFileId":15}""");

        source.Should().Be(new SongFileSource("reference", null, null, 2, 15, null));
    }

    [Fact]
    public void An_adopted_file_is_not_given_the_original_path()
    {
        var source = SongFileSource.Parse(
            "adopted",
            """{"referenceLibraryId":2,"referenceFileId":15,"originalPath":"/home/me/Music/x.flac"}""");

        source.Kind.Should().Be("adopted");
        source.ReferenceLibraryId.Should().Be(2);
        source.Name.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void A_missing_or_malformed_blob_leaves_only_the_kind(string? sourceRef)
    {
        var source = SongFileSource.Parse("youtube", sourceRef);

        source.Should().Be(new SongFileSource("download", "youtube", null, null, null, null));
    }

    [Fact]
    public void A_file_with_no_source_type_is_unknown()
    {
        SongFileSource.Parse(string.Empty, null).Kind.Should().Be("unknown");
    }
}
