using Wondarr.Sources.YouTube;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The parser over the responses recorded from the live InnerTube on 2026-10-05
/// (<c>tests/fixtures/ytmusic/</c>): every result the response states is returned, with the fields the
/// response carried and nothing else.
/// </summary>
public class InnertubeResponseParserTests
{
    [Fact]
    public void Reads_the_songs_shelf_into_six_results()
    {
        var search = Parse("search-songs.json", InnertubeSearchFilter.Songs);

        search.TopResult.Should().BeNull();
        search.Results.Should().HaveCount(6);
        search.Query.Should().Be("daft punk get lucky");
        search.Filter.Should().Be(InnertubeSearchFilter.Songs);

        var first = search.Results[0];
        first.VideoId.Should().Be("4D7u5KF7SP8");
        first.Title.Should().Be("Get Lucky (feat. Pharrell Williams and Nile Rodgers)");
        first.Artists.Should().Equal("Daft Punk", "Pharrell Williams", "Nile Rodgers");
        first.Album.Should().Be("Random Access Memories");
        first.DurationMs.Should().Be(370_000);
        first.MusicVideoType.Should().Be(InnertubeVideoTypes.ArtTrack);
        first.IsExplicit.Should().BeFalse();

        // The radio edit's album is named after the song, and the cover's artist is its own channel.
        search.Results[1].Album.Should().Be("Get Lucky (Radio Edit - feat. Pharrell Williams and Nile Rodgers)");
        search.Results[3].Artists.Should().Equal("Leo");
        search.Results[3].DurationMs.Should().Be(209_000);
    }

    [Fact]
    public void Reads_the_videos_shelf_with_the_channel_as_the_only_artist()
    {
        var search = Parse("search-videos.json", InnertubeSearchFilter.Videos);

        search.Results.Should().HaveCount(6);

        var first = search.Results[0];
        first.VideoId.Should().Be("CCHdMIEGaaM");
        first.Title.Should().Be("Daft Punk - Get Lucky (Official Video) feat. Pharrell Williams and Nile Rodgers");
        first.Artists.Should().Equal("convar HUN");
        first.Album.Should().BeNull();
        first.DurationMs.Should().Be(248_000);
        first.MusicVideoType.Should().Be(InnertubeVideoTypes.UserUpload);

        search.Results[1].MusicVideoType.Should().Be(InnertubeVideoTypes.OfficialVideo);
        search.Results[3].DurationMs.Should().Be(4_275_000);
    }

    [Fact]
    public void Reads_the_ISRC_response_as_a_card_plus_loose_results()
    {
        var search = Parse("search-isrc.json", InnertubeSearchFilter.None);

        var card = search.TopResult;
        card.Should().NotBeNull();
        card!.VideoId.Should().Be("HzdD8kbDzZA");
        card.Title.Should().Be("Take on Me");
        card.Artists.Should().Equal("a-ha");
        card.Album.Should().BeNull();
        card.DurationMs.Should().Be(226_000);
        card.MusicVideoType.Should().Be(InnertubeVideoTypes.ArtTrack);

        // The item sections are loosely related results: two videos without a duration, and a song
        // credited to three artists, one of which carries no browse id and is therefore not credited.
        search.Results.Should().HaveCount(3);
        search.Results[0].VideoId.Should().Be("Z4GMUlCBgd0");
        search.Results[0].DurationMs.Should().BeNull();
        search.Results[0].MusicVideoType.Should().Be(InnertubeVideoTypes.OfficialVideo);
        search.Results[2].Artists.Should().Equal("KK", "Shaan", "Pravin Mani");
        search.Results[2].DurationMs.Should().BeNull();
    }

