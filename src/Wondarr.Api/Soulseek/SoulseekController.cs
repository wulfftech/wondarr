using System.Text.Json;
using Wondarr.Sources.Slskd;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Soulseek;

/// <summary>
/// The Soulseek settings page's backend: what the bundled slskd is configured with, what it is
/// doing, and how much of the search allowance is left. All the rules live in
/// <see cref="ISoulseekSettingsService"/>; this controller only maps resources and turns a refusal
/// into an RFC 7807 validation problem.
/// </summary>
[ApiController]
[Route("api/v1/soulseek")]
public sealed class SoulseekController : ControllerBase
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ISoulseekSettingsService _settings;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly SlskdStatus _status;
    private readonly ISoulseekSearchBudget _budget;

    /// <summary>Initialises a new instance of the <see cref="SoulseekController"/> class.</summary>
    /// <param name="settings">Reads and writes the Soulseek settings.</param>
    /// <param name="options">The live options, for the mode and the sharing flags.</param>
    /// <param name="status">What the supervisor last observed about the bundled slskd.</param>
    /// <param name="budget">The search allowance every submitted search passes through.</param>
    public SoulseekController(
        ISoulseekSettingsService settings,
        IOptionsMonitor<SoulseekOptions> options,
        SlskdStatus status,
        ISoulseekSearchBudget budget)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(budget);

        _settings = settings;
        _options = options;
        _status = status;
        _budget = budget;
    }

    /// <summary>Reads the Soulseek settings.</summary>
    [HttpGet("settings")]
    [Produces("application/json")]
    public ActionResult<SoulseekSettingsResource> GetSettings() =>
        Ok(SoulseekSettingsResource.From(_settings.Get()));

    /// <summary>
    /// Changes the Soulseek settings. Only the fields present in the body are changed; the file is
    /// written and reloaded, and the supervisor restarts slskd by itself when it has to.
    /// </summary>
    /// <param name="resource">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the stored settings and whether slskd restarts, or 400 with the reasons.</returns>
    [HttpPut("settings")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SoulseekSettingsUpdateResponseResource>> UpdateSettings(
        [FromBody] SoulseekSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings
            .UpdateAsync(resource.ToUpdate(), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            // One key for every reason: a validation failure names its own config.yml key, and a
            // read-only field names the environment variable that owns it, so the messages are what
            // the UI shows verbatim.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("settings", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(new SoulseekSettingsUpdateResponseResource(
            SoulseekSettingsResource.From(_settings.Get()),
            result.RestartsSlskd,
            result.RestartsWondarr));
    }

    /// <summary>
    /// Tries a user's own slskd: <c>GET /api/v0/application</c> with the given (or the stored) API key.
    /// Redirects are not followed, so the key never travels to another host; the answer names the
    /// host only, never the key.
    /// </summary>
    /// <param name="resource">The URL to try, and the key (or none for the stored one).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<SoulseekConnectionTestResultResource>> TestConnection(
        [FromBody] SoulseekConnectionTestResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!Uri.TryCreate(resource.Url?.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(url.UserInfo))
        {
            return Ok(new SoulseekConnectionTestResultResource(false, null, false, "The URL must be an absolute http or https address"));
        }

        var key = string.IsNullOrEmpty(resource.ApiKey) ? _options.CurrentValue.External.ApiKey : resource.ApiKey;

        if (string.IsNullOrEmpty(key))
        {
            return Ok(new SoulseekConnectionTestResultResource(false, null, false, "No API key: enter the one from your slskd's settings"));
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), "api/v0/application"));
        request.Headers.Add(SlskdClient.ApiKeyHeader, key);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return Ok(new SoulseekConnectionTestResultResource(false, null, false, "slskd refused the API key"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new SoulseekConnectionTestResultResource(false, null, false, $"slskd at {url.Host} answered {(int)response.StatusCode}"));
            }

            var state = await JsonSerializer
                .DeserializeAsync<SlskdApplicationState>(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    WebJson,
                    cancellationToken)
                .ConfigureAwait(false) ?? new SlskdApplicationState();

            var version = string.IsNullOrWhiteSpace(state.Version.Current) ? null : state.Version.Current;
            var loggedIn = state.Server.IsLoggedIn;

            return Ok(new SoulseekConnectionTestResultResource(
                true,
                version,
                loggedIn,
                loggedIn
                    ? $"Connected to slskd {version} at {url.Host}, logged in to Soulseek"
                    : $"Connected to slskd {version} at {url.Host}, but it is not logged in to Soulseek"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Ok(new SoulseekConnectionTestResultResource(false, null, false, $"slskd at {url.Host} did not answer"));
        }
    }

    /// <summary>Reports whether Soulseek is logged in, what is shared and how much of the allowance is left.</summary>
    [HttpGet("status")]
    [Produces("application/json")]
    public ActionResult<SoulseekStatusResource> GetStatus()
    {
        var options = _options.CurrentValue;
        var snapshot = _status.Current;
        var budget = _budget.Snapshot();

        return Ok(new SoulseekStatusResource(
            options.Mode == SoulseekMode.External ? "external" : "bundled",
            JsonNamingPolicy.CamelCase.ConvertName(snapshot.State.ToString()),
            snapshot.Version,
            snapshot.IsLoggedIn,
            snapshot.SoulseekUsername,

            // "None" and "no problem has been seen yet" both mean nothing to report.
            snapshot.LoginProblem is null or SlskdLoginProblem.None
                ? null
                : JsonNamingPolicy.CamelCase.ConvertName(snapshot.LoginProblem.Value.ToString()),
            snapshot.LastError,
            snapshot.PendingRestart,
            new SoulseekSharingResource(
                options.ShareLibrary,
                [.. options.SharedFolders],
                snapshot.SharedDirectories,
                snapshot.SharedFiles),
            new SoulseekSearchBudgetResource(
                budget.SubmittedInWindow,
                options.Search.MaxSearches,
                budget.Outstanding,
                options.Search.MaxOutstanding,
                budget.NextAllowedAt)));
    }
}
