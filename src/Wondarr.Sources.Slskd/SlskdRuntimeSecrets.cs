using System.Security.Cryptography;
using Wondarr.Core.Logging;
using Wondarr.Core.Persistence;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// The credentials Wondarr generates for the bundled slskd: the API key it uses to call slskd,
/// and the web username/password that protect slskd's own UI on loopback.
/// </summary>
/// <param name="ApiKey">64 lowercase hexadecimal characters, as slskd requires.</param>
/// <param name="WebUsername">Username for slskd's web authentication.</param>
/// <param name="WebPassword">Password for slskd's web authentication.</param>
/// <param name="WebhookToken">
/// The value slskd sends back in <c>X-Wondarr-Webhook</c> when it calls Wondarr's completion webhook.
/// It is the only thing authenticating that endpoint, which cannot use the API key. Secrets stored
/// before the webhook existed carry <c>null</c> here and are given one on the next read.
/// </param>
public sealed record SlskdRuntimeSecrets(string ApiKey, string WebUsername, string WebPassword, string? WebhookToken = null);

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
    public const string WebUsername = "wondarr";

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

        var store = false;

        if (secrets is null || string.IsNullOrEmpty(secrets.ApiKey))
        {
            secrets = new SlskdRuntimeSecrets(GenerateHex(32), WebUsername, GenerateHex(32), GenerateHex(32));
            store = true;
        }
        else if (string.IsNullOrEmpty(secrets.WebhookToken))
        {
            // An installation from before the webhook existed: keep its API key, add the token.
            secrets = secrets with { WebhookToken = GenerateHex(32) };
            store = true;
        }

        if (store)
        {
            await _repository.SetAsync(SettingKey, secrets, cancellationToken).ConfigureAwait(false);
        }

        _cached = secrets;

        _secrets.Register(secrets.ApiKey);
        _secrets.Register(secrets.WebPassword);
        _secrets.Register(secrets.WebhookToken);
        _secrets.Register(_options.CurrentValue.Password);

        return secrets;
    }

    private static string GenerateHex(int byteCount) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));
}
