namespace Wondarr.Api.Profiles;

/// <summary>The naming-preview request: a library's template, or a custom one, rendered for a song.</summary>
/// <param name="SongId">The song to render for, or <see langword="null"/> for the built-in sample.</param>
/// <param name="Template">The template to try, or <see langword="null"/> to use the library's own.</param>
public sealed record LibraryPreviewRequestResource(long? SongId, string? Template);

/// <summary>
/// What a template renders to. An invalid template is not an error response: the UI shows the
/// problems next to the box the user is typing in, so <see cref="Path"/> is <see langword="null"/>
/// and <see cref="Errors"/> carries every message the parser produced.
/// </summary>
/// <param name="Path">The absolute path the file would be written to, or <see langword="null"/> when the template is invalid.</param>
/// <param name="Errors">Every problem the template has; empty when it rendered.</param>
public sealed record LibraryPreviewResource(string? Path, IReadOnlyList<string> Errors);