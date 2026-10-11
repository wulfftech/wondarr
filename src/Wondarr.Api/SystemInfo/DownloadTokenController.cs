using Wondarr.Api.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.SystemInfo;

/// <summary>
/// Mints the short-lived token a download link carries in place of the API key, so the key never
/// appears in a URL the UI builds (owner question 2, code review 2026-10-10).
/// </summary>
[ApiController]
[Route("api/v1/system/download-token")]
public sealed class DownloadTokenController : ControllerBase
{
    private readonly DownloadTokenService _tokens;

    /// <summary>Initialises a new instance of the <see cref="DownloadTokenController"/> class.</summary>
    /// <param name="tokens">Mints and checks the tokens.</param>
    public DownloadTokenController(DownloadTokenService tokens) => _tokens = tokens;

    /// <summary>Mints a token for one download path, good for 60 seconds.</summary>
    /// <param name="request">The path to download, without the URL base.</param>
    /// <returns>The token, or 400 problem details when the path is not a backup or log file download.</returns>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType<DownloadTokenResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public IActionResult Create([FromBody] DownloadTokenRequest request)
    {
        if (!DownloadTokenService.IsDownloadPath(request?.Path))
        {
            ModelState.AddModelError("path", "path must be a backup download or a log file download");

            return ValidationProblem(ModelState);
        }

        return Ok(new DownloadTokenResource(_tokens.Mint(request!.Path!), (int)DownloadTokenService.Lifetime.TotalSeconds));
    }
}

/// <summary>The <c>POST /api/v1/system/download-token</c> body.</summary>
/// <param name="Path">The API path the token is for, such as <c>/api/v1/system/backup/{id}/download</c>.</param>
public sealed record DownloadTokenRequest(string? Path);

/// <summary>A download token.</summary>
/// <param name="Token">Send as <c>?token=</c> on a GET of exactly the requested path.</param>
/// <param name="ExpiresInSeconds">How long the token stays valid.</param>
public sealed record DownloadTokenResource(string Token, int ExpiresInSeconds);
