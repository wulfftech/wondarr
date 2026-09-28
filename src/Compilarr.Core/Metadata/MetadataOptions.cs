using Microsoft.Extensions.Options;

namespace Compilarr.Core.Metadata;

/// <summary>
/// The <c>metadata</c> section of <c>config.yml</c>: the base URL of every external metadata
/// provider Compilarr talks to, plus the contact URL MusicBrainz requires in the User-Agent.
/// </summary>
public sealed class MetadataOptions
{
    /// <summary>Base URL of the MusicBrainz WS/2 API.</summary>
    public string MusicBrainzBaseUrl { get; set; } = "https://musicbrainz.org/ws/2/";

    /// <summary>Base URL of the Cover Art Archive API.</summary>
    public string CoverArtArchiveBaseUrl { get; set; } = "https://coverartarchive.org/";

    /// <summary>Base URL of the Deezer API.</summary>
    public string DeezerBaseUrl { get; set; } = "https://api.deezer.com/";

    /// <summary>Base URL of the iTunes Search API.</summary>
    public string ITunesBaseUrl { get; set; } = "https://itunes.apple.com/";

    /// <summary>Contact URL sent in the <c>User-Agent</c>, as MusicBrainz asks callers to do.</summary>
    public string ContactUrl { get; set; } = "https://github.com/wulfftech/compilarr";

    /// <summary>
    /// Base delay the MusicBrainz retry pipeline backs off with. Deliberately not bound from
    /// configuration: two seconds is the polite floor against MusicBrainz, and only tests lower it.
    /// </summary>
    internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Validates <see cref="MetadataOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class MetadataOptionsValidator : IValidateOptions<MetadataOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Check("metadata.musicbrainz_base_url", options.MusicBrainzBaseUrl, failures);
        Check("metadata.cover_art_archive_base_url", options.CoverArtArchiveBaseUrl, failures);
        Check("metadata.deezer_base_url", options.DeezerBaseUrl, failures);
        Check("metadata.itunes_base_url", options.ITunesBaseUrl, failures);
        Check("metadata.contact_url", options.ContactUrl, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string key, string? value, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{key}: must be an absolute http or https URL (was '{value}')");
        }
    }
}

/// <summary>
/// Normalises the provider base URLs so a relative request path can simply be appended: trimmed,
/// with exactly one trailing slash. <see cref="MetadataOptions.ContactUrl"/> is only trimmed — it
/// is a link for humans, not a base to resolve against.
/// </summary>
public sealed class MetadataOptionsPostConfigure : IPostConfigureOptions<MetadataOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, MetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.MusicBrainzBaseUrl = WithTrailingSlash(options.MusicBrainzBaseUrl);
        options.CoverArtArchiveBaseUrl = WithTrailingSlash(options.CoverArtArchiveBaseUrl);
        options.DeezerBaseUrl = WithTrailingSlash(options.DeezerBaseUrl);
        options.ITunesBaseUrl = WithTrailingSlash(options.ITunesBaseUrl);
        options.ContactUrl = (options.ContactUrl ?? string.Empty).Trim();
    }

    private static string WithTrailingSlash(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        return trimmed.Length == 0 || trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
