using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Logging;
using Wondarr.Core.Updates;

namespace Wondarr.Core.Feedback;

/// <summary>What the feedback is about.</summary>
public enum FeedbackKind
{
    /// <summary>Something is broken. Labelled <c>bug</c> on GitHub.</summary>
    Bug,

    /// <summary>Something could be better. Labelled <c>enhancement</c> on GitHub.</summary>
    Idea,
}

/// <summary>One submission from the feedback button.</summary>
/// <param name="Kind">Bug or idea.</param>
/// <param name="Text">What the person wrote.</param>
/// <param name="Page">The page they were on, as the router has it (<c>/library?sort=title</c>), without the URL base.</param>
/// <param name="PageTitle">That page's heading or document title.</param>
public sealed record FeedbackSubmission(FeedbackKind Kind, string Text, string? Page, string? PageTitle);

/// <summary>Where the feedback ended up.</summary>
/// <param name="Raised">Whether Wondarr opened the issue itself.</param>
/// <param name="IssueUrl">The new issue when <paramref name="Raised"/>; otherwise GitHub's issue form, prefilled, for the person to submit.</param>
/// <param name="IssueNumber">The new issue's number, when GitHub said.</param>
/// <param name="Reason">Why a configured token was not used, or <see langword="null"/>.</param>
/// <param name="MayBeRaised">
/// Whether the request reached GitHub but its answer did not come back (a timeout, a dropped
/// connection): the issue may exist although <paramref name="Raised"/> is false.
/// </param>
public sealed record FeedbackOutcome(bool Raised, string IssueUrl, int? IssueNumber, string? Reason, bool MayBeRaised = false);

/// <summary>What the feedback button will do.</summary>
/// <param name="AutoRaise">Whether a submission opens the issue directly (a token is set and neither GitHub's limit nor Wondarr's own is in force).</param>
/// <param name="Repository">The repository issues go to, <c>owner/name</c>.</param>
/// <param name="RepositoryUrl">That repository's page.</param>
public sealed record FeedbackStatus(bool AutoRaise, string Repository, string RepositoryUrl);

/// <summary>
/// Turns a submission from the feedback button into a GitHub issue. Every submission is logged first,
/// so it is never lost. With <see cref="FeedbackOptions.GitHubToken"/> set the issue is opened through
/// the API; without one, or when GitHub refuses, the outcome is the prefilled issue form instead.
/// </summary>
public interface IFeedbackService
{
    /// <summary>Whether a submission would open the issue directly. Makes no request.</summary>
    FeedbackStatus GetStatus();

