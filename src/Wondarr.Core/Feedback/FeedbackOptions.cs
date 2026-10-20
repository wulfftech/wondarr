using Microsoft.Extensions.Options;
using Wondarr.Core.Logging;

namespace Wondarr.Core.Feedback;

/// <summary>
/// The <c>feedback</c> section of <c>config.yml</c> (environment: <c>APP__FEEDBACK__GITHUB_TOKEN</c>).
/// Optional. Without a token the feedback button hands back a prefilled GitHub issue form for the
/// person to submit themselves.
/// </summary>
public sealed class FeedbackOptions
{
    /// <summary>
    /// A GitHub token that may open issues on the Wondarr repository (a fine-grained token with Issues:
    /// read and write, or a classic one with <c>public_repo</c>). With it the feedback button opens the
    /// issue itself. It is registered with <see cref="ISecretRegistry"/> so the log redactor removes it.
    /// </summary>
    public string? GitHubToken { get; set; }
}

/// <summary>Trims the token and hands it to the secret registry so it cannot reach a log sink.</summary>
public sealed class FeedbackOptionsPostConfigure : IPostConfigureOptions<FeedbackOptions>
{
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="FeedbackOptionsPostConfigure"/> class.</summary>
    /// <param name="secrets">The registry every sink redacts through.</param>
    public FeedbackOptionsPostConfigure(ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, FeedbackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.GitHubToken = string.IsNullOrWhiteSpace(options.GitHubToken) ? null : options.GitHubToken.Trim();

        // Registered here, not at registration time, because the token is bound from configuration.
        _secrets.Register(options.GitHubToken);
    }
}
