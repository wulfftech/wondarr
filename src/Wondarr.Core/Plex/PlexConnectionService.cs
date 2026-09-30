using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Plex;

/// <summary>
/// What Wondarr remembers about Plex, stored as one JSON object under the <c>plex</c> setting.
/// </summary>
public sealed record PlexConnectionSettings
{
    /// <summary>The identifier this install signs in with. Generated once and never changed.</summary>
    public string ClientIdentifier { get; init; } = string.Empty;

    /// <summary>The account token. A secret: never logged and never returned by the API.</summary>
    public string? Token { get; init; }

    /// <summary>The URL of the selected Plex Media Server, as the user gave it.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>The selected server's friendly name, for the UI.</summary>
    public string? ServerName { get; init; }

    /// <summary>The selected server's machine identifier.</summary>
    public string? MachineIdentifier { get; init; }

    /// <summary>The token to use against the selected server. A secret, like <see cref="Token"/>.</summary>
    public string? ServerToken { get; init; }
}

/// <summary>What the UI is told about the Plex connection. Carries no token.</summary>
/// <param name="SignedIn">Whether an account token is stored.</param>
/// <param name="ServerUrl">The selected server, when one was chosen.</param>
/// <param name="ServerName">The selected server's friendly name.</param>
/// <param name="MachineIdentifier">The selected server's machine identifier.</param>
/// <param name="ClientIdentifier">The identifier this install signs in with, so the UI can build the auth URL.</param>
public sealed record PlexConnectionState(
    bool SignedIn,
    string? ServerUrl,
    string? ServerName,
    string? MachineIdentifier,
    string ClientIdentifier);

/// <summary>The outcome of a connection test. A failure is reported here, never thrown.</summary>
/// <param name="Ok">Whether the selected server answered.</param>
/// <param name="ServerName">The server's name, when it answered.</param>
/// <param name="Version">The server's version, when it answered.</param>
/// <param name="MusicSections">How many music sections the server has.</param>
/// <param name="Error">Why it did not answer, in words that carry no token.</param>
public sealed record PlexTestResult(
    bool Ok,
    string? ServerName,
    string? Version,
    int MusicSections,
    string? Error);

/// <summary>Signs in to Plex, remembers the selection, and hands out what later tasks need.</summary>
public interface IPlexConnectionService
{
    /// <summary>Reads what the UI shows about the connection, without any token.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PlexConnectionState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Starts the sign-in: creates a PIN the user approves on plex.tv.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<PlexPin> StartSignInAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Polls the sign-in PIN and, once the user has approved it, stores the token.
    /// </summary>
    /// <param name="pinId">The PIN to poll.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The PIN's state, always with <see cref="PlexPinStatus.AuthToken"/> blanked.</returns>
    Task<PlexPinStatus> CompleteSignInAsync(long pinId, CancellationToken cancellationToken);

    /// <summary>Stores a token the user pasted, after plex.tv has accepted it.</summary>
    /// <param name="token">The token from the user.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="PlexUnauthorizedException">plex.tv refused the token.</exception>
    Task SetTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>Lists the account's servers, with every access token blanked.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<PlexServer>> GetServersAsync(CancellationToken cancellationToken);

    /// <summary>Selects a server, and remembers how to talk to it.</summary>
    /// <param name="serverUrl">The server URL, absolute and http or https.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The server's identity, read from the server itself.</returns>
    Task<PlexIdentity> SelectServerAsync(string serverUrl, CancellationToken cancellationToken);

    /// <summary>Tests the selected server. A Plex or network problem is reported, not thrown.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<PlexTestResult> TestAsync(CancellationToken cancellationToken);

    /// <summary>Lists the selected server's music sections.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<PlexSection>> GetMusicSectionsAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the account and server tokens. The selected server URL is kept for the UI.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SignOutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The selected server and the token to call it with, for the tasks that scan and compact. The
    /// only way a token leaves this service.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="null"/> when not signed in or no server is selected.</returns>
    Task<(Uri Server, string Token)?> GetServerContextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Plex connection: the PIN sign-in, the account's servers, the selected server, and the token
/// that never leaves this class except through <see cref="GetServerContextAsync"/>.
/// </summary>
public sealed partial class PlexConnectionService : IPlexConnectionService
{
    /// <summary>The settings key the connection is stored under.</summary>
    public const string SettingKey = "plex";

