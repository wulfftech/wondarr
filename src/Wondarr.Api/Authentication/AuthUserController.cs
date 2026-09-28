using Wondarr.Core.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Authentication;

/// <summary>The local user the web UI logs in as. Authenticated with the API key, like the rest of the API.</summary>
[ApiController]
[Route("api/v1/auth/user")]
public sealed class AuthUserController : ControllerBase
{
    private const int MaxUsernameLength = 64;
    private const int MinPasswordLength = 8;

    private readonly ICredentialStore _credentials;

    /// <summary>Initialises a new instance of the <see cref="AuthUserController"/> class.</summary>
    public AuthUserController(ICredentialStore credentials) => _credentials = credentials;

    /// <summary>Reports whether credentials exist, and the username if they do.</summary>
    [HttpGet]
    public async Task<ActionResult<AuthUserResource>> Get(CancellationToken cancellationToken)
    {
        var username = await _credentials.GetUsernameAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new AuthUserResource(username is not null, username));
    }

    /// <summary>Creates or replaces the credentials.</summary>
    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpdateAuthUserResource resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resource.Username) || resource.Username.Length > MaxUsernameLength)
        {
            return ValidationProblem(
                "username",
                $"username must be between 1 and {MaxUsernameLength} characters");
        }

        if (resource.Password is null || resource.Password.Length < MinPasswordLength)
        {
            return ValidationProblem(
                "password",
                $"password must be at least {MinPasswordLength} characters");
        }

        await _credentials.SetAsync(resource.Username, resource.Password, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    private ActionResult ValidationProblem(string field, string message)
    {
        ModelState.AddModelError(field, message);

        return ValidationProblem(ModelState);
    }
}

/// <summary>The <c>GET /api/v1/auth/user</c> body.</summary>
/// <param name="Configured">Whether a username and password have been set.</param>
/// <param name="Username">The configured username, or <see langword="null"/>.</param>
public sealed record AuthUserResource(bool Configured, string? Username);

/// <summary>The <c>PUT /api/v1/auth/user</c> body.</summary>
/// <param name="Username">The username to store, 1 to 64 characters.</param>
/// <param name="Password">The password to store, at least 8 characters.</param>
public sealed record UpdateAuthUserResource(string? Username, string? Password);
