using System.Security.Cryptography;
using Compilarr.Core.Logging;
using Compilarr.Core.Persistence;
using Microsoft.Extensions.Options;

namespace Compilarr.Sources.Slskd;

/// <summary>
/// The credentials Compilarr generates for the bundled slskd: the API key it uses to call slskd,
/// and the web username/password that protect slskd's own UI on loopback.
/// </summary>
/// <param name="ApiKey">64 lowercase hexadecimal characters, as slskd requires.</param>
/// <param name="WebUsername">Username for slskd's web authentication.</param>
/// <param name="WebPassword">Password for slskd's web authentication.</param>
public sealed record SlskdRuntimeSecrets(string ApiKey, string WebUsername, string WebPassword);

/// <summary>
/// Generates <see cref="SlskdRuntimeSecrets"/> once and stores them under the
/// <c>slskd.runtime</c> setting, so a restart or an upgrade reuses the same API key instead of
/// invalidating everything that holds it.
/// </summary>
public sealed class SlskdSecretsStore
{
    /// <summary>Settings key the generated secrets live under.</summary>
    public const string SettingKey = "slskd.runtime";

    /// <summary>Username slskd's web authentication is given; the password is the secret.</summary>
    public const string WebUsername = "compilarr";

    private readonly ISettingsRepository _repository;
    private readonly ISecretRegistry _secrets;
    private readonly IOptionsMonitor<SoulseekOptions> _options;

    private SlskdRuntimeSecrets? _cached;

    /// <summary>Initialises a new instance of the <see cref="SlskdSecretsStore"/> class.</summary>
    public SlskdSecretsStore(
        ISettingsRepository repository,
        ISecretRegistry secrets,
        IOptionsMonitor<SoulseekOptions> options)
    {
        _repository = repository;
        _secrets = secrets;
        _options = options;
    }

    /// <summary>
    /// Returns the stored secrets, generating and persisting them on first use. Every value is
    /// registered with <see cref="ISecretRegistry"/>, as is the Soulseek password from the options.
    /// </summary>
    public async Task<SlskdRuntimeSecrets> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var secrets = _cached
            ?? await _repository.GetAsync<SlskdRuntimeSecrets>(SettingKey, cancellationToken).ConfigureAwait(false);

        if (secrets is null || string.IsNullOrEmpty(secrets.ApiKey))
        {
            secrets = new SlskdRuntimeSecrets(GenerateHex(32), WebUsername, GenerateHex(32));
            await _repository.SetAsync(SettingKey, secrets, cancellationToken).ConfigureAwait(false);
        }

        _cached = secrets;

        _secrets.Register(secrets.ApiKey);
        _secrets.Register(secrets.WebPassword);
        _secrets.Register(_options.CurrentValue.Password);

        return secrets;
    }

    private static string GenerateHex(int byteCount) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));
}
