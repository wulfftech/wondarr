using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Feedback;

namespace Wondarr.Api.Feedback;

/// <summary>
/// The feedback button in the header. Each submission is written to the log, then raised as an issue on
/// the Wondarr repository: directly when <c>feedback.github_token</c> is set, otherwise as a prefilled
/// GitHub issue form the person submits themselves. The header shows the button only once a token is set,
/// on the Contribute with feedback card of Settings &gt; General (the <c>settings</c> endpoints). The rules
/// live in <see cref="IFeedbackService"/> and <see cref="IFeedbackSettingsService"/>; no response carries
/// the token.
/// </summary>
[ApiController]
[Route("api/v1/feedback")]
public sealed class FeedbackController : ControllerBase
{
    private readonly IFeedbackService _feedback;
    private readonly IFeedbackSettingsService _settings;

    /// <summary>Initialises a new instance of the <see cref="FeedbackController"/> class.</summary>
    /// <param name="feedback">Logs and raises the feedback.</param>
    /// <param name="settings">Reads, writes and tests the token.</param>
    public FeedbackController(IFeedbackService feedback, IFeedbackSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(settings);

        _feedback = feedback;
        _settings = settings;
    }

    /// <summary>Reports whether a submission opens the issue directly. Makes no request.</summary>
    [HttpGet]
    [Produces("application/json")]
    public ActionResult<FeedbackStatusResource> GetStatus() => Ok(FeedbackStatusResource.From(_feedback.GetStatus()));

    /// <summary>
    /// Logs the feedback and raises it. When GitHub cannot be used (no token, a refused token, its rate
    /// limit, no network) the answer is still a 200, with the prefilled issue form and the reason.
    /// </summary>
    /// <param name="resource">The feedback.</param>
    /// <param name="cancellationToken">Cancels the request to GitHub.</param>
    /// <returns>200 with where the feedback went, or 400 when the text is blank or too long.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<FeedbackResultResource>> Submit(
        [FromBody] FeedbackCreateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var text = resource.Text?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            ModelState.AddModelError("text", "Say what went wrong or what you would like.");
        }
        else if (text.Length > FeedbackService.MaxTextLength)
        {
            ModelState.AddModelError("text", $"Keep it to {FeedbackService.MaxTextLength} characters.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var outcome = await _feedback
            .SubmitAsync(new FeedbackSubmission(resource.Kind, text!, resource.Page, resource.PageTitle), cancellationToken)
            .ConfigureAwait(false);

        return Ok(FeedbackResultResource.From(outcome));
    }

    /// <summary>Reports whether a token is set and whether the environment owns it. Never returns the token.</summary>
    [HttpGet("settings")]
    [Produces("application/json")]
    public ActionResult<FeedbackSettingsResource> GetSettings() => Ok(FeedbackSettingsResource.From(_settings.Current()));

    /// <summary>
    /// Stores or removes the token. An absent token is left alone and an empty one removes it; the file is
    /// written and reloaded, so the header's button follows without a restart.
    /// </summary>
    /// <param name="resource">The change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>200 with the settings as they are now stored, or 400 with the reason.</returns>
    [HttpPut("settings")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<FeedbackSettingsResource>> UpdateSettings(
        [FromBody] FeedbackSettingsUpdateResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = await _settings.UpdateAsync(resource.Token, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("token", error);
            }

            return ValidationProblem(ModelState);
        }

        return Ok(FeedbackSettingsResource.From(_settings.Current()));
    }

    /// <summary>
    /// Asks GitHub about the repository with the typed token, or the stored one when none is sent: whether
    /// GitHub accepts it and, for a classic token, whether it has the scope that opens issues. Opens nothing.
    /// Always 200: whether the token works is the answer, not the status code.
    /// </summary>
    /// <param name="request">The token to try, if any.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("settings/test")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<FeedbackTokenTestResource>> TestToken(
        [FromBody] FeedbackTokenTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _settings.TestAsync(request.Token, cancellationToken).ConfigureAwait(false);

        return Ok(new FeedbackTokenTestResource(result.Ok, result.Message));
    }
}
