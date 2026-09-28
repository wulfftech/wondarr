using System.Security.Cryptography;
using Compilarr.Core.Persistence;

namespace Compilarr.Core.Authentication;

/// <inheritdoc />
public sealed class CredentialStore : ICredentialStore
{
    /// <summary>The settings key the credentials live under.</summary>
    public const string SettingsKey = "auth.user";

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;

    private readonly ISettingsRepository _settings;

    /// <summary>Initialises a new instance of the <see cref="CredentialStore"/> class.</summary>
    public CredentialStore(ISettingsRepository settings) => _settings = settings;

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        await GetStoredAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async Task<string?> GetUsernameAsync(CancellationToken cancellationToken) =>
        (await GetStoredAsync(cancellationToken).ConfigureAwait(false))?.Username;

    /// <inheritdoc />
    public async Task SetAsync(string username, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        var existing = await GetStoredAsync(cancellationToken).ConfigureAwait(false);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);

        var stored = new StoredCredentials
        {
            Username = username,
            Salt = Convert.ToBase64String(salt),
            Iterations = Iterations,
            Hash = Convert.ToBase64String(ComputeHash(password, salt, Iterations)),
            Identifier = existing?.Identifier ?? Guid.NewGuid(),
        };

        await _settings.SetAsync(SettingsKey, stored, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var stored = await GetStoredAsync(cancellationToken).ConfigureAwait(false);

        if (stored is null || !string.Equals(stored.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var salt = Convert.FromBase64String(stored.Salt);
        var expected = Convert.FromBase64String(stored.Hash);

        // Fixed time so a wrong password cannot be told from a wrong username by timing.
        return CryptographicOperations.FixedTimeEquals(ComputeHash(password, salt, stored.Iterations), expected);
    }

    private Task<StoredCredentials?> GetStoredAsync(CancellationToken cancellationToken) =>
        _settings.GetAsync<StoredCredentials>(SettingsKey, cancellationToken);

    private static byte[] ComputeHash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
}