    /// <summary>What every method that needs a token says when there is none.</summary>
    public const string NotSignedIn = "Not signed in to Plex";

    /// <summary>What the methods that need a server say when none was selected.</summary>
    public const string NoServerSelected = "No Plex server selected";

    private const string MusicSectionType = "artist";

    private readonly ISettingsRepository _settings;
    private readonly IPlexTvClient _tv;
    private readonly IPlexServerClient _server;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<PlexConnectionService> _logger;

    /// <summary>Initialises a new instance of the <see cref="PlexConnectionService"/> class.</summary>
    /// <param name="settings">The settings table the connection lives in.</param>
    /// <param name="tv">The plex.tv client.</param>
    /// <param name="server">The Plex Media Server client.</param>
    /// <param name="secrets">The registry every token is handed to, so the log pipeline can redact it.</param>
    /// <param name="logger">The log.</param>
    public PlexConnectionService(
        ISettingsRepository settings,
        IPlexTvClient tv,
        IPlexServerClient server,
        ISecretRegistry secrets,
        ILogger<PlexConnectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tv);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _tv = tv;
        _server = server;
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlexConnectionState> GetStateAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return new PlexConnectionState(
            SignedIn: !string.IsNullOrEmpty(settings.Token),
            ServerUrl: settings.ServerUrl,
            ServerName: settings.ServerName,
            MachineIdentifier: settings.MachineIdentifier,
            ClientIdentifier: settings.ClientIdentifier);
    }

    /// <inheritdoc />
    public async Task<PlexPin> StartSignInAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return await _tv.CreatePinAsync(settings.ClientIdentifier, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlexPinStatus> CompleteSignInAsync(long pinId, CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

        var status = await _tv
            .CheckPinAsync(pinId, settings.ClientIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(status.AuthToken))
        {
            await SaveAsync(settings with { Token = status.AuthToken }, cancellationToken).ConfigureAwait(false);
            _secrets.Register(status.AuthToken);
            LogSignedIn(_logger);
        }

        // The token stays here: everything outside this class sees the status without it.
        return status with { AuthToken = null };
    }

    /// <inheritdoc />
    public async Task SetTokenAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = token.Trim();

        // plex.tv is the only authority on whether a token is good; a 401 becomes a
        // PlexUnauthorizedException here and nothing is stored.
        await _tv.GetServersAsync(trimmed, settings.ClientIdentifier, cancellationToken).ConfigureAwait(false);

        await SaveAsync(settings with { Token = trimmed }, cancellationToken).ConfigureAwait(false);
        _secrets.Register(trimmed);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlexServer>> GetServersAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var token = settings.Token;

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(NotSignedIn);
        }

        var servers = await _tv
            .GetServersAsync(token, settings.ClientIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // The per-server access tokens are for calling the server, not for showing in a list.
        return [.. servers.Select(server => server with { AccessToken = null })];
    }

