using Wondarr.Api.Paging;
using Wondarr.Api.Songs;
using Wondarr.Core.MusicVideos;

namespace Wondarr.Api.MusicVideos;

/// <summary>Fills <see cref="SongResource.MusicVideo"/> on a page of songs.</summary>
public static class SongMusicVideoMapping
{
    /// <summary>
    /// Adds each song's music video state with one query for the whole page. The state stays <see langword="null"/>
    /// on every song while music videos are turned off, and on a song that has no video.
    /// </summary>
    /// <param name="page">The page of songs.</param>
    /// <param name="videos">Reads the states.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public static async Task<PagingResource<SongResource>> WithMusicVideosAsync(
        this PagingResource<SongResource> page,
        IMusicVideoService videos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(videos);

        var states = await videos
            .GetStatesAsync([.. page.Records.Select(song => song.Id)], cancellationToken)
            .ConfigureAwait(false);

        if (states is null || states.Count == 0)
        {
            return page;
        }

        return page with
        {
            Records =
            [
                .. page.Records.Select(song => states.TryGetValue(song.Id, out var state)
                    ? song with { MusicVideo = new SongMusicVideoResource(state.Status, state.Height, state.FailureReason, state.UpdatedAt) }
                    : song),
            ],
        };
    }
}
