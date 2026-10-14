using System.Text.Json;
using System.Text.Json.Nodes;
using MimeKit;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Email;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>Records what the email provider hands to SMTP, and fails on demand.</summary>
internal sealed class FakeSmtpSender : ISmtpSender, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(SmtpEndpoint Endpoint, MimeMessage Message)> _sent = [];
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Thrown by every send when set.</summary>
    public Exception? Throw { get; set; }

    /// <summary>Gets what was sent, in order.</summary>
    public IReadOnlyList<(SmtpEndpoint Endpoint, MimeMessage Message)> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _signal.Dispose();

    /// <inheritdoc />
    public Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        if (Throw is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _sent.Add((endpoint, message));
        }

        _signal.Release();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits, bounded, until <paramref name="count"/> more messages have been sent since the last wait,
    /// for a test whose sends happen on the dispatcher's own loop.
    /// </summary>
    /// <param name="count">How many sends to wait for.</param>
    public async Task WaitForSendsAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (!await _signal.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException($"Only {index} of {count} expected sends arrived.");
            }
        }
    }
}

/// <summary>Settings and messages the email tests share.</summary>
internal static class EmailSamples
{
    /// <summary>Email settings; the overrides replace or (with <see langword="null"/>) remove a key.</summary>
    public static JsonElement Settings(params (string Key, object? Value)[] overrides)
    {
        var settings = new JsonObject
        {
            ["host"] = "smtp.example.com",
            ["port"] = 587,
            ["security"] = "startTls",
            ["username"] = "wondarr",
            ["password"] = "app-password",
            ["from"] = "wondarr@example.com",
            ["to"] = "me@example.com, you@example.com",
            ["wondarrUrl"] = "https://wondarr.example.com/",
            ["delivery"] = "each",
            ["digestMinutes"] = 30,
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
        }

        return JsonDocument.Parse(settings.ToJsonString()).RootElement.Clone();
    }

    /// <summary>An import, upgrade or failure message about one song.</summary>
    public static NotificationMessage Song(
        string @event = NotificationEventNames.Import,
        long id = 7,
        string artist = "Oasis",
        string title = "Wonderwall",
        string? album = "Morning Glory",
        string? coverPath = null,
        string? coverUrl = null,
        long? size = 31_457_280,
        string body = "FLAC")
    {
        var verb = @event switch
        {
            NotificationEventNames.Download => "Downloaded: ",
            NotificationEventNames.Upgrade => "Upgraded: ",
            NotificationEventNames.Failure => "Failed: ",
            _ => "Imported: ",
        };

        return new NotificationMessage(@event, string.Concat(verb, artist, " – ", title), body)
        {
            Song = new NotificationSong(id, title, artist, album, null),
            Download = new NotificationDownload("soulseek", "FLAC", size, coverPath, coverUrl),
        };
    }
}
