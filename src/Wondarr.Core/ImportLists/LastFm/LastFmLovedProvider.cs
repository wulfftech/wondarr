using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.LastFm;

/// <summary>
/// The Last.fm loved-tracks import list (ADR-0012): every track a user has loved, oldest first, read
/// through the named <c>lastfm</c> client. The provider only reads; the sync resolves each track by
/// its recording MBID when Last.fm knows one, then by its text.
/// </summary>
public sealed class LastFmLovedProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string LastFmLovedType = "lastfmLoved";

    /// <summary>How many tracks one page asks for; Last.fm serves at most 200.</summary>
    internal const int PageSize = 200;

    /// <summary>At most 50 pages: 10 000 loved tracks, more than any list needs.</summary>
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
    ];

    private readonly IHttpClientFactory _factory;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>Initialises a new instance of the <see cref="LastFmLovedProvider"/> class.</summary>
    /// <param name="factory">Builds the named <c>lastfm</c> client.</param>
    /// <param name="time">Spaces the pages out.</param>
    public LastFmLovedProvider(IHttpClientFactory factory, TimeProvider time)
        : this(factory, Delay(time))
    {
    }

    internal LastFmLovedProvider(IHttpClientFactory factory, Func<TimeSpan, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(wait);

        _factory = factory;
        _wait = wait;
    }

    /// <inheritdoc />
    public string Type => LastFmLovedType;

    /// <inheritdoc />
    public string DisplayName => "Last.fm loved tracks";

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

        return problems;
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string? user;
        string? apiKey;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            user = LastFmSettings.Setting(settings.RootElement, "user");
            apiKey = LastFmSettings.Setting(settings.RootElement, "apiKey");
        }

        if (user is null || apiKey is null)
        {
            return ImportListFetchResult.Failed("The list has no Last.fm user or API key.");
        }

        var http = _factory.CreateClient(Metadata.ServiceCollectionExtensions.LastFmClientName);
        var entries = new List<ImportListEntry>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var answer = await LastFmFetch
                .PageAsync(http, "user.getlovedtracks", user, apiKey, null, PageSize, page, cancellationToken)
                .ConfigureAwait(false);

            if (!answer.Ok)
            {
                return ImportListFetchResult.Failed(answer.Error!);
            }

            var read = LastFmFetch.Loved(answer.Body);

            if (read?.Track is null)
            {
                return ImportListFetchResult.Failed("Last.fm answered a page Wondarr could not read.");
            }

            foreach (var track in read.Track)
            {
                if (Entry(track) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            if (page >= LastFmFetch.TotalPages(read.Attributes) || read.Track.Count == 0)
            {
                break;
            }

            await _wait(PageDelay, cancellationToken).ConfigureAwait(false);
        }

        return new ImportListFetchResult(entries);
    }

    /// <summary>The loved row as an entry: its id is the artist and title, lowercased.</summary>
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
            MbRecordingId: Text(track.Mbid));
    }

    /// <summary>A value with nothing but whitespace in it is no value at all.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Func<TimeSpan, CancellationToken, Task> Delay(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        return (delay, cancellationToken) => Task.Delay(delay, time, cancellationToken);
    }
}
