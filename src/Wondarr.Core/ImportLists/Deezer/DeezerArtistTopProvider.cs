using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Notifications;

namespace Wondarr.Core.ImportLists.Deezer;

/// <summary>
/// The Deezer artist-top-tracks import list (ADR-0012): an artist's most played tracks, read through
/// <see cref="IDeezerClient"/>. The artist is a link or an id used as it is, or a name searched for —
/// the exact name wins, else the most fanned of the first five. These rows carry no ISRC, so the sync
/// resolves each track by its Deezer id.
/// </summary>
public sealed class DeezerArtistTopProvider : IImportListProvider
{
    /// <summary>The type stored in <see cref="ImportList.Type"/>.</summary>
    public const string DeezerArtistTopType = "deezerArtistTop";

    /// <summary>The smallest and largest top-tracks count Deezer serves.</summary>
    internal const int MinCount = 1;

    internal const int MaxCount = 100;

    /// <summary>The count a list reads until the user changes it.</summary>
    internal const int DefaultCount = 10;

    /// <summary>How many artists one name search asks for.</summary>
    internal const int SearchLimit = 5;

    private static readonly NotificationField[] SettingsFields =
    [
        new("artist", "Artist", "text", true, "An artist name, or a Deezer artist link or id"),
        new("count", "How many top tracks", "number", true, "Between 1 and 100."),
    ];

    private readonly IDeezerClient _deezer;

    /// <summary>Initialises a new instance of the <see cref="DeezerArtistTopProvider"/> class.</summary>
    /// <param name="deezer">The shared Deezer client (its cache, its quota handler).</param>
    public DeezerArtistTopProvider(IDeezerClient deezer)
    {
        ArgumentNullException.ThrowIfNull(deezer);

        _deezer = deezer;
    }

    /// <inheritdoc />
    public string Type => DeezerArtistTopType;

    /// <inheritdoc />
    public string DisplayName => "Artist top tracks (Deezer)";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields => SettingsFields;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText)
    {
        var problems = new List<string>();
        var artist = DeezerListSettings.Setting(settings, "artist");

        if (artist is null)
        {
            problems.Add("Enter an artist name, or a Deezer artist link or id.");
        }
        else if (DeezerLink.IsShortLink(artist))
        {
            problems.Add("Open the short link in a browser and paste the full artist link.");
        }
        else if (artist.Contains("://", StringComparison.Ordinal) && DeezerLink.ArtistId(artist) is null)
        {
            problems.Add("That does not look like a Deezer artist link.");
        }

        switch (Count(settings))
        {
            case null:
                problems.Add("Choose how many top tracks to read (between 1 and 100).");
                break;
            case < MinCount or > MaxCount:
                problems.Add("How many top tracks must be between 1 and 100.");
                break;
        }

        return problems;
    }

    /// <inheritdoc />
    public async Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);

        string? artist;
        int count;

        using (var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(list.Settings) ? "{}" : list.Settings))
        {
            artist = DeezerListSettings.Setting(settings.RootElement, "artist");
            count = Math.Clamp(Count(settings.RootElement) ?? DefaultCount, MinCount, MaxCount);
        }

        if (artist is null)
        {
            return ImportListFetchResult.Failed("The list has no artist to read.");
        }

        var artistId = DeezerLink.ArtistId(artist) ?? await SearchArtistAsync(artist, cancellationToken).ConfigureAwait(false);

        if (artistId is null)
        {
            return ImportListFetchResult.Failed(
                string.Create(CultureInfo.InvariantCulture, $"Deezer has no artist named '{artist.Trim()}'."));
        }

        var top = await _deezer.GetArtistTopAsync(artistId.Value, count, cancellationToken).ConfigureAwait(false);

        if (top is null)
        {
            return ImportListFetchResult.Failed("The Deezer artist was not found, or it has no top tracks.");
        }

        var entries = new List<ImportListEntry>(top.Data.Count);

        foreach (var track in top.Data)
        {
            if (track.Id > 0)
            {
                entries.Add(Entry(track));
            }
        }

        return new ImportListFetchResult(entries);
    }

    /// <summary>
    /// Searches a name: the artist whose name equals it (case-insensitive, after trimming) wins, else
    /// the one with the most fans among the first five.
    /// </summary>
    private async Task<long?> SearchArtistAsync(string name, CancellationToken cancellationToken)
    {
        var search = await _deezer.SearchArtistsAsync(name, SearchLimit, cancellationToken).ConfigureAwait(false);
        var wanted = name.Trim();

        foreach (var hit in search.Data)
        {
            if (string.Equals(hit.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return hit.Id;
            }
        }

        DeezerArtistSearchHit? best = null;

        foreach (var hit in search.Data)
        {
            if (best is null || (hit.NbFan ?? 0) > (best.NbFan ?? 0))
            {
                best = hit;
            }
        }

        return best?.Id;
    }

    /// <summary>The top-tracks row as an entry: the Deezer id is the strongest match there is.</summary>
    private static ImportListEntry Entry(DeezerTrack track) => new(
        string.Create(CultureInfo.InvariantCulture, $"deezer:{track.Id}"),
        string.IsNullOrWhiteSpace(track.Artist.Name) ? null : track.Artist.Name,
        string.IsNullOrWhiteSpace(track.Title) ? null : track.Title,
        string.IsNullOrWhiteSpace(track.Album.Title) ? null : track.Album.Title,
        track.Duration > 0 ? track.Duration * 1000 : null,
        Isrc: null,
        DeezerId: track.Id);

    private static int? Count(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("count", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}
