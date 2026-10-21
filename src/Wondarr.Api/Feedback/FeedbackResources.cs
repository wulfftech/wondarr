using Wondarr.Core.Feedback;

namespace Wondarr.Api.Feedback;

/// <summary>What the feedback button will do with a submission.</summary>
/// <param name="TokenConfigured">Whether a GitHub token is set; without one the header shows no feedback button.</param>
/// <param name="AutoRaise">Whether a submission opens the GitHub issue directly; otherwise the answer is a prefilled issue form.</param>
/// <param name="Repository">The repository issues go to, <c>owner/name</c>.</param>
/// <param name="RepositoryUrl">That repository's page.</param>
/// <param name="MaxTextLength">The longest text a submission may have.</param>
public sealed record FeedbackStatusResource(bool TokenConfigured, bool AutoRaise, string Repository, string RepositoryUrl, int MaxTextLength)
{
    /// <summary>Maps the service's status onto the resource.</summary>
    /// <param name="status">The status to map.</param>
    public static FeedbackStatusResource From(FeedbackStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new FeedbackStatusResource(
            status.TokenConfigured,
            status.AutoRaise,
            status.Repository,
            status.RepositoryUrl,
            FeedbackService.MaxTextLength);
    }
}

/// <summary>A bug report or an idea from the feedback button.</summary>
/// <param name="Kind">Bug or idea.</param>
/// <param name="Text">What the person wrote; required.</param>
/// <param name="Page">The page they were on, as the router has it (<c>/library?sort=title</c>). A longer one is cut.</param>
/// <param name="PageTitle">That page's title. A longer one is cut.</param>
public sealed record FeedbackCreateResource(FeedbackKind Kind = FeedbackKind.Bug, string? Text = null, string? Page = null, string? PageTitle = null);

/// <summary>Where the feedback ended up.</summary>
/// <param name="Raised">Whether Wondarr opened the issue itself.</param>
/// <param name="IssueUrl">The new issue when <paramref name="Raised"/>; otherwise GitHub's issue form, prefilled, for the person to submit.</param>
/// <param name="IssueNumber">The new issue's number, when GitHub said.</param>
/// <param name="Reason">Why a configured token was not used, or <see langword="null"/>.</param>
/// <param name="MayBeRaised">Whether GitHub may have opened the issue although its answer never came back (a timeout, a dropped connection): look at the issue list before finishing the form.</param>
/// <param name="IssueListUrl">The repository's issue list.</param>
public sealed record FeedbackResultResource(bool Raised, string IssueUrl, int? IssueNumber, string? Reason, bool MayBeRaised, string IssueListUrl)
{
    /// <summary>Maps the service's outcome onto the resource.</summary>
    /// <param name="outcome">The outcome to map.</param>
    public static FeedbackResultResource From(FeedbackOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new FeedbackResultResource(
            outcome.Raised,
            outcome.IssueUrl,
            outcome.IssueNumber,
            outcome.Reason,
            outcome.MayBeRaised,
            FeedbackService.IssueListUrl);
    }
}

/// <summary>The Contribute with feedback card of Settings &gt; General. Never carries the token.</summary>
/// <param name="TokenSet">Whether a GitHub token is stored.</param>
/// <param name="TokenLocked">Whether the environment sets it (<c>APP__FEEDBACK__GITHUB_TOKEN</c>), so it cannot be changed here.</param>
public sealed record FeedbackSettingsResource(bool TokenSet, bool TokenLocked)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    /// <param name="settings">The settings to map.</param>
    public static FeedbackSettingsResource From(FeedbackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new FeedbackSettingsResource(settings.TokenSet, settings.TokenLocked);
    }
}

/// <summary>A change to the feedback token. An absent token is left alone; an empty one removes it.</summary>
/// <param name="Token">The new GitHub token.</param>
public sealed record FeedbackSettingsUpdateResource(string? Token = null);

/// <summary>A token to check with GitHub.</summary>
/// <param name="Token">The token to try; absent or empty for the stored one.</param>
public sealed record FeedbackTokenTestRequest(string? Token = null);

/// <summary>What the token check found.</summary>
/// <param name="Ok">Whether the token can be expected to open issues.</param>
/// <param name="Message">A sentence to show; never the token.</param>
public sealed record FeedbackTokenTestResource(bool Ok, string Message);
