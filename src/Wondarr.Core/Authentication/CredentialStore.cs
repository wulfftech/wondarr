using System.Security.Cryptography;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Authentication;

/// <inheritdoc />
public sealed class CredentialStore : ICredentialStore
{
    /// <summary>The settings key the credentials live under.</summary>
    public const string SettingsKey = "auth.user";

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;

    // One check-and-write at a time per process. Wondarr is a single process on a single SQLite
    // file, and the store is scoped per request, so the lock is static: every request shares it.
    private static readonly SemaphoreSlim InitialGate = new(1, 1);

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
    public async Task<bool> TrySetInitialAsync(string username, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        await InitialGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await SetAsync(username, password, cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            InitialGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var stored = await GetStoredAsync(cancellationToken).ConfigureAwait(false);

        var usernameMatches = stored is not null
            && string.Equals(stored.Username, username, StringComparison.OrdinalIgnoreCase);

        // Always pay for the hash, against a dummy when the user is unknown, so response time
        // does not reveal whether a username exists.
        var salt = usernameMatches ? Convert.FromBase64String(stored!.Salt) : DummySalt;
        var expected = usernameMatches ? Convert.FromBase64String(stored!.Hash) : DummyHash;
        var iterations = usernameMatches ? stored!.Iterations : Iterations;

        var passwordMatches = CryptographicOperations.FixedTimeEquals(ComputeHash(password, salt, iterations), expected);
        return usernameMatches && passwordMatches;
    }

    private static readonly byte[] DummySalt = new byte[SaltSize];
    private static readonly byte[] DummyHash = new byte[HashSize];

    private Task<StoredCredentials?> GetStoredAsync(CancellationToken cancellationToken) =>
        _settings.GetAsync<StoredCredentials>(SettingsKey, cancellationToken);

    private static byte[] ComputeHash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
}
