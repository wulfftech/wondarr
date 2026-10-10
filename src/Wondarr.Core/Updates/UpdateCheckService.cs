using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Messaging;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Updates;

/// <summary>
/// Asks GitHub whether a newer Wondarr has been released, and remembers the answer. Wondarr runs in a
/// container and never updates itself: this only finds out, so the UI can say so and a notification can
/// be sent.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>The last answer, with the live <c>update.check_enabled</c> setting applied. Never makes a request.</summary>
    UpdateStatus GetStatus();

    /// <summary>
    /// Checks GitHub now. Never throws for a network or GitHub failure: that becomes
    /// <see cref="UpdateStatus.LastError"/>.
    /// </summary>
    /// <param name="manual">
    /// Whether a person asked for it. A manual check within <see cref="UpdateCheckService.ManualReuseWindow"/>
    /// of the last successful one returns that one instead of asking again.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<UpdateStatus> CheckAsync(bool manual, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class UpdateCheckService : IUpdateCheckService, IDisposable
{
    /// <summary>The GitHub repository releases are read from.</summary>
    public const string Repository = "wulfftech/wondarr";

    /// <summary>The page of the project, used in the User-Agent.</summary>
    public const string ProjectUrl = "https://github.com/" + Repository;

    /// <summary>The releases endpoint.</summary>
    public const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";

    /// <summary>The name of the <see cref="HttpClient"/> the check reads through.</summary>
    public const string ClientName = "github-releases";

    /// <summary>The <c>setting</c> key that remembers the last version announced as a notification.</summary>
    public const string LastAnnouncedKey = "update.last_announced_version";

    /// <summary>The longest the release notes are kept.</summary>
    public const int MaxReleaseNotesLength = 20_000;

    /// <summary>How long one request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A manual check this soon after a successful one returns that one.</summary>
    public static readonly TimeSpan ManualReuseWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long to stay away from GitHub when it limits us without saying until when.</summary>
    private static readonly TimeSpan DefaultBlock = TimeSpan.FromHours(1);

    /// <summary>The longest a limit is believed for.</summary>
    private static readonly TimeSpan MaxBlock = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<UpdateOptions> _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly IEventAggregator _events;
    private readonly TimeProvider _time;
    private readonly RunningVersion _running;
    private readonly ILogger<UpdateCheckService> _logger;

    private readonly object _stateLock = new();
    private IReadOnlyList<Release>? _releases;
    private string? _etag;
    private DateTimeOffset? _checkedAt;
    private string? _lastError;
    private DateTimeOffset? _blockedUntil;

    /// <summary>Initialises a new instance of the <see cref="UpdateCheckService"/> class.</summary>
    /// <param name="clients">Hands out the GitHub client.</param>
    /// <param name="options">The live <c>update</c> settings.</param>
    /// <param name="scopes">Creates the scope the last announced version is read and written in.</param>
    /// <param name="events">Publishes the check's outcome.</param>
    /// <param name="time">The clock.</param>
    /// <param name="running">The version of this build.</param>
    /// <param name="logger">The log sink.</param>
    public UpdateCheckService(
        IHttpClientFactory clients,
        IOptionsMonitor<UpdateOptions> options,
        IServiceScopeFactory scopes,
        IEventAggregator events,
        TimeProvider time,
        RunningVersion running,
        ILogger<UpdateCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _scopes = scopes;
        _events = events;
        _time = time;
        _running = running;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public UpdateStatus GetStatus()
    {
        var enabled = _options.CurrentValue.CheckEnabled;

        lock (_stateLock)
        {
            var latest = _releases is null ? null : Latest(_releases);
            var available = enabled
                && !_running.IsDevelopmentBuild
                && latest is not null
                && _running.Parsed is { } current
                && latest.Version > current;

            return new UpdateStatus(
                _running.Text,
                _running.IsDevelopmentBuild,
                latest?.Version.ToString(),
                available,
                latest?.Name,
                latest?.Url,
                latest?.Notes,
                latest?.PublishedAt,
                _checkedAt,
                enabled ? _lastError : null,
                enabled);
        }
    }

    /// <inheritdoc />
    public async Task<UpdateStatus> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.CheckEnabled)
        {
            return GetStatus();
        }

        bool ran;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ran = await RunAsync(manual, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var status = GetStatus();

        if (ran)
        {
            await AnnounceAsync(status, cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(new UpdateCheckedEvent(status), cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>
    /// The newest release that counts for this build: a stable build hears about stable releases only, a
    /// pre-release build about both (so an <c>rc</c> user hears of the next <c>rc</c> and of the release).
    /// </summary>
    private Release? Latest(IReadOnlyList<Release> releases)
    {
        var includePreReleases = _running.Parsed is { IsPreRelease: true } && !_running.IsDevelopmentBuild;

        Release? best = null;

        foreach (var release in releases)
        {
            if (release.IsPreRelease && !includePreReleases)
            {
                continue;
            }

            if (best is null || release.Version > best.Version)
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>Makes one request, unless GitHub asked us to wait. Returns whether anything was asked.</summary>
    private async Task<bool> RunAsync(bool manual, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        lock (_stateLock)
        {
            if (manual && _checkedAt is { } last && _lastError is null && now - last < ManualReuseWindow)
            {
                return false;
            }

            if (_blockedUntil is { } blocked)
            {
                if (now < blocked)
                {
                    _lastError = RateLimitMessage(blocked);

                    return false;
                }

                _blockedUntil = null;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            var client = _clients.CreateClient(ClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);

            string? etag;
            lock (_stateLock)
            {
                etag = _releases is null ? null : _etag;
            }

            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            await HandleAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Fail($"GitHub did not answer within {(int)RequestTimeout.TotalSeconds} seconds.");
        }
        catch (HttpRequestException exception)
        {
            Fail($"GitHub could not be reached ({exception.HttpRequestError.ToString().ToLowerInvariant()}).");
        }
        catch (JsonException)
        {
            Fail("GitHub answered something Wondarr could not read.");
        }

        return true;
    }

    private async Task HandleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            lock (_stateLock)
            {
                if (_releases is not null)
                {
                    _checkedAt = now;
                    _lastError = null;

                    return;
                }
            }

            Fail("GitHub answered that nothing changed, but Wondarr has no earlier answer.");

            return;
        }

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releases = ParseReleases(body);
            var etag = response.Headers.ETag?.ToString();

            lock (_stateLock)
            {
                _releases = releases;
                _etag = etag;
                _checkedAt = now;
                _lastError = null;
            }

            return;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && LimitEnd(response, now) is { } until)
        {
            lock (_stateLock)
            {
                _blockedUntil = until;
            }

            Fail(RateLimitMessage(until));

            return;
        }

        Fail($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + ".");
    }

    /// <summary>When GitHub says we may ask again, or <see langword="null"/> when this is not a rate limit.</summary>
    private static DateTimeOffset? LimitEnd(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter is not null)
        {
            var wait = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - now : (TimeSpan?)null);

            if (wait is { } value && value > TimeSpan.Zero)
            {
                return now + (value > MaxBlock ? MaxBlock : value);
            }

            return now + DefaultBlock;
        }

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && string.Equals(remaining.FirstOrDefault()?.Trim(), "0", StringComparison.Ordinal))
        {
            if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(seconds);

                return at > now ? (at - now > MaxBlock ? now + MaxBlock : at) : now + TimeSpan.FromMinutes(1);
            }

            return now + DefaultBlock;
        }

        return null;
    }

    private string RateLimitMessage(DateTimeOffset until)
    {
        var local = TimeZoneInfo.ConvertTime(until, _time.LocalTimeZone);

        return $"GitHub's rate limit; next check after {local.ToString("HH:mm", CultureInfo.InvariantCulture)}.";
    }

    private void Fail(string message)
    {
        bool changed;

        lock (_stateLock)
        {
            changed = !string.Equals(_lastError, message, StringComparison.Ordinal);
            _lastError = message;
        }

        // Once per distinct problem: a check that keeps failing the same way is not worth a line each time.
        if (changed)
        {
            LogCheckFailed(_logger, message);
        }
    }

    private static List<Release> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The releases list is not an array.");
        }

        var releases = new List<Release>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || GetBool(element, "draft"))
            {
                continue;
            }

            var version = SemanticVersion.TryParse(GetString(element, "tag_name"));

            if (version is null)
            {
                continue;
            }

            var notes = GetString(element, "body");

            if (notes is { Length: > MaxReleaseNotesLength })
            {
                notes = notes[..MaxReleaseNotesLength];
            }

            DateTimeOffset? published = element.TryGetProperty("published_at", out var at)
                && at.ValueKind == JsonValueKind.String
                && at.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : null;

            var url = GetString(element, "html_url");

            releases.Add(new Release(
                version,
                GetString(element, "name") is { Length: > 0 } name ? name : version.ToString(),
                url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                    ? url
                    : string.Concat(ProjectUrl, "/releases"),
                notes,
                published,
                GetBool(element, "prerelease") || version.IsPreRelease));
        }

        return releases;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Tells the user (once per version) that an update exists.</summary>
    private async Task AnnounceAsync(UpdateStatus status, CancellationToken cancellationToken)
    {
        if (!status.UpdateAvailable || status.LatestVersion is null)
        {
            return;
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();

            var last = await settings.GetAsync<string>(LastAnnouncedKey, cancellationToken).ConfigureAwait(false);

            if (string.Equals(last, status.LatestVersion, StringComparison.Ordinal))
            {
                return;
            }

            // Remembered before it is sent: a restart must not repeat it, and a send that fails is not retried.
            await settings.SetAsync(LastAnnouncedKey, status.LatestVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAnnounceFailed(_logger, exception.GetType().Name);

            return;
        }

        await _events
            .PublishAsync(
                new UpdateAvailableEvent(status.CurrentVersion, status.LatestVersion, status.ReleaseUrl),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The update check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The new version could not be recorded as announced ({Reason}); no notification was sent")]
    private static partial void LogAnnounceFailed(ILogger logger, string reason);

    private sealed record Release(
        SemanticVersion Version,
        string Name,
        string? Url,
        string? Notes,
        DateTimeOffset? PublishedAt,
        bool IsPreRelease);
}
