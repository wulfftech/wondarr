// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/Discord.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/DiscordProxy.cs, GPL-3.0.
// Adapted for Wondarr: `IHttpClientFactory` and `System.Text.Json` instead of NzbDrone's `IHttpClient`,
// no NLog, one song-level message per event, no configurable grab/import field lists, and the retry and
// error handling of `NotificationHttp` in place of Lidarr's own.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Notifications.Discord;

/// <summary>
/// Posts Wondarr's events to a Discord channel webhook as one embed per event.
/// </summary>
/// <remarks>
/// The body is Discord's own wire shape, so the property names are Discord's — <c>avatar_url</c>,
/// <c>icon_url</c> — with everything else camelCase. A <c>429</c> is Discord's rate limit: the
/// <c>Retry-After</c> it carries is waited out once by <see cref="NotificationHttp.SendAsync"/>.
/// </remarks>
public sealed partial class DiscordProvider : INotificationProvider
{
    /// <summary>The wire format: camelCase names, and nothing written for a value that is absent.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The size suffixes <see cref="HumanSize"/> steps through.</summary>
    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="DiscordProvider"/> class.</summary>
    /// <param name="clients">The factory the named <see cref="NotificationHttp.ClientName"/> client comes from.</param>
    /// <param name="time">The clock a rate-limit wait and the embed timestamp are read from.</param>
    /// <param name="logger">The log sink; a send failure is a debug line, the dispatcher warns.</param>
    public DiscordProvider(
        IHttpClientFactory clients,
        TimeProvider time,
        ILogger<DiscordProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Implementation => "Discord";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new(
            "webHookUrl",
            "Webhook URL",
            "url",
            Required: true,
            Secret: true,
            HelpText: "The channel webhook URL. It carries the channel's token, so it is never shown again."),
        new("username", "Username", "text", Required: false, HelpText: "The name to post as, if not Discord's default."),
        new("avatar", "Avatar", "url", Required: false, HelpText: "The avatar URL to post the message with."),
        new(
            "author",
            "Author",
            "text",
            Required: false,
            HelpText: $"""The author name shown on the embed. Defaults to "{DiscordSettings.DefaultAuthor}".""",
            Advanced: true),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = DiscordSettings.From(settings);

        return DiscordSettings.TryWebHookUrl(parsed.WebHookUrl, out _)
            ? []
            : ["The webhook URL must be an https address on discord.com or discordapp.com."];
    }

    /// <inheritdoc />
    public async Task SendAsync(
        NotificationMessage message,
        JsonElement settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parsed = DiscordSettings.From(settings);

        if (!DiscordSettings.TryWebHookUrl(parsed.WebHookUrl, out var url))
        {
            throw new NotificationSendException("the Discord webhook has no usable URL.");
        }

        var body = JsonSerializer.Serialize(Build(message, parsed), PayloadJson);
        var client = _clients.CreateClient(NotificationHttp.ClientName);

        await NotificationHttp
            .SendAsync(client, () => Request(url, body), _time, cancellationToken)
            .ConfigureAwait(false);

        LogSent(_logger, message.Event);
    }

    // Discord refuses the whole message (HTTP 400) when any part is over its limit, so a long song
    // title or failure message would silently lose every notification about that song; each part is
    // cut to fit instead (https://discord.com/developers/docs/resources/message#embed-object-embed-limits).
    private const int TitleLimit = 256;
    private const int DescriptionLimit = 4096;
    private const int FieldValueLimit = 1024;
    private const int AuthorLimit = 256;
    private const int UsernameLimit = 80;

