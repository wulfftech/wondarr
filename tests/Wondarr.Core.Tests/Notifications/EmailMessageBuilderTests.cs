using FluentAssertions;
using MimeKit;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Email;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>The emails: subjects, the two parts, escaping, links and the embedded covers.</summary>
public sealed class EmailMessageBuilderTests : IDisposable
{
    private static readonly MailboxAddress From = new("Wondarr", "wondarr@example.com");
    private static readonly MailboxAddress[] To = [new MailboxAddress("", "me@example.com")];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));

    public EmailMessageBuilderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(NotificationEventNames.Download, "Wondarr: downloaded Oasis – Wonderwall")]
    [InlineData(NotificationEventNames.Import, "Wondarr: imported Oasis – Wonderwall")]
    [InlineData(NotificationEventNames.Upgrade, "Wondarr: upgraded Oasis – Wonderwall")]
    [InlineData(NotificationEventNames.Failure, "Wondarr: failed Oasis – Wonderwall")]
    public void A_song_event_has_its_verb_and_the_song_in_the_subject(string @event, string subject)
    {
        var mail = Single(EmailSamples.Song(@event));

        mail.Subject.Should().Be(subject);
    }

    [Fact]
    public void Every_message_is_a_plain_text_and_html_alternative()
    {
        var mail = Single(EmailSamples.Song());

        mail.Body.Should().BeOfType<MultipartAlternative>();
        mail.TextBody.Should().Contain("Artist: Oasis").And.Contain("Title: Wonderwall").And.Contain("Album: Morning Glory");
        mail.TextBody.Should().Contain("Source: soulseek").And.Contain("Quality: FLAC").And.Contain("Size: 30.0 MB");
        mail.HtmlBody.Should().Contain("<table").And.Contain("Oasis").And.Contain("Morning Glory").And.Contain("30.0 MB");

        var plain = Single(new NotificationMessage(NotificationEventNames.Health, "Health: Plex", "Plex is down"));

        plain.Body.Should().BeOfType<MultipartAlternative>();
        plain.Subject.Should().Be("Wondarr: Health: Plex");
        plain.TextBody.Should().Contain("Plex is down");
        plain.HtmlBody.Should().Contain("Plex is down");
    }

    [Fact]
    public void A_failure_shows_its_reason()
    {
        var mail = Single(EmailSamples.Song(NotificationEventNames.Failure, body: "the peer went offline"));

        mail.TextBody.Should().Contain("Reason: the peer went offline");
        mail.HtmlBody.Should().Contain("the peer went offline");
    }

    [Fact]
    public void Values_are_html_escaped()
    {
        var mail = Single(EmailSamples.Song(artist: "AC & DC", title: "<script>alert(1)</script>", album: "\"Quote\" <b>"));

        mail.HtmlBody.Should().NotContain("<script>").And.NotContain("<b>");
        mail.HtmlBody.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.Contain("AC &amp; DC");

        var digest = Digest([new DigestEntry(EmailSamples.Song(title: "<script>x</script>"), Now)]);

        digest.HtmlBody.Should().NotContain("<script>").And.Contain("&lt;script&gt;x&lt;/script&gt;");
    }

    [Fact]
    public void The_link_appears_only_when_a_wondarr_address_is_set()
    {
        var withUrl = Single(EmailSamples.Song(id: 42));
        var without = EmailMessageBuilder.Single(
            EmailSamples.Song(id: 42),
            EmailSettings.Read(EmailSamples.Settings(("wondarrUrl", null))),
            From,
            To);

        withUrl.TextBody.Should().Contain("https://wondarr.example.com/song/42");
        withUrl.HtmlBody.Should().Contain("href=\"https://wondarr.example.com/song/42\"");
        without.TextBody.Should().NotContain("/song/");
        without.HtmlBody.Should().NotContain("href=");
    }

    [Fact]
    public void A_small_cover_is_embedded_by_content_id()
    {
        var cover = Cover("album", 1_000);

        var mail = Single(EmailSamples.Song(coverPath: cover, coverUrl: "https://img.example.com/c.jpg"));

        var image = mail.BodyParts.OfType<MimePart>().Single(part => part.ContentType.MediaType == "image");
        image.ContentId.Should().NotBeNullOrEmpty();
        mail.HtmlBody.Should().Contain($"src=\"cid:{image.ContentId}\"").And.NotContain("img.example.com");
        mail.Body.Should().BeOfType<MultipartAlternative>();
    }

    [Fact]
    public void A_cover_over_500_kilobytes_falls_back_to_its_url()
    {
        var cover = Cover("big", (500 * 1024) + 1);

        var mail = Single(EmailSamples.Song(coverPath: cover, coverUrl: "https://img.example.com/c.jpg"));

        mail.BodyParts.OfType<MimePart>().Should().NotContain(part => part.ContentType.MediaType == "image");
        mail.HtmlBody.Should().Contain("src=\"https://img.example.com/c.jpg\"");

        var none = Single(EmailSamples.Song(coverPath: cover));

        none.HtmlBody.Should().NotContain("<img");
    }

    [Fact]
    public void A_missing_cover_file_falls_back_and_only_a_file_called_cover_jpg_is_read()
    {
        var other = Path.Combine(_directory, "secret.txt");
        File.WriteAllText(other, "not a cover");

        var mail = Single(EmailSamples.Song(coverPath: other, coverUrl: "https://img.example.com/c.jpg"));

        mail.BodyParts.OfType<MimePart>().Should().NotContain(part => part.ContentType.MediaType == "image");
        mail.HtmlBody.Should().Contain("https://img.example.com/c.jpg");

        var absent = Single(EmailSamples.Song(coverPath: Path.Combine(_directory, "nowhere", "cover.jpg")));

        absent.HtmlBody.Should().NotContain("<img");
    }

    [Fact]
    public void A_digest_embeds_at_most_25_covers_and_the_rest_fall_back_to_urls()
    {
        var entries = Enumerable.Range(1, 30)
            .Select(index => new DigestEntry(
                EmailSamples.Song(
                    id: index,
                    title: $"Song {index}",
                    coverPath: Cover($"album{index}", 100),
                    coverUrl: $"https://img.example.com/{index}.jpg"),
                Now.AddMinutes(index)))
            .ToList();

        var mail = Digest(entries);

        mail.BodyParts.OfType<MimePart>().Count(part => part.ContentType.MediaType == "image").Should().Be(25);
        mail.HtmlBody.Should().Contain("https://img.example.com/26.jpg").And.Contain("https://img.example.com/30.jpg");
        mail.HtmlBody.Should().NotContain("https://img.example.com/1.jpg");
    }

    [Fact]
    public void Songs_of_one_album_share_one_embedded_cover()
    {
        var cover = Cover("shared", 100);

        var entries = Enumerable.Range(1, 3)
            .Select(index => new DigestEntry(EmailSamples.Song(id: index, title: $"Song {index}", coverPath: cover), Now.AddMinutes(index)))
            .ToList();

        var mail = Digest(entries);

        var image = mail.BodyParts.OfType<MimePart>().Single(part => part.ContentType.MediaType == "image");
        mail.HtmlBody!.Split($"cid:{image.ContentId}").Length.Should().Be(4, "three rows point at the one part");
    }

    [Fact]
    public void A_digest_groups_by_outcome_in_time_order_and_counts_them_in_the_subject()
    {
        var entries = new List<DigestEntry>
        {
            new(EmailSamples.Song(NotificationEventNames.Failure, 3, title: "Broken", body: "no peers"), Now.AddMinutes(1)),
            new(EmailSamples.Song(NotificationEventNames.Import, 1, title: "First"), Now.AddMinutes(2)),
            new(EmailSamples.Song(NotificationEventNames.Upgrade, 4, title: "Better"), Now.AddMinutes(3)),
            new(EmailSamples.Song(NotificationEventNames.Import, 2, title: "Second"), Now.AddMinutes(4)),
        };

        var mail = EmailMessageBuilder.Digest(
            entries,
            new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 13, 14, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc,
            EmailSettings.Read(EmailSamples.Settings()),
            From,
            To);

        mail.Subject.Should().Be("Wondarr: 2 imported, 1 upgraded, 1 failed (14:00–14:30)");
        mail.Body.Should().BeOfType<MultipartAlternative>();

        var text = mail.TextBody!;
        text.IndexOf("Imported (2)", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Upgraded (1)", StringComparison.Ordinal));
        text.IndexOf("Upgraded (1)", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Failed (1)", StringComparison.Ordinal));
        text.IndexOf("First", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Second", StringComparison.Ordinal));
        text.Should().Contain("no peers").And.Contain("https://wondarr.example.com/song/3");
        mail.HtmlBody.Should().Contain("Imported (2)").And.Contain("Failed (1)").And.Contain("soulseek");
    }

    [Fact]
    public void A_completed_download_shows_the_detail_table_and_the_cover_url_but_no_embedded_cover()
    {
        var mail = Single(EmailSamples.Song(NotificationEventNames.Download, coverUrl: "https://img.example.com/c.jpg"));

        mail.Subject.Should().Be("Wondarr: downloaded Oasis – Wonderwall");
        mail.TextBody.Should().Contain("Artist: Oasis").And.Contain("Title: Wonderwall").And.Contain("Album: Morning Glory");
        mail.TextBody.Should().Contain("Source: soulseek").And.Contain("Quality: FLAC").And.Contain("Size: 30.0 MB");
        mail.HtmlBody.Should().Contain("<table").And.Contain("Downloaded Oasis").And.Contain("src=\"https://img.example.com/c.jpg\"");
        mail.BodyParts.OfType<MimePart>().Should().NotContain(part => part.ContentType.MediaType == "image");
    }

    [Fact]
    public void A_digest_has_a_downloaded_group_before_the_imported_one_and_counts_it_in_the_subject()
    {
        var entries = new List<DigestEntry>
        {
            new(EmailSamples.Song(NotificationEventNames.Import, 1, title: "First"), Now.AddMinutes(1)),
            new(EmailSamples.Song(NotificationEventNames.Download, 2, title: "Alpha"), Now.AddMinutes(2)),
            new(EmailSamples.Song(NotificationEventNames.Download, 3, title: "Beta"), Now.AddMinutes(3)),
            new(EmailSamples.Song(NotificationEventNames.Download, 4, title: "Gamma"), Now.AddMinutes(4)),
            new(EmailSamples.Song(NotificationEventNames.Import, 5, title: "Second"), Now.AddMinutes(5)),
        };

        var mail = Digest(entries);

        mail.Subject.Should().Be("Wondarr: 3 downloaded, 2 imported (14:00–14:30)");

        var text = mail.TextBody!;
        text.Should().Contain("Downloaded (3)").And.Contain("Imported (2)");
        text.IndexOf("Downloaded (3)", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Imported (2)", StringComparison.Ordinal));
        mail.HtmlBody.Should().Contain("Downloaded (3)").And.Contain("Imported (2)");
    }

    [Fact]
    public void A_line_break_in_a_title_cannot_add_a_header()
    {
        var mail = Single(EmailSamples.Song(title: "Evil\r\nBcc: victim@example.com"));

        mail.Headers.Contains(HeaderId.Bcc).Should().BeFalse();
        mail.Bcc.Count.Should().Be(0);
        mail.Subject.Should().NotContain("\r").And.NotContain("\n");

        // And on the wire: the header block (up to the first blank line) has no Bcc line.
        mail.ToString().Split(["\r\n\r\n", "\n\n"], 2, StringSplitOptions.None)[0]
            .Split('\n').AsEnumerable().Should().NotContain(line => line.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_digest_subject_shows_dates_when_the_window_crosses_midnight_or_is_long()
    {
        MimeMessage Subject(DateTimeOffset from, DateTimeOffset to) =>
            EmailMessageBuilder.Digest(
                [new DigestEntry(EmailSamples.Song(), from)],
                from,
                to,
                TimeZoneInfo.Utc,
                EmailSettings.Read(EmailSamples.Settings()),
                From,
                To);

        var evening = new DateTimeOffset(2026, 10, 13, 23, 50, 0, TimeSpan.Zero);

        Subject(evening, evening.AddMinutes(30)).Subject.Should().Be("Wondarr: 1 imported (13 Oct 23:50–14 Oct 00:20)");
        Subject(Now, Now.AddMinutes(30)).Subject.Should().Be("Wondarr: 1 imported (14:00–14:30)");
        var early = new DateTimeOffset(2026, 10, 13, 0, 30, 0, TimeSpan.Zero);

        Subject(early, early.AddHours(13)).Subject.Should().Be("Wondarr: 1 imported (13 Oct 00:30–13 Oct 13:30)");
    }

    private static DateTimeOffset Now { get; } = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    private static MimeMessage Single(NotificationMessage message) =>
        EmailMessageBuilder.Single(message, EmailSettings.Read(EmailSamples.Settings()), From, To);

    private static MimeMessage Digest(IReadOnlyList<DigestEntry> entries) =>
        EmailMessageBuilder.Digest(entries, Now, Now.AddMinutes(30), TimeZoneInfo.Utc, EmailSettings.Read(EmailSamples.Settings()), From, To);

    /// <summary>Writes an album folder's cover.jpg of the given size and returns its path.</summary>
    private string Cover(string album, long bytes)
    {
        var folder = Path.Combine(_directory, album);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "cover.jpg");
        File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }
}
