using System.Text.Json;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Songs;

/// <summary>
/// Where a song's file came from, reduced to the facts that are safe to show: <c>song_file.source_ref</c>
/// is a JSON blob written by three different code paths, and only the fields below ever leave it.
/// </summary>
/// <param name="Kind">
/// <c>reference</c> (the user's own file, never written), <c>adopted</c> (a copy filed into a managed
/// library), <c>download</c> (a grab Wondarr made) or <c>unknown</c> when nothing could be read.
/// </param>
/// <param name="Provider">The download provider (<c>soulseek</c>, <c>youtube</c>, …), when stored.</param>
/// <param name="Name">The remote file's name — its last path segment only, never the peer's folder — when stored.</param>
/// <param name="ReferenceLibraryId">The reference library, for a reference or adopted file.</param>
/// <param name="ReferenceFileId">The reference-library row, for a reference or adopted file.</param>
/// <param name="QueueItemId">The queue item the grab was, for a download.</param>
public sealed record SongFileSource(
    string Kind,
    string? Provider,
    string? Name,
    long? ReferenceLibraryId,
    long? ReferenceFileId,
    long? QueueItemId)
{
    /// <summary>The kind of a source that could not be read.</summary>
    public const string UnknownKind = "unknown";

    /// <summary>The kind of a grab Wondarr made.</summary>
    public const string DownloadKind = "download";

    /// <summary>
    /// Reads the safe fields out of a stored source reference. Never throws: a missing, empty or
    /// malformed blob is a source with only its kind.
    /// </summary>
    /// <param name="sourceType">The file's <c>source_type</c>.</param>
    /// <param name="sourceRef">The file's <c>source_ref</c> JSON, or <see langword="null"/>.</param>
    public static SongFileSource Parse(string sourceType, string? sourceRef)
    {
        var kind = sourceType switch
        {
            SourceTypes.Reference => SourceTypes.Reference,
            SourceTypes.Adopted => SourceTypes.Adopted,
            "" => UnknownKind,
            _ => DownloadKind,
        };
        var bare = new SongFileSource(kind, kind == DownloadKind ? sourceType : null, null, null, null, null);

        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return bare;
        }

        try
        {
            using var document = JsonDocument.Parse(sourceRef);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return bare;
            }

            var root = document.RootElement;

            return kind switch
            {
                SourceTypes.Reference or SourceTypes.Adopted => new SongFileSource(
                    kind,
                    null,
                    null,
                    ReadLong(root, "referenceLibraryId"),
                    ReadLong(root, "referenceFileId"),
                    null),
                _ => new SongFileSource(
                    kind,
                    ReadString(root, "provider") ?? bare.Provider,
                    LastSegment(ReadString(root, "remotePath")),
                    null,
                    null,
                    ReadLong(root, "queueItemId")),
            };
        }
        catch (JsonException)
        {
            return bare;
        }
    }

    /// <summary>Finds a property whatever its casing.</summary>
    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;

                return true;
            }
        }

        value = default;

        return false;
    }

    private static long? ReadLong(JsonElement root, string name) =>
        TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? ReadString(JsonElement root, string name) =>
        TryGet(root, name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>The last segment of a remote path, split on either slash; a peer's folder layout is not shown.</summary>
    private static string? LastSegment(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return null;
        }

        var trimmed = remotePath.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        var name = index < 0 ? trimmed : trimmed[(index + 1)..];

        return name.Length == 0 ? null : name;
    }
}
