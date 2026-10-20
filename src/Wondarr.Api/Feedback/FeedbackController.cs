using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Feedback;

namespace Wondarr.Api.Feedback;

/// <summary>
/// The feedback button in the header. Each submission is written to the log, then raised as an issue on
/// the Wondarr repository: directly when <c>feedback.github_token</c> is set, otherwise as a prefilled
/// GitHub issue form the person submits themselves. The rules live in <see cref="IFeedbackService"/>.
/// </summary>
[ApiController]
[Route("api/v1/feedback")]
public sealed class FeedbackController : ControllerBase
{
    private readonly IFeedbackService _feedback;

    /// <summary>Initialises a new instance of the <see cref="FeedbackController"/> class.</summary>
    /// <param name="feedback">Logs and raises the feedback.</param>
    public FeedbackController(IFeedbackService feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        _feedback = feedback;
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
}
