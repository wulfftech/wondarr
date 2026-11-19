using Microsoft.Extensions.Options;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// The <c>music_videos</c> section of <c>config.yml</c> (ADR-0014): whether Wondarr keeps a music
/// video per song, and where and how it files it. Everything here is off or empty until the user
/// turns the feature on.
/// </summary>
public sealed class MusicVideoOptions
{
    /// <summary>The layout that files videos under <see cref="RootPath"/> by the naming template.</summary>
    public const string FolderLayout = "folder";

    /// <summary>The layout that files a video next to the song's audio file.</summary>
    public const string BesideTrackLayout = "beside_track";

    /// <summary>The naming template a fresh install uses.</summary>
    public const string DefaultNamingTemplate = "{Artist}/{Artist} - {Title}";

    /// <summary>Whether music videos are on. Off by default: the video column, the actions and the task appear only while it is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The video root: where <see cref="FolderLayout"/> files videos, and where a song held only by a
    /// reference library falls back to under <see cref="BesideTrackLayout"/>. It carries the
    /// <c>.wondarr-library</c> marker like a library root. Holds no path of its own: the configuration layer below
    /// <c>config.yml</c> supplies <c>&lt;media root&gt;/music-videos</c> (<c>WondarrPaths.MediaRootDefaults</c>), so
    /// it is <see langword="null"/> only where that layer is absent (a bare options instance).
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary><see cref="FolderLayout"/> or <see cref="BesideTrackLayout"/>.</summary>
    public string Layout { get; set; } = FolderLayout;

    /// <summary>
    /// The path of a video under the root, without its extension. It may use <c>{Artist}</c>,
    /// <c>{AlbumArtist}</c>, <c>{Title}</c>, <c>{Album}</c> and <c>{Year}</c>.
    /// </summary>
    public string NamingTemplate { get; set; } = DefaultNamingTemplate;

    /// <summary>What is added to the audio file's name to name its video under <see cref="BesideTrackLayout"/>.</summary>
    public string BesideTrackSuffix { get; set; } = "-video";

    /// <summary>The tallest picture to download, in pixels, or <see langword="null"/> for no cap.</summary>
    public int? MaxHeight { get; set; }

    /// <summary>Whether a Kodi/Jellyfin <c>.nfo</c> file is written beside the video.</summary>
    public bool WriteNfo { get; set; } = true;

    /// <summary>Whether a thumbnail is written beside the video.</summary>
    public bool WriteThumbnail { get; set; } = true;

    /// <summary>How many songs one run of the scheduled task may search for.</summary>
    public int ScheduledSearchLimit { get; set; } = 25;

    /// <summary>
    /// Whether the scheduled "Find missing music videos" task runs. Off by default, and it runs only while
    /// <see cref="Enabled"/> is on too.
    /// </summary>
    public bool ScheduledSearchEnabled { get; set; }

    /// <summary>How often the scheduled task runs, in hours.</summary>
    public int ScheduledSearchIntervalHours { get; set; } = 24;
}

/// <summary>
/// The rules for the naming template, shared by the settings service and the options validator.
/// </summary>
public static class MusicVideoTemplate
{
    /// <summary>The tokens a template may use.</summary>
    public static readonly IReadOnlyList<string> Tokens = ["{Artist}", "{AlbumArtist}", "{Title}", "{Album}", "{Year}"];

    /// <summary>Why <paramref name="template"/> is not usable, or <see langword="null"/> when it is.</summary>
    /// <param name="template">The template text.</param>
    public static string? Problem(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "must not be empty";
        }

        var rest = template;

        foreach (var token in Tokens)
        {
            rest = rest.Replace(token, string.Empty, StringComparison.Ordinal);
        }

        // Whatever is left holds no token, so a brace there is an unknown token or a stray one.
        var open = rest.IndexOf('{', StringComparison.Ordinal);
        var close = rest.IndexOf('}', StringComparison.Ordinal);

        if (open < 0 && close < 0)
        {
            return null;
        }

        var end = open >= 0 ? rest.IndexOf('}', open) : -1;
        var unknown = open >= 0 && end > open ? rest[open..(end + 1)] : null;

        return unknown is not null
            ? $"uses {unknown}, which is not a token; use {string.Join(", ", Tokens)}"
            : $"has a stray brace; use only {string.Join(", ", Tokens)}";
    }
}

/// <summary>
/// Validates <see cref="MusicVideoOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>. The root's rules (absolute, outside the
/// libraries) need the database and are the settings service's.
/// </summary>
public sealed class MusicVideoOptionsValidator : IValidateOptions<MusicVideoOptions>
{
    /// <summary>The most songs one scheduled run may search for.</summary>
    public const int MaxScheduledSearchLimit = 500;

    /// <summary>The longest wait between two scheduled runs, in hours (30 days).</summary>
    public const int MaxScheduledSearchIntervalHours = 720;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MusicVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.Layout is not (MusicVideoOptions.FolderLayout or MusicVideoOptions.BesideTrackLayout))
        {
            failures.Add(
                $"music_videos.layout: must be {MusicVideoOptions.FolderLayout} or {MusicVideoOptions.BesideTrackLayout} (was {options.Layout})");
        }

        if (MusicVideoTemplate.Problem(options.NamingTemplate) is { } problem)
        {
            failures.Add($"music_videos.naming_template: {problem}");
        }

        if (string.IsNullOrWhiteSpace(options.BesideTrackSuffix))
        {
            failures.Add("music_videos.beside_track_suffix: must not be empty");
        }
        else if (options.BesideTrackSuffix.Contains("..", StringComparison.Ordinal)
            || options.BesideTrackSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || options.BesideTrackSuffix.IndexOfAny(['/', '\\']) >= 0)
        {
            failures.Add("music_videos.beside_track_suffix: must not hold a path separator, '..' or a character a file name cannot have");
        }

        if (options.MaxHeight is <= 0)
        {
            failures.Add($"music_videos.max_height: must be above 0, or unset for no cap (was {options.MaxHeight})");
        }

        if (options.ScheduledSearchLimit is < 1 or > MaxScheduledSearchLimit)
        {
            failures.Add(
                $"music_videos.scheduled_search_limit: must be between 1 and {MaxScheduledSearchLimit} (was {options.ScheduledSearchLimit})");
        }

        if (options.ScheduledSearchIntervalHours is < 1 or > MaxScheduledSearchIntervalHours)
        {
            failures.Add(
                $"music_videos.scheduled_search_interval_hours: must be between 1 and {MaxScheduledSearchIntervalHours} (was {options.ScheduledSearchIntervalHours})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
