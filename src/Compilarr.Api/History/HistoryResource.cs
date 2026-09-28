using System.Text.Json;
using Compilarr.Api.Songs;
using Compilarr.Core.Domain;

namespace Compilarr.Api.History;

/// <summary>
/// One song lifecycle event. <c>data</c> is returned as a JSON object rather than the string the
/// column holds, so a client can read a grab's payload without parsing it a second time.
/// </summary>
/// <param name="Id">The history row.</param>
/// <param name="SongId">The song the event is about.</param>
/// <param name="Song">The song the event is about.</param>
/// <param name="EventType">What happened.</param>
/// <param name="Date">When it happened: the row's <c>createdAt</c>.</param>
/// <param name="SourceInstanceId">The source instance the grab went through, or <see langword="null"/>.</param>
/// <param name="QualityId">The quality the event involved, or <see langword="null"/>.</param>
/// <param name="Data">The event's payload as a JSON object.</param>
public sealed record HistoryResource(
    long Id,
    long SongId,
    SongResource Song,
    HistoryEventType EventType,
    DateTime Date,
    long? SourceInstanceId,
    long? QualityId,
    JsonElement Data);

/// <summary>Maps the stored history row to the shape the API returns.</summary>
public static class HistoryResourceExtensions
{
    /// <summary>
    /// Maps an event. Its song, and that song's artist, album context and file, must have been loaded
    /// by the query.
    /// </summary>
    /// <param name="item">The event to map.</param>
    public static HistoryResource ToResource(this HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new HistoryResource(
            item.Id,
            item.SongId,
            item.Song.ToResource(),
            item.EventType,
            item.CreatedAt,
            item.SourceInstanceId,
            item.QualityId,
            ParseData(item.Data));
    }

    /// <summary>
    /// Reads the stored JSON text back as an object. Rows are always written as an object, so anything
    /// else — including text that is not JSON at all — becomes an empty object rather than an
    /// exception on a read endpoint.
    /// </summary>
    private static JsonElement ParseData(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : emptyObject();
        }
        catch (JsonException)
        {
            return emptyObject();
        }

        static JsonElement emptyObject()
        {
            using var empty = JsonDocument.Parse("{}");

            return empty.RootElement.Clone();
        }
    }
}
