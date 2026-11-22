using Wondarr.Core.MusicVideos;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Reads a music video's title from YouTube's oEmbed endpoint, through the client and the answer cache the finder uses, so
/// a title looked up for a candidate is not asked for again.
/// </summary>
public sealed class OEmbedMusicVideoTitleSource : IMusicVideoTitleSource
{
    private readonly IYouTubeOEmbedClient _oEmbed;

    /// <summary>Initialises a new instance of the <see cref="OEmbedMusicVideoTitleSource"/> class.</summary>
    /// <param name="oEmbed">Reads an upload's real title and channel.</param>
    public OEmbedMusicVideoTitleSource(IYouTubeOEmbedClient oEmbed)
    {
        ArgumentNullException.ThrowIfNull(oEmbed);

        _oEmbed = oEmbed;
    }

    /// <inheritdoc />
    public async Task<string?> GetTitleAsync(string videoId, CancellationToken cancellationToken)
    {
        var answer = await _oEmbed.GetAsync(videoId, cancellationToken).ConfigureAwait(false);

        return answer?.Title;
    }
}
