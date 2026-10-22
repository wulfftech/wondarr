namespace Wondarr.Api.YouTube;

/// <summary>One YouTube video's details, as the "Add to MusicBrainz" form uses them.</summary>
/// <param name="VideoId">The video id.</param>
/// <param name="Url">The video's watch link, the form MusicBrainz expects.</param>
/// <param name="Title">The video's title, or <see langword="null"/>.</param>
/// <param name="Channel">The channel that uploaded it, or <see langword="null"/>.</param>
/// <param name="UploadDate">The day it was uploaded, as <c>yyyy-MM-dd</c>, or <see langword="null"/>.</param>
/// <param name="DurationMs">Its length in milliseconds, or <see langword="null"/>.</param>
public sealed record YouTubeVideoResource(
    string VideoId,
    string Url,
    string? Title,
    string? Channel,
    string? UploadDate,
    int? DurationMs);