    /// <summary>
    /// Logs the submission and raises it. Never throws for a network or GitHub failure: that becomes the
    /// form link and <see cref="FeedbackOutcome.Reason"/>.
    /// </summary>
    /// <param name="submission">The feedback; its text must not be blank.</param>
    /// <param name="cancellationToken">Cancels the request to GitHub.</param>
    Task<FeedbackOutcome> SubmitAsync(FeedbackSubmission submission, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class FeedbackService : IFeedbackService
{
    /// <summary>The name of the <see cref="HttpClient"/> issues are opened through.</summary>
    public const string ClientName = "github-issues";

    /// <summary>The issues endpoint of the repository.</summary>
    public const string IssuesUrl = "https://api.github.com/repos/" + UpdateCheckService.Repository + "/issues";

    /// <summary>GitHub's new-issue form, which takes the title, body and labels as query parameters.</summary>
    public const string NewIssueUrl = UpdateCheckService.ProjectUrl + "/issues/new";

    /// <summary>The repository's issue list, to look at when a request may have opened an issue anyway.</summary>
    public const string IssueListUrl = UpdateCheckService.ProjectUrl + "/issues";

    /// <summary>The longest text accepted.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The longest page path kept; a longer one is cut.</summary>
    public const int MaxPageLength = 300;

    /// <summary>The longest page title kept; a longer one is cut.</summary>
    public const int MaxPageTitleLength = 120;

    /// <summary>The longest issue title, taken from the first line of the text.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>
    /// The longest prefilled form link. GitHub answers a link much past 8 KB with an error page, so a
    /// longer text is cut to fit (the full text is in the log).
    /// </summary>
    public const int MaxFormUrlLength = 7000;

    /// <summary>The issue title when no line of the text is more than control characters.</summary>
    public const string DefaultTitle = "Feedback from Wondarr";

    /// <summary>
    /// The most issues Wondarr asks GitHub to open per <see cref="IssueWindow"/>. The issues carry the
    /// token owner's name, so a script holding the API key must not be able to flood the repository.
    /// </summary>
    public const int MaxIssuesPerWindow = 5;

    /// <summary>The window <see cref="MaxIssuesPerWindow"/> counts in.</summary>
    public static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the request to GitHub may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The wait after a limit GitHub states without saying until when. GitHub asks for at least a minute
    /// after a secondary rate limit.
    /// </summary>
    private static readonly TimeSpan ShortBlock = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly ISecretRegistry _secrets;
    private readonly TimeProvider _time;
    private readonly RunningVersion _running;
    private readonly ILogger<FeedbackService> _logger;

    private readonly Lock _stateLock = new();
    private readonly Queue<DateTimeOffset> _recentRequests = new();
    private DateTimeOffset? _blockedUntil;

    /// <summary>Initialises a new instance of the <see cref="FeedbackService"/> class.</summary>
    /// <param name="clients">Hands out the GitHub client.</param>
    /// <param name="options">The live <c>feedback</c> settings.</param>
    /// <param name="secrets">Removes known secrets from what goes to the public issue.</param>
    /// <param name="time">The clock.</param>
    /// <param name="running">The version of this build, written into each issue.</param>
    /// <param name="logger">The log sink.</param>
    public FeedbackService(
        IHttpClientFactory clients,
        IOptionsMonitor<FeedbackOptions> options,
        ISecretRegistry secrets,
        TimeProvider time,
        RunningVersion running,
        ILogger<FeedbackService> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _secrets = secrets;
        _time = time;
        _running = running;
        _logger = logger;
    }

    /// <inheritdoc />
    public FeedbackStatus GetStatus() => new(
        _options.CurrentValue.GitHubToken is not null && BlockedUntil() is null && ThrottledUntil() is null,
        UpdateCheckService.Repository,
        UpdateCheckService.ProjectUrl);

    /// <inheritdoc />
    public async Task<FeedbackOutcome> SubmitAsync(FeedbackSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.Text);

        var kind = submission.Kind;
        var text = submission.Text.Trim();
        var page = CleanPage(submission.Page);

        // Logged before anything else: with no token, or a GitHub that refuses, this line is the record.
        LogReceived(_logger, kind, page ?? "an unknown page", text);

        // The issue is public: a pasted link with an API key or a token in it, or a home folder, goes out redacted.
        var issue = new Issue(
            kind,
            _secrets.Redact(text),
            page is null ? null : CleanPage(_secrets.Redact(page)),
            CleanLine(submission.PageTitle is null ? null : _secrets.Redact(submission.PageTitle), MaxPageTitleLength)?
                .Replace("`", string.Empty, StringComparison.Ordinal));

        var token = _options.CurrentValue.GitHubToken;

        if (token is null)
        {
            return Form(issue, reason: null);
        }

        if (BlockedUntil() is { } blocked)
        {
            return NotRaised(issue, RateLimitMessage(blocked));
        }

        if (!TryTakeRequestSlot(out var throttledUntil))
        {
            return NotRaised(
                issue,
                $"Wondarr opens at most {MaxIssuesPerWindow} issues every {(int)IssueWindow.TotalMinutes} minutes; the next one after {LocalTime(throttledUntil)}.");
        }

        // On the injected clock, so a test can run the 15 seconds out without waiting for them.
        using var deadline = new CancellationTokenSource(RequestTimeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var client = _clients.CreateClient(ClientName);
            var payload = new IssueRequest(Title(issue.Text), Body(issue, issue.Text, cut: false), [Label(kind)]);

            using var request = new HttpRequestMessage(HttpMethod.Post, IssuesUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Never retried: a POST that timed out may still have opened the issue, and a second one would duplicate it.
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var (number, url) = ParseCreated(json);

                LogRaised(_logger, url);

                return new FeedbackOutcome(true, url, number, null);
            }

            return NotRaised(issue, Refusal(response, json));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotRaised(issue, $"GitHub did not answer within {(int)RequestTimeout.TotalSeconds} seconds.", mayBeRaised: true);
        }
        catch (HttpRequestException exception)
        {
            // Without a connection nothing was sent; a connection that broke later may have delivered the request.
            var unsent = exception.HttpRequestError is HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError;

            return NotRaised(
                issue,
                $"GitHub could not be reached ({exception.HttpRequestError.ToString().ToLowerInvariant()}).",
                mayBeRaised: !unsent);
        }
    }

    /// <summary>The first line of the text, on one line and cut at a word near <see cref="MaxTitleLength"/>.</summary>
    private static string Title(string text)
    {
        var line = text.Split('\n').Select(part => CleanLine(part, int.MaxValue)).FirstOrDefault(part => part is not null);

        if (line is null)
        {
            return DefaultTitle;
        }

        if (line.Length <= MaxTitleLength)
        {
            return line;
        }

        var cut = line[..(MaxTitleLength - 1)];
        var space = cut.LastIndexOf(' ');

        // A word boundary only when it keeps most of the line; a long unbroken word is cut where it is.
        return string.Concat(space > MaxTitleLength / 2 ? cut[..space] : TrimSurrogate(cut), "…");
    }

    /// <summary>
    /// The issue body: the text, then the page and version it came from. The page and its heading are
    /// code spans, so a heading taken from library data (an artist called <c>@someone</c>, a link) stays text.
    /// </summary>
    private string Body(Issue issue, string text, bool cut)
    {
        var builder = new StringBuilder(text.Length + 300);

        builder.Append(text).Append("\n\n");

        if (cut)
        {
            builder.Append("*(Cut short to fit GitHub's form; the full text is in Wondarr's log.)*\n\n");
        }

        builder.Append("---\n\n");
        builder.Append("- **Kind:** ").Append(issue.Kind == FeedbackKind.Bug ? "Bug" : "Idea").Append('\n');

        if (issue.Page is not null)
        {
            builder.Append("- **Page:** `").Append(issue.Page).Append('`');

            if (issue.PageTitle is not null)
            {
                builder.Append(" (`").Append(issue.PageTitle).Append("`)");
            }

            builder.Append('\n');
        }

        builder.Append("- **Wondarr:** ").Append(_running.Text).Append("\n\n");
        builder.Append("<sub>Sent with the feedback button in Wondarr.</sub>\n");

        return builder.ToString();
    }

    /// <summary>
    /// GitHub's issue form with the title, body and label filled in. The text is cut when the link would
    /// be too long for GitHub; everything else is short enough by the limits above.
    /// </summary>
    private string FormUrl(Issue issue)
    {
        var text = issue.Text;
        var prefix = string.Concat(NewIssueUrl, "?title=", Uri.EscapeDataString(Title(text)), "&labels=", Label(issue.Kind), "&body=");

        // Never half a surrogate pair: escaping one throws.
        string Link(int length)
        {
            var kept = TrimSurrogate(text[..length]);

            return prefix + Uri.EscapeDataString(Body(issue, kept, cut: kept.Length < text.Length));
        }

        var full = Link(text.Length);

        if (full.Length <= MaxFormUrlLength)
        {
            return full;
        }

        // The longest prefix of the text that fits. Escaping one character takes between 1 and 9 characters,
        // so the length of the link only grows with the length of the text and a binary search finds it.
        int low = 0, high = text.Length - 1;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (Link(middle).Length <= MaxFormUrlLength)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Link(low);
    }

    private FeedbackOutcome Form(Issue issue, string? reason, bool mayBeRaised = false) =>
        new(false, FormUrl(issue), null, reason, mayBeRaised);

    private FeedbackOutcome NotRaised(Issue issue, string reason, bool mayBeRaised = false)
    {
        LogNotRaised(_logger, reason);

        return Form(issue, reason, mayBeRaised);
    }

    private string Refusal(HttpResponseMessage response, string body)
    {
        var now = _time.GetUtcNow();

        // A stated wait is honoured whatever the status. Without one, a 429 is a limit, and so is a 403 that
        // says so: GitHub's secondary limit sends neither Retry-After nor x-ratelimit-remaining: 0 at times.
        var until = UpdateCheckService.LimitEnd(response, now);

        if (until is null
            && (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))))
        {
            until = now + ShortBlock;
        }

