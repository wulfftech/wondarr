using System.Globalization;
using System.Text.Json;
using MailKit.Security;
using MimeKit;

namespace Wondarr.Core.Notifications.Email;

/// <summary>The choices of the email <c>security</c> field. The first is the form's default.</summary>
internal static class EmailSecurity
{
    public const string StartTls = "startTls";
    public const string SslOnConnect = "sslOnConnect";
    public const string None = "none";

    /// <summary>Every choice the settings form offers; the first is the default.</summary>
    public static readonly IReadOnlyList<string> All = [StartTls, SslOnConnect, None];

    /// <summary>Maps a choice to MailKit's socket option; an unknown choice is STARTTLS.</summary>
    /// <param name="security">The stored choice.</param>
    public static SecureSocketOptions Options(string security) => security switch
    {
        SslOnConnect => SecureSocketOptions.SslOnConnect,
        None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}

/// <summary>The choices of the email <c>delivery</c> field. The first is the form's default.</summary>
internal static class EmailDelivery
{
    public const string Each = "each";
    public const string Digest = "digest";

    /// <summary>Every choice the settings form offers; the first is the default.</summary>
    public static readonly IReadOnlyList<string> All = [Each, Digest];
}

/// <summary>One email notification's settings, read out of the stored JSON object.</summary>
/// <param name="Host">The SMTP server, or <see langword="null"/>.</param>
/// <param name="Port">The SMTP port; 587 when the user left it empty, <see langword="null"/> when it is not a whole number.</param>
/// <param name="Security">One of the <see cref="EmailSecurity"/> choices, as stored.</param>
/// <param name="Username">The login, or <see langword="null"/>.</param>
/// <param name="Password">The password, or <see langword="null"/>.</param>
/// <param name="From">The sender address as typed, or <see langword="null"/>.</param>
/// <param name="To">The recipient addresses as typed, comma separated, or <see langword="null"/>.</param>
/// <param name="WondarrUrl">The address Wondarr is reached at, or <see langword="null"/>.</param>
/// <param name="Delivery">One of the <see cref="EmailDelivery"/> choices, as stored.</param>
/// <param name="DigestMinutes">The window in minutes; 30 when the user left it empty, <see langword="null"/> when it is not a whole number.</param>
internal sealed record EmailSettings(
    string? Host,
    int? Port,
    string Security,
    string? Username,
    string? Password,
    string? From,
    string? To,
    string? WondarrUrl,
    string Delivery,
    int? DigestMinutes)
{
    /// <summary>The default SMTP submission port.</summary>
    public const int DefaultPort = 587;

    /// <summary>The default digest window in minutes.</summary>
    public const int DefaultDigestMinutes = 30;

    /// <summary>The shortest digest window in minutes.</summary>
    public const int MinDigestMinutes = 5;

    /// <summary>The longest digest window in minutes.</summary>
    public const int MaxDigestMinutes = 1440;

    /// <summary>Gets a value indicating whether the notification batches imports, upgrades and failures.</summary>
    public bool IsDigest => string.Equals(Delivery, EmailDelivery.Digest, StringComparison.Ordinal);

    /// <summary>Reads a settings object; anything absent takes its default.</summary>
    /// <param name="settings">The stored settings object.</param>
    public static EmailSettings Read(JsonElement settings)
    {
        var security = Text(settings, "security");
        var delivery = Text(settings, "delivery");

        return new EmailSettings(
            Text(settings, "host")?.Trim(),
            Whole(settings, "port", DefaultPort),
            string.IsNullOrWhiteSpace(security) ? EmailSecurity.StartTls : security.Trim(),
            Text(settings, "username"),
            Text(settings, "password"),
            Text(settings, "from")?.Trim(),
            Text(settings, "to"),
            Text(settings, "wondarrUrl")?.Trim(),
            string.IsNullOrWhiteSpace(delivery) ? EmailDelivery.Each : delivery.Trim(),
            Whole(settings, "digestMinutes", DefaultDigestMinutes));
    }

    /// <summary>
    /// Splits the recipient text on commas and parses each one. A part that is not one address is
    /// returned in <paramref name="invalid"/>.
    /// </summary>
    /// <param name="text">The recipients as typed.</param>
    /// <param name="invalid">What did not parse.</param>
    public static IReadOnlyList<MailboxAddress> ParseRecipients(string? text, out IReadOnlyList<string> invalid)
    {
        var good = new List<MailboxAddress>();
        var bad = new List<string>();

        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryAddress(part, out var address))
            {
                good.Add(address);
            }
            else
            {
                bad.Add(part);
            }
        }

        invalid = bad;

        return good;
    }

    /// <summary>Parses one address; a bare <c>name</c> with no <c>@</c> is not one.</summary>
    /// <param name="text">The address as typed.</param>
    /// <param name="address">The parsed address.</param>
    public static bool TryAddress(string? text, out MailboxAddress address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(text) ||
            !MailboxAddress.TryParse(text.Trim(), out var parsed) ||
            !parsed.Address.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        address = parsed;

        return true;
    }

    private static string? Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A whole number setting: absent or null is the default, anything else that is not whole is <see langword="null"/>.</summary>
    private static int? Whole(JsonElement settings, string name, int fallback)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return fallback;
            case JsonValueKind.Number when value.TryGetInt32(out var number):
                return number;
            case JsonValueKind.String:
                var text = value.GetString();

                return string.IsNullOrWhiteSpace(text)
                    ? fallback
                    : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }
}
