// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Payloads/DiscordPayload.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Payloads/Embed.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Payloads/DiscordField.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Payloads/DiscordAuthor.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Payloads/DiscordImage.cs, GPL-3.0.
// Adapted for Wondarr: `System.Text.Json` with Discord's own snake_case names spelled out, and the
// image and thumbnail wrappers of Lidarr's `Embed` dropped because Wondarr sends no artwork.

using System.Text.Json.Serialization;

namespace Wondarr.Core.Notifications.Discord;

/// <summary>
/// The body of one Discord webhook post: what to say as plain content, who to say it as, and the
/// embeds that carry the event.
/// </summary>
internal sealed record DiscordPayload
{
    /// <summary>Plain text above the embeds, or <see langword="null"/> when the embed says it all.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    /// <summary>The name to post under, or <see langword="null"/> for the webhook's own default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; init; }

    /// <summary>The avatar to post with, or <see langword="null"/> for the webhook's own default.</summary>
    [JsonPropertyName("avatar_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarUrl { get; init; }

    /// <summary>The embeds to post, one per message.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DiscordEmbed>? Embeds { get; init; }
}

/// <summary>One embed: a coloured card with a title, a description and the structured fields.</summary>
internal sealed record DiscordEmbed
{
    /// <summary>The single line at the top of the card.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>The body under the title.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The stripe colour, one of <see cref="DiscordColors"/>.</summary>
    public int Color { get; init; }

    /// <summary>When the event happened, as an ISO 8601 UTC timestamp.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timestamp { get; init; }

    /// <summary>Who the card is from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiscordAuthor? Author { get; init; }

    /// <summary>The structured parts of the event, or <see langword="null"/> when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DiscordField>? Fields { get; init; }
}

/// <summary>One named value on an embed.</summary>
/// <param name="Name">The field's label.</param>
/// <param name="Value">The field's value.</param>
internal sealed record DiscordField(string Name, string Value)
{
    /// <summary>Whether Discord lays this field beside the previous one instead of under it.</summary>
    public bool Inline { get; init; }
}

/// <summary>Who an embed is attributed to.</summary>
/// <param name="Name">The author name.</param>
internal sealed record DiscordAuthor(string Name)
{
    /// <summary>The author's avatar, or <see langword="null"/> when none is set.</summary>
    [JsonPropertyName("icon_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconUrl { get; init; }
}
