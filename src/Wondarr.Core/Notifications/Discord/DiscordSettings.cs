// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/DiscordSettings.cs, GPL-3.0.
// Adapted for Wondarr: read from a `System.Text.Json` object rather than model-bound, no FluentValidation,
// no grab/import field selectors, and a host allow-list instead of Lidarr's plain URL check because the
// webhook URL carries the channel's token.

using System.Text.Json;

namespace Wondarr.Core.Notifications.Discord;

/// <summary>One Discord notification's settings, read out of the stored JSON object.</summary>
/// <param name="WebHookUrl">The channel webhook URL, or <see langword="null"/> when the user left it empty.</param>
/// <param name="Username">The name to post as, or <see langword="null"/> for the webhook's default.</param>
/// <param name="Avatar">The avatar URL to post with, or <see langword="null"/>.</param>
/// <param name="Author">The embed author name, or <see langword="null"/> for <see cref="DefaultAuthor"/>.</param>
internal sealed record DiscordSettings(string? WebHookUrl, string? Username, string? Avatar, string? Author)
{
    /// <summary>The author name an embed carries when the user set none.</summary>
    public const string DefaultAuthor = "Wondarr";

    /// <summary>The hosts a webhook URL may point at: Discord's own, over https.</summary>
    private static readonly string[] AllowedHosts = ["discord.com", "discordapp.com"];

    /// <summary>Gets the author name to put on an embed, never blank.</summary>
    public string AuthorName => string.IsNullOrWhiteSpace(Author) ? DefaultAuthor : Author.Trim();

    /// <summary>Reads a settings object; anything absent takes its default.</summary>
    /// <param name="settings">The stored settings object.</param>
    public static DiscordSettings From(JsonElement settings) =>
        new(Text(settings, "webHookUrl"), Text(settings, "username"), Text(settings, "avatar"), Text(settings, "author"));

    /// <summary>
    /// Checks a webhook URL: https, on a Discord host. Anywhere else would hand the token to a stranger.
    /// </summary>
    /// <param name="value">The URL the user entered, or <see langword="null"/>.</param>
    /// <param name="url">The parsed URL when it is usable.</param>
    public static bool TryWebHookUrl(string? value, out Uri url)
    {
        url = null!;

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        url = parsed;

        return AllowedHosts.Any(host => IsHost(parsed.Host, host));
    }

    /// <summary>Whether a host is one of Discord's, its subdomains included.</summary>
    private static bool IsHost(string host, string allowed) =>
        string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(string.Concat(".", allowed), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
