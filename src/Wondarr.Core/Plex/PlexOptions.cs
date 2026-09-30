using Microsoft.Extensions.Options;

namespace Wondarr.Core.Plex;

/// <summary>
/// The <c>plex</c> section of <c>config.yml</c>: the plex.tv endpoints Wondarr signs in against and
/// how long a Plex request may take.
/// </summary>
public sealed class PlexOptions
{
    /// <summary>Base URL of the plex.tv API, where the sign-in PIN is created and polled.</summary>
    public string PlexTvBaseUrl { get; set; } = "https://plex.tv/";

    /// <summary>The page the user opens to approve a sign-in PIN.</summary>
    public string AppAuthUrl { get; set; } = "https://app.plex.tv/auth";

    /// <summary>
    /// How long one request to plex.tv or to a Plex Media Server may take, in seconds. The server
    /// answer also bounds "is this server reachable?", so it stays short.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Validates <see cref="PlexOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class PlexOptionsValidator : IValidateOptions<PlexOptions>
{
    /// <summary>The shortest request timeout the validator accepts, in seconds.</summary>
    public const int MinimumTimeoutSeconds = 5;

    /// <summary>The longest request timeout the validator accepts, in seconds.</summary>
    public const int MaximumTimeoutSeconds = 120;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckUrl("plex.plex_tv_base_url", options.PlexTvBaseUrl, failures);
        CheckUrl("plex.app_auth_url", options.AppAuthUrl, failures);

        if (options.RequestTimeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            failures.Add(
                $"plex.request_timeout_seconds: must be between {MinimumTimeoutSeconds} and "
                + $"{MaximumTimeoutSeconds} (was {options.RequestTimeoutSeconds})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckUrl(string key, string? value, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{key}: must be an absolute http or https URL (was '{value}')");
        }
    }
}
