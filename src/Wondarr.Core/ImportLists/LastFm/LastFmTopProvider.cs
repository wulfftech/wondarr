using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.LastFm;

/// <summary>
/// The Last.fm top-tracks import list (ADR-0012): a user's most played tracks over a period, most
/// played first, read through the named <c>lastfm</c> client. The rows carry no MBID but often a
/// length, so the sync resolves each track by its text and checks the length.
/// </summary>
public sealed class LastFmTopProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string LastFmTopType = "lastfmTop";

    /// <summary>The smallest and largest top-tracks count Last.fm's own UI offers.</summary>
    internal const int MinCount = 1;

    internal const int MaxCount = 500;

    /// <summary>The count a list reads until the user changes it.</summary>
    internal const int DefaultCount = 50;

    /// <summary>How many tracks one page asks for; Last.fm serves at most 200.</summary>
    internal const int PageSize = 200;

    /// <summary>At most 50 pages, like the loved list: a safety bound, never a real one.</summary>
    internal const int MaxPages = 50;

    /// <summary>How long one page waits before the next: Last.fm allows 5 requests a second.</summary>
    internal static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(200);

    private static readonly NotificationField[] SettingsFields =
    [
        new("user", "User", "text", true, "Your Last.fm user name"),
        new(
            "apiKey",
            "API key",
            "password",
            true,
            "Your own Last.fm API key, from last.fm/api/account/create",
            Secret: true),
        new("period", "Period", "select", true, "Which stretch of listening the top tracks cover.", LastFmSettings.Periods),
        new("count", "How many top tracks", "number", true, "Between 1 and 500."),
    ];

    private readonly IHttpClientFactory _factory;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>Initialises a new instance of the <see cref="LastFmTopProvider"/> class.</summary>
    /// <param name="factory">Builds the named <c>lastfm</c> client.</param>
    /// <param name="time">Spaces the pages out.</param>
    public LastFmTopProvider(IHttpClientFactory factory, TimeProvider time)
        : this(factory, Delay(time))
    {
    }

    internal LastFmTopProvider(IHttpClientFactory factory, Func<TimeSpan, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(wait);

        _factory = factory;
        _wait = wait;
    }

    /// <inheritdoc />
    public string Type => LastFmTopType;

    /// <inheritdoc />
    public string DisplayName => "Last.fm top tracks";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        var problems = new List<string>();

        if (LastFmSettings.Setting(settings, "user") is null)
        {
            problems.Add("Enter your Last.fm user name.");
        }

        if (LastFmSettings.Setting(settings, "apiKey") is null)
        {
            problems.Add("Enter your Last.fm API key.");
        }

        var period = LastFmSettings.Setting(settings, "period") ?? LastFmSettings.DefaultPeriod;

        if (!LastFmSettings.Periods.Contains(period, StringComparer.Ordinal))
        {
            problems.Add("Choose one of the periods the list offers.");
        }

        switch (LastFmSettings.Number(settings, "count"))
        {
            case < MinCount or > MaxCount:
                problems.Add("How many top tracks must be between 1 and 500.");
                break;
        }

        return problems;
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string? user;
        string? apiKey;
        string period;
        int count;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            user = LastFmSettings.Setting(settings.RootElement, "user");
            apiKey = LastFmSettings.Setting(settings.RootElement, "apiKey");
            period = LastFmSettings.Setting(settings.RootElement, "period") ?? LastFmSettings.DefaultPeriod;
            count = Math.Clamp(
                LastFmSettings.Number(settings.RootElement, "count") ?? DefaultCount,
                MinCount,
                MaxCount);
        }

        if (user is null || apiKey is null)
        {
            return ImportListFetchResult.Failed("The list has no Last.fm user or API key.");
        }

        var http = _factory.CreateClient(Metadata.ServiceCollectionExtensions.LastFmClientName);
        var limit = Math.Min(count, PageSize);
        var entries = new List<ImportListEntry>(count);

        for (var page = 1; page <= MaxPages && entries.Count < count; page++)
        {
            var answer = await LastFmFetch
                .PageAsync(http, "user.gettoptracks", user, apiKey, limit, page, cancellationToken)
                .ConfigureAwait(false);

            if (!answer.Ok)
            {
                return ImportListFetchResult.Failed(answer.Error!);
            }

            var read = LastFmFetch.Top(answer.Body);

            if (read?.Track is null)
            {
                return ImportListFetchResult.Failed("Last.fm answered a page Wondarr could not read.");
            }

            foreach (var track in read.Track)
            {
                if (entries.Count >= count)
                {
                    break;
                }

                if (Entry(track) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            if (page >= LastFmFetch.TotalPages(read.Attributes) || read.Track.Count < limit)
            {
                break;
            }

            await _wait(PageDelay, cancellationToken).ConfigureAwait(false);
        }

        return new ImportListFetchResult(entries);
    }

    /// <summary>The top row as an entry: its id is the artist and title, lowercased.</summary>
    private static ImportListEntry? Entry(LastFmTrack track)
    {
        var artist = Text(track.Artist?.Name);
        var title = Text(track.Name);

        if (artist is null && title is null)
        {
            return null;
        }

        return new ImportListEntry(
            string.Concat("lastfm:", artist?.ToLowerInvariant(), "|", title?.ToLowerInvariant()),
            artist,
            title,
            DurationMs: Duration(track.Duration),
            MbRecordingId: Text(track.Mbid));
    }

    /// <summary>The length in milliseconds, or <see langword="null"/> when Last.fm does not know it.</summary>
    private static int? Duration(string? seconds)
    {
        if (seconds is null
            || !int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return null;
        }

        return value * 1000;
    }

    /// <summary>A value with nothing but whitespace in it is no value at all.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Func<TimeSpan, CancellationToken, Task> Delay(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        return (delay, cancellationToken) => Task.Delay(delay, time, cancellationToken);
    }
}
