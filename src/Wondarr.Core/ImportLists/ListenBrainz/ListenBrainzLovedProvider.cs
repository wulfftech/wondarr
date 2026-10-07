using System.Globalization;
using System.Net;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.ListenBrainz;

/// <summary>
/// The ListenBrainz loved-tracks import list (ADR-0012): every recording a user has loved, newest
/// first, read through the named <c>listenbrainz</c> client. Most rows carry a recording MBID, so
/// the sync resolves them by it; a row without one is identified by its MessyBrainz id and resolved
/// by its text.
/// </summary>
public sealed class ListenBrainzLovedProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string ListenBrainzLovedType = "listenbrainzLoved";

    /// <summary>How many rows one page asks for; ListenBrainz serves at most 100.</summary>
    internal const int PageSize = 100;

    /// <summary>At most 100 pages: 10 000 loved tracks, more than any list needs.</summary>
    internal const int MaxPages = 100;

    private static readonly NotificationField[] SettingsFields =
    [
        new("user", "User", "text", true, "Your ListenBrainz user name"),
    ];

    private readonly IHttpClientFactory _factory;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>Initialises a new instance of the <see cref="ListenBrainzLovedProvider"/> class.</summary>
    /// <param name="factory">Builds the named <c>listenbrainz</c> client.</param>
    /// <param name="time">Waits out the rate-limit reset between pages.</param>
    public ListenBrainzLovedProvider(IHttpClientFactory factory, TimeProvider time)
        : this(factory, Waiter(time))
    {
    }

    internal ListenBrainzLovedProvider(IHttpClientFactory factory, Func<TimeSpan, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(wait);

        _factory = factory;
        _wait = wait;
    }

    /// <inheritdoc />
    public string Type => ListenBrainzLovedType;

    /// <inheritdoc />
    public string DisplayName => "ListenBrainz loved tracks";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) =>
        ListenBrainzSettings.Setting(settings, "user") is null
            ? ["Enter your ListenBrainz user name."]
            : [];

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string? user;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            user = ListenBrainzSettings.Setting(settings.RootElement, "user");
        }

        if (user is null)
        {
            return ImportListFetchResult.Failed("The list has no ListenBrainz user to read.");
        }

        var http = _factory.CreateClient(Metadata.ServiceCollectionExtensions.ListenBrainzClientName);
        var entries = new List<ImportListEntry>();
        var offset = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var answer = await ListenBrainzFetch
                .GetAsync(
                    http,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"feedback/user/{Uri.EscapeDataString(user)}/get-feedback"
                        + $"?score=1&count={PageSize.ToString(CultureInfo.InvariantCulture)}"
                        + $"&offset={offset.ToString(CultureInfo.InvariantCulture)}&metadata=true"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (answer.Status == HttpStatusCode.NotFound)
            {
                return ImportListFetchResult.Failed(
                    string.Create(CultureInfo.InvariantCulture, $"ListenBrainz has no user named '{user.Trim()}'."));
            }

            if (!answer.Ok)
            {
                return ImportListFetchResult.Failed(answer.Error!);
            }

            if (answer.Status != HttpStatusCode.OK)
            {
                return ImportListFetchResult.Failed($"ListenBrainz answered {(int)answer.Status}.");
            }

            var read = ListenBrainzFetch.Feedback(answer.Body);

            if (read is null)
            {
                return ImportListFetchResult.Failed("ListenBrainz answered a page Wondarr could not read.");
            }

            if (read.Feedback is not null)
            {
                foreach (var row in read.Feedback)
                {
                    if (Entry(row) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
            }

            offset += PageSize;

            if (offset >= read.TotalCount)
            {
                break;
            }

            if (answer.Wait > TimeSpan.Zero)
            {
                await _wait(answer.Wait, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ImportListFetchResult(entries);
    }

    /// <summary>
    /// The loved row as an entry: the recording MBID is the strongest match there is, and a row
    /// without one keeps its MessyBrainz id so two syncs still agree on what the line is.
    /// </summary>
    private static ImportListEntry? Entry(ListenBrainzFeedback row)
    {
        var mbid = ListenBrainzSettings.RecordingMbid(row.RecordingMbid);
        var msid = ListenBrainzSettings.RecordingMsid(row.RecordingMsid);

        if (mbid is null && msid is null)
        {
            return null;
        }

        return new ImportListEntry(
            string.Concat("lb:", mbid ?? msid),
            Text(row.TrackMetadata?.ArtistName),
            Text(row.TrackMetadata?.TrackName),
            MbRecordingId: mbid);
    }

    /// <summary>A value with nothing but whitespace in it is no value at all.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Func<TimeSpan, CancellationToken, Task> Waiter(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        return (delay, cancellationToken) => Task.Delay(delay, time, cancellationToken);
    }
}