    [Fact]
    public void Reads_the_explicit_badge()
    {
        const string json = """
            {
              "contents": {
                "tabbedSearchResultsRenderer": {
                  "tabs": [
                    {
                      "tabRenderer": {
                        "content": {
                          "sectionListRenderer": {
                            "contents": [
                              {
                                "musicShelfRenderer": {
                                  "contents": [
                                    {
                                      "musicResponsiveListItemRenderer": {
                                        "flexColumns": [
                                          {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "Get Lucky"}]}}},
                                          {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "Daft Punk", "navigationEndpoint": {"browseEndpoint": {"browseId": "UCRr1xG_2WIDs18a6cIiCxeA"}}}]}}}
                                        ],
                                        "overlay": {"musicItemThumbnailOverlayRenderer": {"content": {"musicPlayButtonRenderer": {
                                          "playNavigationEndpoint": {"watchEndpoint": {
                                            "videoId": "4D7u5KF7SP8",
                                            "watchEndpointMusicSupportedConfigs": {"watchEndpointMusicConfig": {"musicVideoType": "MUSIC_VIDEO_TYPE_ATV"}}
                                          }}
                                        }}}},
                                        "badges": [{"musicInlineBadgeRenderer": {"accessibilityData": {"accessibilityData": {"label": "Explicit"}}}}]
                                      }
                                    }
                                  ]
                                }
                              }
                            ]
                          }
                        }
                      }
                    }
                  ]
                }
              }
            }
            """;

        var search = InnertubeResponseParser.Parse(json, "daft punk get lucky", InnertubeSearchFilter.Songs);

        var result = search.Results.Should().ContainSingle().Subject;
        result.IsExplicit.Should().BeTrue();
        result.Title.Should().Be("Get Lucky");
        result.Artists.Should().Equal("Daft Punk");
        result.DurationMs.Should().BeNull();
    }

    [Fact]
    public void Answers_an_empty_result_for_a_body_without_the_search_shape()
    {
        var search = InnertubeResponseParser.Parse("{}", "daft punk get lucky", InnertubeSearchFilter.Songs);

        search.TopResult.Should().BeNull();
        search.Results.Should().BeEmpty();
        search.Query.Should().Be("daft punk get lucky");
        search.Filter.Should().Be(InnertubeSearchFilter.Songs);
    }

    [Fact]
    public void Skips_items_without_a_video_id()
    {
        const string json = """
            {
              "contents": {
                "tabbedSearchResultsRenderer": {
                  "tabs": [
                    {
                      "tabRenderer": {
                        "content": {
                          "sectionListRenderer": {
                            "contents": [
                              {
                                "musicShelfRenderer": {
                                  "contents": [
                                    {"musicResponsiveListItemRenderer": {"flexColumns": [
                                      {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "A shelf header, not a result"}]}}}
                                    ]}},
                                    {"musicResponsiveListItemRenderer": {
                                      "flexColumns": [
                                        {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "Get Lucky"}]}}},
                                        {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "Daft Punk", "navigationEndpoint": {"browseEndpoint": {"browseId": "UCRr1xG_2WIDs18a6cIiCxeA"}}}]}}}
                                      ],
                                      "overlay": {"musicItemThumbnailOverlayRenderer": {"content": {"musicPlayButtonRenderer": {
                                        "playNavigationEndpoint": {"watchEndpoint": {
                                          "videoId": "4D7u5KF7SP8",
                                          "watchEndpointMusicSupportedConfigs": {"watchEndpointMusicConfig": {"musicVideoType": "MUSIC_VIDEO_TYPE_ATV"}}
                                        }}
                                      }}}}
                                    }}
                                  ]
                                }
                              }
                            ]
                          }
                        }
                      }
                    }
                  ]
                }
              }
            }
            """;

        var search = InnertubeResponseParser.Parse(json, "daft punk get lucky", InnertubeSearchFilter.Songs);

        search.Results.Should().ContainSingle().Which.VideoId.Should().Be("4D7u5KF7SP8");
    }

    private static InnertubeSearchResult Parse(string name, InnertubeSearchFilter filter) =>
        InnertubeResponseParser.Parse(YouTubeTestData.ReadFixture(name), "daft punk get lucky", filter);
}
