using Wondarr.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Where slskd is and how Wondarr authenticates to it: the bundled one on loopback with the
/// generated key, or the user's own with the key from the settings. Read on every request, so a
/// settings change applies at once.
/// </summary>
public interface ISlskdEndpoint
{
    /// <summary>
    /// The base address every request goes to, and the API key (<c>X-API-Key</c>) it carries. The
    /// key is a secret: it never reaches a log line, a health message or an exception.
    /// </summary>
    ValueTask<(Uri BaseAddress, string ApiKey)> ResolveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SlskdEndpoint : ISlskdEndpoint
{
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="SlskdEndpoint"/> class.</summary>
    /// <param name="options">Soulseek settings, read on every call so a change applies at once.</param>
    /// <param name="scopeFactory">
    /// Source of scopes, because <see cref="SlskdSecretsStore"/> is scoped (it uses the settings
    /// repository); the endpoint itself is a singleton.
    /// </param>
    /// <param name="secrets">Where the external key and web password are registered as secrets.</param>
    public SlskdEndpoint(
        IOptionsMonitor<SoulseekOptions> options,
        IServiceScopeFactory scopeFactory,
        ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(secrets);

        _options = options;
        _scopeFactory = scopeFactory;
        _secrets = secrets;
    }

    /// <inheritdoc />
    public async ValueTask<(Uri BaseAddress, string ApiKey)> ResolveAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (options.Mode == SoulseekMode.External)
        {
            var external = options.External;

            // Registered as soon as they are read, so no redaction filter can miss them.
            _secrets.Register(external.ApiKey);
            _secrets.Register(external.WebPassword);

            // A trailing slash, so a relative request path resolves against the host root.
            var url = external.Url!.TrimEnd('/') + "/";

            return (new Uri(url, UriKind.Absolute), external.ApiKey!);
        }

        // The bundled slskd is always on loopback; the port is the only part that moves.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SlskdSecretsStore>();
        var secrets = await store.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);

        return (new Uri($"http://127.0.0.1:{options.WebPort}/", UriKind.Absolute), secrets.ApiKey);
    }
}