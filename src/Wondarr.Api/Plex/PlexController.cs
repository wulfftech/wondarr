using Wondarr.Core.Plex;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Plex;

/// <summary>
/// The Plex settings page's backend: the PIN sign-in, the account's servers, the selected server and
/// its music sections. All the rules live in <see cref="IPlexConnectionService"/>; this controller
/// only maps resources and turns a refusal into an RFC 7807 problem.
/// <para>
/// No action returns a token: the core never hands one out except to the tasks that call the server.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/plex")]
public sealed class PlexController : ControllerBase
{
    /// <summary>What the UI is told when Plex refuses the stored sign-in.</summary>
    public const string RejectedSignIn = "Plex rejected the stored sign-in; sign in again";

    private const string NotAcceptedToken = "Plex did not accept that token";

    private readonly IPlexConnectionService _plex;

    /// <summary>Initialises a new instance of the <see cref="PlexController"/> class.</summary>
    /// <param name="plex">The connection service that owns the sign-in and the selected server.</param>
    public PlexController(IPlexConnectionService plex)
    {
        ArgumentNullException.ThrowIfNull(plex);

        _plex = plex;
    }

    /// <summary>Reads what the UI shows about the connection.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PlexStateResource>> GetState(CancellationToken cancellationToken)
    {
        var state = await _plex.GetStateAsync(cancellationToken).ConfigureAwait(false);

        return Ok(PlexStateResource.From(state));
    }

    /// <summary>Starts the sign-in: creates the PIN the user approves on plex.tv.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("pin")]
    [Produces("application/json")]
    public async Task<ActionResult<PlexPinResource>> CreatePin(CancellationToken cancellationToken)
    {
        try
        {
            var pin = await _plex.StartSignInAsync(cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(GetPinStatus), new { id = pin.Id }, PlexPinResource.From(pin));
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>
    /// Polls a sign-in PIN. A status with neither flag set means the user has not approved the code
    /// yet, so the UI keeps polling.
    /// </summary>
    /// <param name="id">The PIN id <c>POST api/v1/plex/pin</c> returned.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("pin/{id:long}")]
    [Produces("application/json")]
    public async Task<ActionResult<PlexPinStatusResource>> GetPinStatus(long id, CancellationToken cancellationToken)
    {
        try
        {
            // Authorized is this PIN's own answer: an install already signed in still sees a new,
            // pending PIN as pending.
            var status = await _plex.CompleteSignInAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(new PlexPinStatusResource(Authorized: status.Authorized, Expired: status.Expired));
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>Stores a token the user pasted, after plex.tv has accepted it.</summary>
    /// <param name="resource">The token. It is never echoed back.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpPut("token")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> SetToken(
        [FromBody] PlexTokenResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            await _plex.SetTokenAsync(resource.Token, cancellationToken).ConfigureAwait(false);

            return NoContent();
        }
        catch (PlexUnauthorizedException)
        {
            // The token itself is what the message would have to repeat, so the problem says only
            // what happened.
            return Problem(
                title: "Plex rejected the token",
                detail: NotAcceptedToken,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Invalid token",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>Lists the Plex Media Servers the signed-in account can reach.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("servers")]
    [Produces("application/json")]
    public async Task<ActionResult<List<PlexServerResource>>> GetServers(CancellationToken cancellationToken)
    {
        try
        {
            var servers = await _plex.GetServersAsync(cancellationToken).ConfigureAwait(false);

            return Ok(servers.Select(PlexServerResource.From).ToList());
        }
        catch (InvalidOperationException exception)
        {
            return NotReady(exception);
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>Selects the server Wondarr talks to, and reads its identity to confirm it is reachable.</summary>
    /// <param name="resource">The server URL to select.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("server")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<PlexStateResource>> SelectServer(
        [FromBody] PlexServerResourceUpdate resource,
        CancellationToken cancellationToken)
    {
        try
        {
            await _plex.SelectServerAsync(resource.ServerUrl, cancellationToken).ConfigureAwait(false);

            var state = await _plex.GetStateAsync(cancellationToken).ConfigureAwait(false);

            return Ok(PlexStateResource.From(state));
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Invalid server URL",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException exception)
        {
            return NotReady(exception);
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>
    /// Selects one of the account's servers by name: every connection plex.tv lists for it is tried,
    /// and the best one that answers as that server is kept.
    /// </summary>
    /// <param name="resource">The server to connect to.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("server/connect")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<ActionResult<PlexStateResource>> ConnectServer(
        [FromBody] PlexServerConnectResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            await _plex.ConnectServerAsync(resource.MachineIdentifier, cancellationToken).ConfigureAwait(false);

            var state = await _plex.GetStateAsync(cancellationToken).ConfigureAwait(false);

            return Ok(PlexStateResource.From(state));
        }
        catch (ArgumentException exception)
        {
            return Problem(
                title: "Unknown Plex server",
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException exception)
        {
            return NotReady(exception);
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>
    /// Tests the selected server. Always 200: whether the connection works is the answer, not the
    /// status code.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost("test")]
    [Produces("application/json")]
    public async Task<ActionResult<PlexTestResource>> Test(CancellationToken cancellationToken)
    {
        var result = await _plex.TestAsync(cancellationToken).ConfigureAwait(false);

        return Ok(PlexTestResource.From(result));
    }

    /// <summary>Lists the music sections of the selected server.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("sections")]
    [Produces("application/json")]
    public async Task<ActionResult<List<PlexSectionResource>>> GetSections(CancellationToken cancellationToken)
    {
        try
        {
            var sections = await _plex.GetMusicSectionsAsync(cancellationToken).ConfigureAwait(false);

            return Ok(sections.Select(PlexSectionResource.From).ToList());
        }
        catch (InvalidOperationException exception)
        {
            return NotReady(exception);
        }
        catch (PlexException exception)
        {
            return PlexFailure(exception);
        }
    }

    /// <summary>Signs out. The selected server is kept, so the UI can still show what was chosen.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    [HttpDelete]
    public async Task<IActionResult> SignOut(CancellationToken cancellationToken)
    {
        await _plex.SignOutAsync(cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Turns a core failure into a problem. Plex refusing the credentials is a conflict the user
    /// resolves by signing in again; everything else is a bad gateway whose detail is the
    /// exception's message, which the core guarantees holds no token.
    /// </summary>
    private ObjectResult PlexFailure(PlexException exception) =>
        exception is PlexUnauthorizedException
            ? Problem(
                title: "Plex rejected the sign-in",
                detail: RejectedSignIn,
                statusCode: StatusCodes.Status409Conflict)
            : Problem(
                title: "Plex is unreachable",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway);

    /// <summary>
    /// The core refuses with <see cref="InvalidOperationException"/> when the install is not signed in
    /// or no server is selected. That is the current state of the connection, not a failure of this
    /// request, so it travels as a conflict carrying the service's own words.
    /// </summary>
    private ObjectResult NotReady(InvalidOperationException exception) =>
        Problem(
            title: "Plex is not connected",
            detail: exception.Message,
            statusCode: StatusCodes.Status409Conflict);
}
