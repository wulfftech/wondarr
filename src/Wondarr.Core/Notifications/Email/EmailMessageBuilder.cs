using System.Globalization;
using System.Net;
using System.Text;
using MimeKit;

namespace Wondarr.Core.Notifications.Email;

/// <summary>
/// Builds the emails: a plain-text part and an inline-styled HTML part in a <c>multipart/alternative</c>,
/// with album covers embedded by <c>cid:</c> when the album folder has a small <c>cover.jpg</c>.
/// </summary>
/// <remarks>
/// Every value that reaches the HTML is encoded. A cover is read only when it is a file called
/// <c>cover.jpg</c>, at most <see cref="MaxCoverBytes"/> long; otherwise the album's cover URL is a
/// plain remote image (a mail client may block it), or there is no image. One message embeds at most
/// <see cref="MaxEmbeddedCovers"/> covers, and songs of one album share a single embedded part.
/// </remarks>
internal sealed class EmailMessageBuilder
{
    /// <summary>The largest cover file that is embedded, in bytes.</summary>
    public const long MaxCoverBytes = 500 * 1024;

    /// <summary>The most covers one message embeds.</summary>
    public const int MaxEmbeddedCovers = 25;

    private const string CoverFileName = "cover.jpg";

    private readonly Dictionary<string, string?> _covers = new(StringComparer.Ordinal);
    private readonly BodyBuilder _body = new();
    private int _embedded;

    private EmailMessageBuilder()
    {
    }

