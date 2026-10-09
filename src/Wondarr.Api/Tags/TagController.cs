using Wondarr.Core.Songs;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Tags;

/// <summary>The tags in use on songs, with how many songs carry each (Lidarr's <c>/tag</c> shape, without ids).</summary>
[ApiController]
[Route("api/v1/tag")]
public sealed class TagController : ControllerBase
{
    private readonly ISongEditorService _editor;

    /// <summary>Initialises a new instance of the <see cref="TagController"/> class.</summary>
    /// <param name="editor">The editor service, which counts the tags.</param>
    public TagController(ISongEditorService editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        _editor = editor;
    }

    /// <summary>Lists the tags in use, ordered by label.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<TagResource>>> GetTags(CancellationToken cancellationToken)
    {
        var tags = await _editor.GetTagsAsync(cancellationToken).ConfigureAwait(false);

        return Ok(tags.Select(tag => new TagResource(tag.Label, tag.SongCount)).ToList());
    }
}

/// <summary>One tag in use.</summary>
/// <param name="Label">The tag.</param>
/// <param name="SongCount">How many songs carry it.</param>
public sealed record TagResource(string Label, int SongCount);
