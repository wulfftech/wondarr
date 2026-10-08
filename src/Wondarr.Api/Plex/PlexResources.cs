using Wondarr.Core.Plex;

namespace Wondarr.Api.Plex;

/// <summary>
/// The Plex connection as the Settings UI reads it. No field carries a token: the sign-in lives on
/// the server and only <see cref="SignedIn"/> says whether there is one.
/// </summary>
/// <param name="SignedIn">Whether an account token is stored.</param>
/// <param name="ServerUrl">The selected server's URL, once one was chosen.</param>
/// <param name="ServerName">The selected server's friendly name.</param>
/// <param name="MachineIdentifier">The selected server's machine identifier.</param>
/// <param name="ClientIdentifier">The identifier this install signs in with, which builds the auth URL.</param>
public sealed record PlexStateResource(
    bool SignedIn,
    string? ServerUrl,
    string? ServerName,
    string? MachineIdentifier,
    string ClientIdentifier)
{
    /// <summary>Maps the connection service's state onto the resource.</summary>
    /// <param name="state">The state the service reports.</param>
    public static PlexStateResource From(PlexConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new PlexStateResource(
            state.SignedIn,
            state.ServerUrl,
            state.ServerName,
            state.MachineIdentifier,
            state.ClientIdentifier);
    }
}

/// <summary>A sign-in PIN: the code the user types on plex.tv, and the page that carries it there.</summary>
/// <param name="Id">The PIN id the UI polls.</param>
/// <param name="Code">The code shown to the user.</param>
/// <param name="AuthUrl">The plex.tv page the user opens to approve the code.</param>
/// <param name="ExpiresAt">When plex.tv stops accepting the code.</param>
public sealed record PlexPinResource(long Id, string Code, string AuthUrl, DateTimeOffset ExpiresAt)
{
    /// <summary>Maps the service's PIN onto the resource.</summary>
    /// <param name="pin">The PIN plex.tv created.</param>
    public static PlexPinResource From(PlexPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        return new PlexPinResource(pin.Id, pin.Code, pin.AuthUrl, pin.ExpiresAt);
    }
}

/// <summary>
/// How a sign-in poll ended. A status with both flags false means the user has not approved the code
/// yet, so the UI keeps polling.
/// </summary>
/// <param name="Authorized">Whether a token arrived and was stored.</param>
/// <param name="Expired">Whether the PIN is past its expiry, or plex.tv no longer knows it.</param>
public sealed record PlexPinStatusResource(bool Authorized, bool Expired);

/// <summary>One of the ways a Plex Media Server can be reached.</summary>
/// <param name="Uri">The connection URL.</param>
/// <param name="Local">Whether plex.tv considers the connection local to the account.</param>
/// <param name="Relay">Whether the connection goes through Plex's relay.</param>
public sealed record PlexConnectionResource(string Uri, bool Local, bool Relay)
{
    /// <summary>Maps one of the client's connections onto the resource.</summary>
    /// <param name="connection">The connection plex.tv reported.</param>
    public static PlexConnectionResource From(PlexServerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new PlexConnectionResource(connection.Uri, connection.Local, connection.Relay);
    }
}

/// <summary>A Plex Media Server the signed-in account can reach. Carries no access token.</summary>
/// <param name="Name">The server's friendly name.</param>
/// <param name="MachineIdentifier">The server's stable machine identifier.</param>
/// <param name="Owned">Whether the signed-in account owns the server.</param>
/// <param name="ProductVersion">The Plex Media Server version, when plex.tv reports one.</param>
/// <param name="Connections">Every connection plex.tv knows for the server.</param>
public sealed record PlexServerResource(
    string Name,
    string MachineIdentifier,
    bool Owned,
    string? ProductVersion,
    List<PlexConnectionResource> Connections)
{
    /// <summary>Maps a server the connection service reported onto the resource.</summary>
    /// <param name="server">The server, with its access token already blanked.</param>
    public static PlexServerResource From(PlexServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return new PlexServerResource(
            server.Name,
            server.MachineIdentifier,
            server.Owned,
            server.ProductVersion,
            [.. server.Connections.Select(PlexConnectionResource.From)]);
    }
}

/// <summary>The outcome of a connection test. A failure is reported here rather than as a status code.</summary>
/// <param name="Ok">Whether the selected server answered.</param>
/// <param name="ServerName">The server's name, when it answered.</param>
/// <param name="Version">The server's version, when it answered.</param>
/// <param name="MusicSections">How many music sections the server has.</param>
/// <param name="Error">Why it did not answer, in words that carry no token.</param>
public sealed record PlexTestResource(bool Ok, string? ServerName, string? Version, int MusicSections, string? Error)
{
    /// <summary>Maps the connection service's test result onto the resource.</summary>
    /// <param name="result">The result the service reported.</param>
    public static PlexTestResource From(PlexTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PlexTestResource(result.Ok, result.ServerName, result.Version, result.MusicSections, result.Error);
    }
}

/// <summary>One music library on the selected server.</summary>
/// <param name="Key">The section key, which a later task refreshes and empties.</param>
/// <param name="Title">The section's title, for example "Music".</param>
/// <param name="Locations">The folders the section scans, as the server sees them.</param>
public sealed record PlexSectionResource(string Key, string Title, List<string> Locations)
{
    /// <summary>Maps a section onto the resource.</summary>
    /// <param name="section">The music section the server reported.</param>
    public static PlexSectionResource From(PlexSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return new PlexSectionResource(section.Key, section.Title, [.. section.Locations]);
    }
}

/// <summary>A token the user pasted, so an install without a browser can sign in.</summary>
/// <param name="Token">The account token. Never echoed back by any response.</param>
public sealed record PlexTokenResource(string Token);

/// <summary>The server the user picked from the list <c>GET api/v1/plex/servers</c> returned.</summary>
/// <param name="ServerUrl">The connection URL to reach the server on.</param>
public sealed record PlexServerResourceUpdate(string ServerUrl);

/// <summary>The body of <c>PUT /api/v1/plex/server/connect</c>: which of the account's servers to connect to.</summary>
/// <param name="MachineIdentifier">The server's machine identifier, as <c>GET /api/v1/plex/servers</c> lists it.</param>
public sealed record PlexServerConnectResource(string MachineIdentifier);
