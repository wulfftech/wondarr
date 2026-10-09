using Wondarr.Core.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Metadata.LastFm;

/// <summary>
/// The <c>lastfm</c> section of <c>config.yml</c>: Wondarr's own Last.fm API key. Optional. With a
/// key the song page shows what Last.fm knows about the song, and a Last.fm import list without a key
/// of its own reads with this one.
/// </summary>
public sealed class LastFmOptions
{
    /// <summary>
    /// The Last.fm API key. It is registered with <see cref="ISecretRegistry"/> so the log redactor
    /// removes it.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>Trims the key and hands it to the secret registry so it cannot reach a log sink.</summary>
public sealed class LastFmOptionsPostConfigure : IPostConfigureOptions<LastFmOptions>
{
    private readonly ISecretRegistry _secrets;

    /// <summary>Initialises a new instance of the <see cref="LastFmOptionsPostConfigure"/> class.</summary>
    /// <param name="secrets">The registry every sink redacts through.</param>
    public LastFmOptionsPostConfigure(ISecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, LastFmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ApiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey.Trim();

        // Registered here, not at registration time, because the key is bound from configuration.
        _secrets.Register(options.ApiKey);
    }
}
