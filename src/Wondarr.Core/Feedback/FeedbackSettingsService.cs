using System.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;

namespace Wondarr.Core.Feedback;

/// <summary>
/// Whether the feedback token is set. The token itself is never part of this: a settings page that can
/// read a token back would put it in every response, log and browser cache on the way.
/// </summary>
/// <param name="TokenSet">Whether <c>feedback.github_token</c> holds a token.</param>
/// <param name="TokenLocked">Whether the environment sets it (<c>APP__FEEDBACK__GITHUB_TOKEN</c>), so it cannot be changed here.</param>
public sealed record FeedbackSettings(bool TokenSet, bool TokenLocked);

/// <summary>How an update ended.</summary>
/// <param name="Success">Whether the token was written.</param>
/// <param name="Errors">Why it was refused: a token that cannot be one, or a field the environment owns.</param>
public sealed record FeedbackSettingsUpdateResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>Reads, writes and tests the GitHub token of the Contribute with feedback card.</summary>
public interface IFeedbackSettingsService
{
    /// <summary>Reports whether a token is set and whether the environment owns it.</summary>
    FeedbackSettings Current();

    /// <summary>
    /// Writes the token to <c>config.yml</c>; the options reload, so the feedback button appears without a
    /// restart. A <see langword="null"/> token is left alone and an empty one removes it.
    /// </summary>
    /// <param name="token">The new token.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<FeedbackSettingsUpdateResult> UpdateAsync(string? token, CancellationToken cancellationToken);

    /// <summary>Checks the typed token with GitHub, or the stored one when none is typed. Opens nothing.</summary>
    /// <param name="token">The token to try, or <see langword="null"/> for the stored one.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<FeedbackTokenCheck> TestAsync(string? token, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IFeedbackSettingsService"/>. A token set by <c>APP__FEEDBACK__GITHUB_TOKEN</c> is
/// refused rather than silently overridden: the environment source is added after the YAML one, so a write
/// to the file would be invisible.
/// </summary>
public sealed partial class FeedbackSettingsService : IFeedbackSettingsService
{
    /// <summary>The longest token accepted; GitHub's are 40 (classic) and 93 (fine-grained) characters.</summary>
    public const int MaxTokenLength = 255;

    /// <summary>The environment variable that owns the token when it is set.</summary>
    public const string TokenEnvironment = "APP__FEEDBACK__GITHUB_TOKEN";

    private const string Section = "feedback";
    private const string TokenKey = "github_token";

    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly IFeedbackService _feedback;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<FeedbackSettingsService> _logger;
    private readonly bool _locked;

    /// <summary>Initialises a new instance of the <see cref="FeedbackSettingsService"/> class.</summary>
    /// <param name="options">The live <c>feedback</c> options, which already include the environment.</param>
    /// <param name="writer">Writes <c>config.yml</c> and reloads the configuration.</param>
    /// <param name="feedback">Checks a token with GitHub.</param>
    /// <param name="secrets">Told about a new token before it is written, so no log line can print it.</param>
    /// <param name="environment">The process environment, used to find whether it owns the token.</param>
    /// <param name="logger">Receives a Debug line when the token changes, never the token.</param>
    public FeedbackSettingsService(
        IOptionsMonitor<FeedbackOptions> options,
        IConfigFileWriter writer,
        IFeedbackService feedback,
        ISecretRegistry secrets,
        IDictionary environment,
        ILogger<FeedbackSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _writer = writer;
        _feedback = feedback;
        _secrets = secrets;
        _logger = logger;
        _locked = EnvironmentOverrides.SectionKeys(environment, "FEEDBACK").Contains("githubtoken");
    }

    /// <inheritdoc />
    public FeedbackSettings Current() => new(_options.CurrentValue.GitHubToken is not null, _locked);

    /// <inheritdoc />
    public async Task<FeedbackSettingsUpdateResult> UpdateAsync(string? token, CancellationToken cancellationToken)
    {
        if (token is null)
        {
            return new FeedbackSettingsUpdateResult(true, []);
        }

        if (_locked)
        {
            return new FeedbackSettingsUpdateResult(
                false,
                [$"token is set by the environment variable {TokenEnvironment} and cannot be changed here"]);
        }

        var trimmed = token.Trim();

        if (Problem(trimmed) is { } problem)
        {
            return new FeedbackSettingsUpdateResult(false, [problem]);
        }

        var value = trimmed.Length == 0 ? null : trimmed;

        // Before the write, so the reload that follows cannot log the new token before it is known as one.
        _secrets.Register(value);

        await _writer
            .UpdateSectionsAsync(
                new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
                {
                    [Section] = new Dictionary<string, object?> { [TokenKey] = value },
                },
                cancellationToken)
            .ConfigureAwait(false);

        LogUpdated(_logger, value is not null);

        return new FeedbackSettingsUpdateResult(true, []);
    }

    /// <inheritdoc />
    public async Task<FeedbackTokenCheck> TestAsync(string? token, CancellationToken cancellationToken)
    {
        var typed = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        if (typed is not null && Problem(typed) is { } problem)
        {
            return new FeedbackTokenCheck(false, problem);
        }

        var candidate = typed ?? _options.CurrentValue.GitHubToken;

        return candidate is null
            ? new FeedbackTokenCheck(false, "There is no token to test; paste one first.")
            : await _feedback.CheckTokenAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why the trimmed text cannot be a token, or <see langword="null"/> when it can (an empty one removes it).</summary>
    private static string? Problem(string trimmed)
    {
        if (trimmed.Length > MaxTokenLength)
        {
            return "token is too long to be a GitHub token";
        }

        // A token is one word: a space or a line break means something else was pasted with it.
        if (trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return "token has spaces or line breaks in it; paste the token alone";
        }

        // GitHub's tokens are letters, digits and _. Anything else (a smart quote, a zero-width space from a
        // chat app) would also fail every request: HTTP headers take ASCII only.
        return trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? null
            : "token has characters a GitHub token never has (only letters, digits and _); paste the token alone";
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the feedback token (set: {Set})")]
    private static partial void LogUpdated(ILogger logger, bool set);
}
