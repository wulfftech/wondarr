namespace Wondarr.Core.Authentication;

/// <summary>
/// The single local user that guards the web UI. Modelled on Lidarr's <c>UserService</c>, but
/// backed by the settings table instead of a dedicated table.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Whether a username and password have been configured.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    /// <summary>The configured username, or <see langword="null"/> when nothing is configured.</summary>
    Task<string?> GetUsernameAsync(CancellationToken cancellationToken);

    /// <summary>Creates or replaces the credentials.</summary>
    Task SetAsync(string username, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the credentials only when none exist yet. Returns <see langword="false"/>, leaving
    /// the stored credentials untouched, when someone got there first. Safe to call concurrently:
    /// of any number of simultaneous calls on an empty store, exactly one returns <see langword="true"/>.
    /// </summary>
    Task<bool> TrySetInitialAsync(string username, string password, CancellationToken cancellationToken);

    /// <summary>Checks a username and password. The username comparison is case-insensitive.</summary>
    Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken);
}