    /// <inheritdoc />
    public async Task<PlexIdentity> SelectServerAsync(string serverUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "A Plex server URL must be absolute and use http or https.",
                nameof(serverUrl));
        }

        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var token = settings.Token;

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(NotSignedIn);
        }

        var identity = await _server.GetIdentityAsync(server, token, cancellationToken).ConfigureAwait(false);

        var resource = await FindServerAsync(identity.MachineIdentifier, token, settings, cancellationToken)
            .ConfigureAwait(false);

        // A server can hand out its own token, which is what to use against it from then on.
        var serverToken = resource?.AccessToken ?? token;
        var name = resource?.Name ?? identity.MachineIdentifier;

        await SaveAsync(
            settings with
            {
                ServerUrl = serverUrl.Trim(),
                MachineIdentifier = identity.MachineIdentifier,
                ServerName = name,
                ServerToken = serverToken,
            },
            cancellationToken).ConfigureAwait(false);

        _secrets.Register(serverToken);
        LogServerSelected(_logger, name);

        return identity;
    }

    /// <inheritdoc />
    public async Task<PlexTestResult> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(settings.Token))
            {
                return new PlexTestResult(false, null, null, 0, NotSignedIn);
            }

            if (!TryReadServerUrl(settings, out var server))
            {
                return new PlexTestResult(false, null, null, 0, NoServerSelected);
            }

            var token = TokenFor(settings)!;

            var identity = await _server.GetIdentityAsync(server, token, cancellationToken).ConfigureAwait(false);
            var sections = await _server.GetSectionsAsync(server, token, cancellationToken).ConfigureAwait(false);

            return new PlexTestResult(
                Ok: true,
                ServerName: string.IsNullOrEmpty(settings.ServerName) ? identity.MachineIdentifier : settings.ServerName,
                Version: identity.Version,
                MusicSections: sections.Count(section => IsMusic(section)),
                Error: null);
        }
        catch (PlexException exception)
        {
            // The one place a failure is an answer rather than an exception: the UI shows it.
            return new PlexTestResult(false, null, null, 0, exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlexSection>> GetMusicSectionsAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var token = TokenFor(settings);

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(NotSignedIn);
        }

        if (!TryReadServerUrl(settings, out var server))
        {
            throw new InvalidOperationException(NoServerSelected);
        }

        var sections = await _server
            .GetSectionsAsync(server, token, cancellationToken)
            .ConfigureAwait(false);

        return [.. sections.Where(IsMusic)];
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

        await SaveAsync(settings with { Token = null, ServerToken = null }, cancellationToken).ConfigureAwait(false);

        LogSignedOut(_logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in to Plex")]
    private static partial void LogSignedIn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Selected the Plex server {ServerName}")]
    private static partial void LogServerSelected(ILogger logger, string serverName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed out of Plex; the selected server is kept")]
    private static partial void LogSignedOut(ILogger logger);

    /// <inheritdoc />
    public async Task<(Uri Server, string Token)?> GetServerContextAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

        if (TokenFor(settings) is not { } token || !TryReadServerUrl(settings, out var server))
        {
            return null;
        }

        return (server, token);
    }

    /// <summary>Loads the connection, creating the client identifier on first use, and registers its secrets.</summary>
    private async Task<PlexConnectionSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings
            .GetAsync<PlexConnectionSettings>(SettingKey, cancellationToken)
            .ConfigureAwait(false);

        if (settings is null || string.IsNullOrWhiteSpace(settings.ClientIdentifier))
        {
            settings = (settings ?? new PlexConnectionSettings())
                with { ClientIdentifier = Guid.NewGuid().ToString("N") };

            await _settings.SetAsync(SettingKey, settings, cancellationToken).ConfigureAwait(false);
        }

        Register(settings);

        return settings;
    }

    private async Task SaveAsync(PlexConnectionSettings settings, CancellationToken cancellationToken)
    {
        Register(settings);

        await _settings.SetAsync(SettingKey, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands every token to the redactor as soon as it is read or written.</summary>
    private void Register(PlexConnectionSettings settings)
    {
        _secrets.Register(settings.Token);
        _secrets.Register(settings.ServerToken);
    }

    /// <summary>The resource whose machine identifier matches, when plex.tv recognises it.</summary>
    private async Task<PlexServer?> FindServerAsync(
        string machineIdentifier,
        string token,
        PlexConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var servers = await _tv
            .GetServersAsync(token, settings.ClientIdentifier, cancellationToken)
            .ConfigureAwait(false);

        return servers.FirstOrDefault(server =>
            string.Equals(server.MachineIdentifier, machineIdentifier, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The token to call the selected server with: its own when plex.tv gave one.</summary>
    private static string? TokenFor(PlexConnectionSettings settings) =>
        string.IsNullOrEmpty(settings.ServerToken) ? settings.Token : settings.ServerToken;

    private static bool TryReadServerUrl(PlexConnectionSettings settings, out Uri server)
    {
        server = null!;

        return !string.IsNullOrWhiteSpace(settings.ServerUrl)
            && Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out server!);
    }

    private static bool IsMusic(PlexSection section) =>
        string.Equals(section.Type, MusicSectionType, StringComparison.OrdinalIgnoreCase);
}
