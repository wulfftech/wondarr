using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Searching;

/// <summary>
/// What a torrent or usenet grab handle carries (<see cref="GrabHandle.Value"/>, read only by the
/// indexer source): the client and the container in it, the wanted file, where it is staged, and the
/// song as the matcher needs it when the container's file list was unknown at search time.
/// </summary>
/// <param name="ClientId">The download client row the container went to.</param>
/// <param name="IndexerId">The indexer row that listed the release.</param>
/// <param name="ReleaseId">The indexer's guid for the release.</param>
/// <param name="InfoHash">Torrents: the info-hash, lower-case hex.</param>
/// <param name="JobId">Usenet: the client's job id.</param>
/// <param name="FilePath">
/// The wanted file's path inside the container (a usenet file's name); null when the file list was
/// unknown, and the file is found by <see cref="Song"/> once the metadata or the unpacked post is there.
/// Torrents are matched to the client's list by path, never by index (pad files shift the numbers).
/// </param>
/// <param name="StagingDir">The grab's own folder the finished file is linked or moved into.</param>
/// <param name="Song">The song as the matcher needs it, for a container whose file list was unknown.</param>
public sealed record ContainerGrab(
    long ClientId,
    long IndexerId,
    string ReleaseId,
    string? InfoHash,
    string? JobId,
    string? FilePath,
    string StagingDir,
    ContainerMatchRequest? Song)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The handle's JSON.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a handle back; throws when it is not one of ours.</summary>
    /// <param name="value">The handle's JSON.</param>
    public static ContainerGrab Deserialize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return JsonSerializer.Deserialize<ContainerGrab>(value, Json)
            ?? throw new InvalidOperationException("The grab handle is empty.");
    }

    /// <summary>Reads a handle back, or null when it is not one of ours.</summary>
    /// <param name="value">The handle's JSON.</param>
    public static ContainerGrab? TryDeserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ContainerGrab>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