        if (until is { } end)
        {
            lock (_stateLock)
            {
                // Two refusals answered at once must not let the shorter wait replace the longer one.
                if (_blockedUntil is null || _blockedUntil < end)
                {
                    _blockedUntil = end;
                }
            }

            return RateLimitMessage(end);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub refused the token (401); it may have expired.",
            HttpStatusCode.Forbidden => $"The token may not open issues on {UpdateCheckService.Repository} (403).",
            HttpStatusCode.NotFound => $"The token cannot see {UpdateCheckService.Repository} (404).",
            HttpStatusCode.Gone => $"Issues are switched off on {UpdateCheckService.Repository} (410).",
            _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + ".",
        };
    }

    private DateTimeOffset? BlockedUntil()
    {
        lock (_stateLock)
        {
            if (_blockedUntil is { } until && until <= _time.GetUtcNow())
            {
                _blockedUntil = null;
            }

            return _blockedUntil;
        }
    }

    /// <summary>When Wondarr's own limit lets the next request through, or <see langword="null"/> when it does now.</summary>
    private DateTimeOffset? ThrottledUntil()
    {
        lock (_stateLock)
        {
            ForgetOldRequests();

            return _recentRequests.Count < MaxIssuesPerWindow ? null : _recentRequests.Peek() + IssueWindow;
        }
    }

    /// <summary>Counts a request against Wondarr's own limit, unless the limit is reached.</summary>
    private bool TryTakeRequestSlot(out DateTimeOffset throttledUntil)
    {
        lock (_stateLock)
        {
            ForgetOldRequests();

            if (_recentRequests.Count >= MaxIssuesPerWindow)
            {
                throttledUntil = _recentRequests.Peek() + IssueWindow;

                return false;
            }

            // Counted before it is sent, so submissions at the same moment cannot all slip under the limit.
            _recentRequests.Enqueue(_time.GetUtcNow());
            throttledUntil = default;

            return true;
        }
    }

    private void ForgetOldRequests()
    {
        var since = _time.GetUtcNow() - IssueWindow;

        while (_recentRequests.Count > 0 && _recentRequests.Peek() <= since)
        {
            _recentRequests.Dequeue();
        }
    }

    private string RateLimitMessage(DateTimeOffset until) => $"GitHub's rate limit is in force until {LocalTime(until)}.";

    private string LocalTime(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("HH:mm 'UTC'zzz", CultureInfo.InvariantCulture);

    /// <summary>The new issue's number and page. An answer Wondarr cannot read still means the issue exists.</summary>
    private static (int? Number, string Url) ParseCreated(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, IssueListUrl);
            }

            int? number = root.TryGetProperty("number", out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
            var url = root.TryGetProperty("html_url", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;

            // Only a GitHub page goes back to the browser as a link.
            return url is not null
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host == "github.com"
                    ? (number, url)
                    : (number, IssueListUrl);
        }
        catch (JsonException)
        {
            return (null, IssueListUrl);
        }
    }

    private static string Label(FeedbackKind kind) => kind == FeedbackKind.Bug ? "bug" : "enhancement";

    /// <summary>A path that starts with <c>/</c>, on one line, without backticks (it is shown as code), cut to length.</summary>
    private static string? CleanPage(string? page)
    {
        var line = CleanLine(page, MaxPageLength);

        return line is null || !line.StartsWith('/') ? null : line.Replace("`", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text on one line, whitespace and control characters collapsed to single spaces, cut to
    /// <paramref name="maxLength"/>; <see langword="null"/> when nothing else is left.
    /// </summary>
    private static string? CleanLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var space = false;

        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = true;

                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(character);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var line = builder.ToString();

        return line.Length <= maxLength ? line : TrimSurrogate(line[..maxLength]);
    }

    /// <summary>Drops a high surrogate left at the end by a cut, so the text never ends in half a character.</summary>
    private static string TrimSurrogate(string value) =>
        value.Length > 0 && char.IsHighSurrogate(value[^1]) ? value[..^1] : value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Feedback ({Kind}) from {Page}: {Text}")]
    private static partial void LogReceived(ILogger logger, FeedbackKind kind, string page, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feedback raised as {IssueUrl}")]
    private static partial void LogRaised(ILogger logger, string issueUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feedback was not raised on GitHub: {Reason} The prefilled issue form was offered instead")]
    private static partial void LogNotRaised(ILogger logger, string reason);

    /// <summary>What goes to GitHub: the text, page and heading after redaction and clean-up.</summary>
    private sealed record Issue(FeedbackKind Kind, string Text, string? Page, string? PageTitle);

    private sealed record IssueRequest(string Title, string Body, string[] Labels);
}