    /// <summary>Builds the email for one event.</summary>
    /// <param name="message">The event.</param>
    /// <param name="settings">The notification's settings.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipients.</param>
    public static MimeMessage Single(
        NotificationMessage message,
        EmailSettings settings,
        MailboxAddress from,
        IReadOnlyList<MailboxAddress> to)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);

        var builder = new EmailMessageBuilder();
        var songMessage = message.Song is not null &&
            message.Event is NotificationEventNames.Download or NotificationEventNames.Import or NotificationEventNames.Upgrade or NotificationEventNames.Failure;

        string subject;
        string text;
        string html;

        if (songMessage)
        {
            var song = message.Song!;
            var name = Name(song);
            var link = Link(settings, song.Id);

            subject = string.Concat("Wondarr: ", Verb(message.Event), " ", name);

            var details = Details(song, message.Download, message.Event == NotificationEventNames.Failure ? message.Body : null);
            var cover = builder.Cover(message.Download);

            text = string.Concat(
                subject.Replace("Wondarr: ", string.Empty, StringComparison.Ordinal),
                "\n\n",
                string.Join('\n', details.Select(pair => string.Concat(pair.Key, ": ", pair.Value))),
                link is null ? string.Empty : string.Concat("\n\nOpen in Wondarr: ", link),
                "\n");

            html = Page(
                string.Concat(
                    "<h2 style=\"margin:0 0 12px;font-size:18px\">",
                    Encode(Capitalised(Verb(message.Event)) + " " + name),
                    "</h2>",
                    cover is null ? string.Empty : string.Concat("<p style=\"margin:0 0 12px\">", cover, "</p>"),
                    DetailsTable(details),
                    link is null
                        ? string.Empty
                        : string.Concat("<p><a href=\"", Encode(link), "\">Open in Wondarr</a></p>")));
        }
        else
        {
            subject = string.Concat("Wondarr: ", message.Title);
            text = string.Concat(message.Title, "\n\n", message.Body, "\n");
            html = Page(string.Concat(
                "<h2 style=\"margin:0 0 12px;font-size:18px\">",
                Encode(message.Title),
                "</h2><p style=\"margin:0\">",
                Encode(message.Body),
                "</p>"));
        }

        return builder.Finish(subject, text, html, from, to);
    }

    /// <summary>Builds the email for a window of held-back events.</summary>
    /// <param name="entries">The events, oldest first.</param>
    /// <param name="from">When the window began.</param>
    /// <param name="to">When the window ended.</param>
    /// <param name="zone">The time zone the times show in.</param>
    /// <param name="settings">The notification's settings.</param>
    /// <param name="sender">The sender.</param>
    /// <param name="recipients">The recipients.</param>
    public static MimeMessage Digest(
        IReadOnlyList<DigestEntry> entries,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeZoneInfo zone,
        EmailSettings settings,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(settings);

        var builder = new EmailMessageBuilder();
        var groups = new (string Event, string Verb, string Heading)[]
        {
            (NotificationEventNames.Download, "downloaded", "Downloaded"),
            (NotificationEventNames.Import, "imported", "Imported"),
            (NotificationEventNames.Upgrade, "upgraded", "Upgraded"),
            (NotificationEventNames.Failure, "failed", "Failed"),
        };

        var counts = string.Join(
            ", ",
            groups
                .Select(group => (group.Verb, Count: entries.Count(entry => entry.Message.Event == group.Event)))
                .Where(group => group.Count > 0)
                .Select(group => string.Concat(group.Count.ToString(CultureInfo.InvariantCulture), " ", group.Verb)));

        var range = Range(from, to, zone);
        var summary = string.Concat(counts.Length == 0 ? "no events" : counts, " (", range, ")");
        var subject = string.Concat("Wondarr: ", summary);

        var text = new StringBuilder().Append("Wondarr digest: ").Append(summary).Append('\n');
        var html = new StringBuilder()
            .Append("<h2 style=\"margin:0 0 12px;font-size:18px\">Wondarr digest: ")
            .Append(Encode(summary))
            .Append("</h2>");

        foreach (var group in groups)
        {
            var rows = entries.Where(entry => entry.Message.Event == group.Event).OrderBy(entry => entry.At).ToList();

            if (rows.Count == 0)
            {
                continue;
            }

            text.Append('\n').Append(group.Heading).Append(" (").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
            html.Append("<h3 style=\"margin:16px 0 6px;font-size:15px\">")
                .Append(group.Heading)
                .Append(" (")
                .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append(")</h3><table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;width:100%\">")
                .Append("<tr style=\"text-align:left;background:#f2f2f2\"><th></th><th>Song</th><th>Album</th><th>Source</th><th>Quality</th><th>Size</th><th>Time</th></tr>");

            foreach (var row in rows)
            {
                var message = row.Message;
                var song = message.Song;
                var download = message.Download;
                var name = song is null ? message.Title : Name(song);
                var link = song is null ? null : Link(settings, song.Id);
                var time = Clock(row.At, zone);

                text.Append("- ").Append(name);
                AppendIf(text, " | ", song?.AlbumTitle);
                AppendIf(text, " | ", download?.SourceType);
                AppendIf(text, " | ", download?.Quality);
                AppendIf(text, " | ", Size(download?.SizeBytes));
                text.Append(" | ").Append(time);

                if (group.Event == NotificationEventNames.Failure)
                {
                    text.Append(" | ").Append(message.Body);
                }

                if (link is not null)
                {
                    text.Append(" | ").Append(link);
                }

                text.Append('\n');

                var cover = builder.Cover(download);
                var label = link is null
                    ? Encode(name)
                    : string.Concat("<a href=\"", Encode(link), "\">", Encode(name), "</a>");

                html.Append("<tr style=\"border-top:1px solid #ddd;vertical-align:top\"><td>")
                    .Append(cover ?? string.Empty)
                    .Append("</td><td>")
                    .Append(label);

                if (group.Event == NotificationEventNames.Failure)
                {
                    html.Append("<br><span style=\"color:#b00020\">").Append(Encode(message.Body)).Append("</span>");
                }

                html.Append("</td><td>").Append(Encode(song?.AlbumTitle))
                    .Append("</td><td>").Append(Encode(download?.SourceType))
                    .Append("</td><td>").Append(Encode(download?.Quality))
                    .Append("</td><td>").Append(Encode(Size(download?.SizeBytes)))
                    .Append("</td><td>").Append(Encode(time))
                    .Append("</td></tr>");
            }

            html.Append("</table>");
        }

        return builder.Finish(subject, text.ToString(), Page(html.ToString()), sender, recipients);
    }

    private static void AppendIf(StringBuilder text, string separator, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append(separator).Append(value);
        }
    }

    private static string Page(string content) => string.Concat(
        "<html><body style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#222\">",
        content,
        "</body></html>");

    private static List<KeyValuePair<string, string>> Details(NotificationSong song, NotificationDownload? download, string? reason)
    {
        var details = new List<KeyValuePair<string, string>>
        {
            new("Artist", song.ArtistCredit),
            new("Title", song.Title),
        };

        Add(details, "Album", song.AlbumTitle);
        Add(details, "Source", download?.SourceType);
        Add(details, "Quality", download?.Quality);
        Add(details, "Size", Size(download?.SizeBytes));
        Add(details, "Reason", reason);

        return details;
    }

    private static void Add(List<KeyValuePair<string, string>> details, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            details.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static string DetailsTable(List<KeyValuePair<string, string>> details) => string.Concat(
        "<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse\">",
        string.Concat(details.Select(pair => string.Concat(
            "<tr style=\"border-top:1px solid #ddd\"><th style=\"text-align:left;color:#666\">",
            Encode(pair.Key),
            "</th><td>",
            Encode(pair.Value),
            "</td></tr>"))),
        "</table>");

    private static string Name(NotificationSong song) => string.Concat(song.ArtistCredit, " – ", song.Title);

    private static string Verb(string @event) => @event switch
    {
        NotificationEventNames.Download => "downloaded",
        NotificationEventNames.Import => "imported",
        NotificationEventNames.Upgrade => "upgraded",
        _ => "failed",
    };

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>"14:00–14:30"; with dates ("13 Oct 23:50–14 Oct 00:20") when it crosses midnight or lasts over 12 hours.</summary>
    private static string Range(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(from, zone);
        var end = TimeZoneInfo.ConvertTime(to, zone);

        if (start.Date == end.Date && end - start <= TimeSpan.FromHours(12))
        {
            return string.Concat(Clock(from, zone), "–", Clock(to, zone));
        }

        return string.Concat(
            start.ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
            "–",
            end.ToString("d MMM HH:mm", CultureInfo.InvariantCulture));
    }

    private static string Clock(DateTimeOffset moment, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? Link(EmailSettings settings, long songId) =>
        string.IsNullOrWhiteSpace(settings.WondarrUrl)
            ? null
            : string.Concat(settings.WondarrUrl.TrimEnd('/'), "/song/", songId.ToString(CultureInfo.InvariantCulture));

    private static string? Size(long? bytes)
    {
        if (bytes is not { } size || size < 0)
        {
            return null;
        }

        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Concat(size.ToString(CultureInfo.InvariantCulture), " B")
            : string.Concat(value.ToString("0.0", CultureInfo.InvariantCulture), " ", units[unit]);
    }

    /// <summary>
    /// The <c>img</c> tag for a song's cover, or <see langword="null"/>: the embedded part when the
    /// album's <c>cover.jpg</c> is there and small enough (one part per file), otherwise the cover URL.
    /// </summary>
    private string? Cover(NotificationDownload? download)
    {
        if (download is null)
        {
            return null;
        }

        if (download.CoverPath is { } path)
        {
            if (!_covers.TryGetValue(path, out var cid))
            {
                cid = _embedded < MaxEmbeddedCovers ? Embed(path) : null;
                _covers[path] = cid;
            }

            if (cid is not null)
            {
                return Image(string.Concat("cid:", cid));
            }
        }

        return Uri.TryCreate(download.CoverUrl, UriKind.Absolute, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? Image(url.AbsoluteUri)
            : null;
    }

    private string? Embed(string path)
    {
        byte[] data;

        try
        {
            var info = new FileInfo(path);

            if (!string.Equals(info.Name, CoverFileName, StringComparison.OrdinalIgnoreCase) ||
                !info.Exists ||
                info.Length == 0 ||
                info.Length > MaxCoverBytes)
            {
                return null;
            }

            data = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        _embedded++;

        var cid = string.Concat("cover", _embedded.ToString(CultureInfo.InvariantCulture), "@wondarr");
        var part = _body.LinkedResources.Add(CoverFileName, data, new ContentType("image", "jpeg"));
        part.ContentId = cid;

        return cid;
    }

    private static string Image(string source) => string.Concat(
        "<img src=\"",
        Encode(source),
        "\" width=\"64\" height=\"64\" alt=\"\" style=\"display:block;border-radius:4px\">");

    private MimeMessage Finish(
        string subject,
        string text,
        string html,
        MailboxAddress from,
        IReadOnlyList<MailboxAddress> to)
    {
        _body.TextBody = text;
        _body.HtmlBody = html;

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.AddRange(to);
        message.Subject = subject.ReplaceLineEndings(" ");
        message.Body = _body.ToMessageBody();

        return message;
    }
}
