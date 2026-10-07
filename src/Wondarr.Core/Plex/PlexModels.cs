namespace Wondarr.Core.Plex;

/// <summary>
/// A sign-in PIN: the code the user types on plex.tv, and the URL that carries it there.
/// </summary>
/// <param name="Id">The PIN id, polled until the user approves the code or the PIN expires.</param>
/// <param name="Code">The code shown to the user.</param>
/// <param name="ExpiresAt">When plex.tv stops accepting the code.</param>
/// <param name="AuthUrl">The page the user opens to approve the code.</param>
public sealed record PlexPin(long Id, string Code, DateTimeOffset ExpiresAt, string AuthUrl);

/// <summary>The state of a sign-in PIN: still pending, expired, or approved with a token.</summary>
/// <param name="Expired">Whether the PIN is past its expiry, or plex.tv no longer knows it.</param>
/// <param name="AuthToken">The account token, once the user has approved. Never leaves the service.</param>
public sealed record PlexPinStatus(bool Expired, string? AuthToken)
{
    /// <summary>
    /// Gets a value indicating whether this poll found the PIN approved and stored its token. It is
    /// what callers see instead of the token, and it says nothing about an earlier sign-in.
    /// </summary>
    public bool Authorized { get; init; }
}

/// <summary>One of the ways a Plex Media Server can be reached.</summary>
/// <param name="Uri">The connection URL, usually a <c>plex.direct</c> host.</param>
/// <param name="Local">Whether plex.tv considers the connection local to the user's network.</param>
/// <param name="Relay">Whether the connection goes through Plex's relay.</param>
/// <param name="Protocol">The scheme, <c>http</c> or <c>https</c>.</param>
/// <param name="Address">The host or IP the connection resolves to.</param>
/// <param name="Port">The port the server answers on.</param>
public sealed record PlexServerConnection(
    string Uri,
    bool Local,
    bool Relay,
    string Protocol,
    string Address,
    int Port);

/// <summary>
/// A Plex Media Server as plex.tv describes it for the signed-in account.
/// </summary>
/// <param name="Name">The server's friendly name.</param>
/// <param name="MachineIdentifier">The server's machine identifier, which <c>/identity</c> repeats.</param>
/// <param name="Owned">Whether the signed-in account owns the server.</param>
/// <param name="AccessToken">
/// The token to use against this server. Blanked before it is handed to anything outside this
/// namespace, and never returned by the API.
/// </param>
/// <param name="ProductVersion">The Plex Media Server version, when plex.tv reports one.</param>
/// <param name="Connections">Every connection plex.tv knows for the server.</param>
public sealed record PlexServer(
    string Name,
    string MachineIdentifier,
    bool Owned,
    string? AccessToken,
    string? ProductVersion,
    IReadOnlyList<PlexServerConnection> Connections);

/// <summary>What a Plex Media Server says about itself at <c>/identity</c>.</summary>
/// <param name="MachineIdentifier">The server's stable machine identifier.</param>
/// <param name="Version">The Plex Media Server version.</param>
/// <param name="Claimed">Whether the server is signed in to a Plex account.</param>
public sealed record PlexIdentity(string MachineIdentifier, string Version, bool Claimed);

/// <summary>One library on a Plex Media Server.</summary>
/// <param name="Key">The section key, which the refresh and empty-trash calls use. Plex sends it as a string.</param>
/// <param name="Title">The section's title, for example "Music".</param>
/// <param name="Type">The section type; <c>artist</c> is a music library.</param>
/// <param name="Refreshing">Whether a scan of this section is running.</param>
/// <param name="Locations">The folders this section scans, as the server sees them.</param>
public sealed record PlexSection(
    string Key,
    string Title,
    string Type,
    bool Refreshing,
    IReadOnlyList<string> Locations);

/// <summary>
/// A Plex request failed. The message describes the failure without the token, which must never
/// reach a log line, an API response or a thrown exception.
/// </summary>
public class PlexException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="PlexException"/> class.</summary>
    /// <param name="message">A description that carries no token.</param>
    public PlexException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="PlexException"/> class.</summary>
    /// <param name="message">A description that carries no token.</param>
    /// <param name="innerException">The transport failure behind this one.</param>
    public PlexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Plex refused the request's credentials with HTTP 401 or 403.</summary>
public sealed class PlexUnauthorizedException : PlexException
{
    /// <summary>Initialises a new instance of the <see cref="PlexUnauthorizedException"/> class.</summary>
    /// <param name="message">A description that carries no token.</param>
    public PlexUnauthorizedException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="PlexUnauthorizedException"/> class.</summary>
    /// <param name="message">A description that carries no token.</param>
    /// <param name="innerException">The transport failure behind this one.</param>
    public PlexUnauthorizedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One track of a Plex music section, with the files it is made of (as the server sees them).</summary>
/// <param name="RatingKey">The track's rating key.</param>
/// <param name="Title">The track title.</param>
/// <param name="Files">The paths of its media parts.</param>
public sealed record PlexTrack(string RatingKey, string Title, IReadOnlyList<string> Files);

/// <summary>One item of a Plex playlist.</summary>
/// <param name="RatingKey">The track's rating key.</param>
/// <param name="PlaylistItemId">The item's own id within the playlist (what remove and move name).</param>
public sealed record PlexPlaylistItem(string RatingKey, string PlaylistItemId);