    /// <summary>The text cut to <paramref name="limit"/> characters, ending in an ellipsis when it was cut.</summary>
    internal static string? Limit(string? text, int limit)
    {
        if (text is null || text.Length <= limit)
        {
            return text;
        }

        var cut = text[..(limit - 1)];

        // Never leave half of a surrogate pair at the end.
        if (char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return string.Concat(cut, "…");
    }

    /// <summary>Builds the body of one message: its embed, and who to post it as.</summary>
    private DiscordPayload Build(NotificationMessage message, DiscordSettings settings) => new()
    {
        Username = Limit(Blank(settings.Username), UsernameLimit),
        AvatarUrl = Blank(settings.Avatar),
        Embeds = [Embed(message, settings)],
    };

    /// <summary>Builds the embed one event turns into.</summary>
    private DiscordEmbed Embed(NotificationMessage message, DiscordSettings settings) => new()
    {
        Title = Limit(message.Title, TitleLimit),
        Description = Limit(message.Body, DescriptionLimit),
        Color = (int)Color(message),
        Timestamp = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
        Author = new DiscordAuthor(Limit(settings.AuthorName, AuthorLimit)!),
        Fields = EmbedFields(message),
    };

    /// <summary>The colour an event's embed is drawn in.</summary>
    private static DiscordColors Color(NotificationMessage message) => message.Event switch
    {
        NotificationEventNames.Grab => DiscordColors.Standard,
        NotificationEventNames.Import => DiscordColors.Success,
        NotificationEventNames.Upgrade => DiscordColors.Success,
        NotificationEventNames.Failure => DiscordColors.Danger,
        NotificationEventNames.Health => IsWarning(message.Health?.Level) ? DiscordColors.Warning : DiscordColors.Danger,
        NotificationEventNames.Update => DiscordColors.Standard,
        NotificationEventNames.Test => DiscordColors.Standard,
        _ => throw new NotificationSendException($"no Discord embed exists for the event '{message.Event}'."),
    };

    /// <summary>Whether a health level is the warning one; anything else is the error one.</summary>
    private static bool IsWarning(string? level) =>
        string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase);

    /// <summary>The structured parts of an event, or <see langword="null"/> when it has none.</summary>
    private static List<DiscordField>? EmbedFields(NotificationMessage message)
    {
        var fields = new List<DiscordField>();

        if (message.Song is { } song)
        {
            Add(fields, "Artist", song.ArtistCredit);
            Add(fields, "Album", song.AlbumTitle);
        }

        if (message.Release is { } release)
        {
            Add(fields, "Quality", release.Quality);
            Add(fields, "Source", release.SourceType);
            Add(fields, "Size", release.SizeBytes is { } size ? HumanSize(size) : null);
        }
        else if (message.File is { } file)
        {
            Add(fields, "Quality", file.Quality);
        }

        if (message.IsUpgrade)
        {
            Add(fields, "Upgrade", "Yes");
        }

        if (message.Update is { } update)
        {
            Add(fields, "Installed", update.CurrentVersion);
            Add(fields, "Latest", update.LatestVersion);
            Add(fields, "Release", update.ReleaseUrl);
        }

        return fields.Count == 0 ? null : fields;
    }

    private static void Add(List<DiscordField> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new DiscordField(name, Limit(value.Trim(), FieldValueLimit)!));
        }
    }

    /// <summary>A byte count as a person reads it, the way Lidarr's own embed prints one.</summary>
    private static string HumanSize(long bytes)
    {
        if (bytes == 0)
        {
            return string.Concat("0 ", SizeSuffixes[0]);
        }

        var magnitude = Math.Abs(bytes);
        var place = (int)Math.Floor(Math.Log(magnitude, 1024));
        var scaled = Math.Round(magnitude / Math.Pow(1024, place), 1);

        return string.Concat(
            (Math.Sign(bytes) * scaled).ToString(CultureInfo.InvariantCulture),
            " ",
            SizeSuffixes[place]);
    }

    /// <summary>
    /// Builds one request. Called again for a retry: an <see cref="HttpRequestMessage"/> and its content
    /// cannot be sent twice.
    /// </summary>
    private static HttpRequestMessage Request(Uri url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent the {Event} Discord message")]
    private static partial void LogSent(ILogger logger, string @event);
}
