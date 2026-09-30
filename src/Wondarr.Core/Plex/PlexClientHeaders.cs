using System.Reflection;

namespace Wondarr.Core.Plex;

/// <summary>
/// The headers every Plex request carries. The token travels in <c>X-Plex-Token</c> and nowhere
/// else: a token in a query string ends up in Plex's own access log and in any proxy log on the way.
/// </summary>
internal static class PlexClientHeaders
{
    /// <summary>The product name plex.tv and a Plex Media Server see in <c>X-Plex-Product</c>.</summary>
    public const string Product = "Wondarr";

    private static readonly string Version = ResolveVersion();

    /// <summary>What the system-status endpoint reports as the app version.</summary>
    public static string AppVersion => Version;

    /// <summary>Adds the identifying headers, and the token when there is one, to a request.</summary>
    /// <param name="request">The request to stamp.</param>
    /// <param name="clientIdentifier">The stable identifier this install signs in with.</param>
    /// <param name="token">The Plex token, or <see langword="null"/> before sign-in.</param>
    public static void Apply(HttpRequestMessage request, string clientIdentifier, string? token)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", Product);
        request.Headers.TryAddWithoutValidation("X-Plex-Version", Version);
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        }
    }

    /// <summary>
    /// Reads the running app's version the way <c>Wondarr.Api</c>'s system-status endpoint does: the
    /// entry assembly's informational version, without the source revision a local build appends.
    /// </summary>
    private static string ResolveVersion()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        var plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus >= 0 ? version[..plus] : version;
    }
}
