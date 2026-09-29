using Wondarr.Core.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Metadata.AcoustId;

/// <summary>
/// The <c>acoustid</c> section of <c>config.yml</c>: the AcoustID client key, the score thresholds a
/// fingerprint verdict is judged by, and how fast this process is allowed to ask.
/// </summary>
public sealed class AcoustIdOptions
{
    /// <summary>
    /// The AcoustID client key. Optional: with no key Wondarr never calls AcoustID and a download is
    /// verified by probe and duration alone (MATCHING_ENGINE.md §6.5). It is registered with
    /// <see cref="ISecretRegistry"/> so the log redactor removes it.
    /// </summary>
    public string? ClientKey { get; set; }

    /// <summary>Base URL of the AcoustID web service.</summary>
    public string BaseUrl { get; set; } = "https://api.acoustid.org/v2/";

    /// <summary>The score at or above which a fingerprint match is accepted.</summary>
    public double AcceptScore { get; set; } = 0.7;

    /// <summary>The score at or above which a match is imported with a low-confidence badge.</summary>
    public double ReviewScore { get; set; } = 0.5;

    /// <summary>Whether a below-<see cref="AcceptScore"/> match fails the download instead of being reviewed.</summary>
    public bool Strict { get; set; }

    /// <summary>
    /// Requests per second this process may make. AcoustID serves three; the limit is a ceiling, not a
    /// hint, so the validator refuses anything above it.
    /// </summary>
    public int RequestsPerSecond { get; set; } = 3;

    /// <summary>The spacing every AcoustID request is started with.</summary>
    public TimeSpan RequestInterval => TimeSpan.FromMilliseconds(1000.0 / RequestsPerSecond);
}

/// <summary>
/// Validates <see cref="AcoustIdOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class AcoustIdOptionsValidator : IValidateOptions<AcoustIdOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AcoustIdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckScore("acoustid.accept_score", options.AcceptScore, failures);
        CheckScore("acoustid.review_score", options.ReviewScore, failures);

        if (options.ReviewScore > options.AcceptScore)
        {
            failures.Add(
                $"acoustid.review_score: must not be above acoustid.accept_score "
                + $"(was {options.ReviewScore} and {options.AcceptScore})");
        }

        if (options.RequestsPerSecond is < 1 or > 3)
        {
            failures.Add(
                $"acoustid.requests_per_second: must be between 1 and 3, which is the service's own limit "
                + $"(was {options.RequestsPerSecond})");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"acoustid.base_url: must be an absolute http or https URL (was '{options.BaseUrl}')");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckScore(string key, double value, List<string> failures)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            failures.Add($"{key}: must be a score between 0 and 1 (was {value})");
        }
    }
}

/// <summary>
/// Trims the base URL to exactly one trailing slash, like the metadata provider URLs, and hands the
/// client key to the secret registry so it cannot reach a log sink.
/// </summary>
public sealed class AcoustIdOptionsPostConfigure : IPostConfigureOptions<AcoustIdOptions>
{
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="AcoustIdOptionsPostConfigure"/> class.</summary>
    /// <param name="secrets">The registry every sink redacts through.</param>
    public AcoustIdOptionsPostConfigure(ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, AcoustIdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.BaseUrl = WithTrailingSlash(options.BaseUrl);
        options.ClientKey = string.IsNullOrWhiteSpace(options.ClientKey) ? null : options.ClientKey.Trim();

        // Registered here, not at registration time, because the key is bound from configuration.
        _secrets.Register(options.ClientKey);
    }

    private static string WithTrailingSlash(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        return trimmed.Length == 0 || trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
