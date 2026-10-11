using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.Songs;

/// <summary>
/// The background half of choosing an album: <c>RefileSong</c> moves the song's managed file to the
/// folder its new album names and retags it. A refusal or a failure fails the command, which is where
/// the song page looks for it.
/// </summary>
public sealed class RefileSongCommandHandler : ICommandHandler
{
    /// <summary>The name <c>PUT /api/v1/song/{id}/albumcontext</c> queues.</summary>
    public const string CommandName = "RefileSong";

    /// <summary>The command bodies are camelCase, like every other JSON this app exchanges.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopes;

    /// <summary>Initialises a new instance of the <see cref="RefileSongCommandHandler"/> class.</summary>
    /// <param name="scopes">The scope factory, so the re-file reads the rows through its own tracker.</param>
    public RefileSongCommandHandler(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <summary>Builds the body the queue is given for a song.</summary>
    /// <param name="songId">The song to re-file.</param>
    /// <returns>The JSON body.</returns>
    public static string BodyFor(long songId) =>
        JsonSerializer.Serialize(new RefileSongBody(CommandName, songId), BodyJson);

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var songId = ReadSongId(context.Body);

        using var scope = _scopes.CreateScope();
        var refiler = scope.ServiceProvider.GetRequiredService<ISongRefiler>();

        var result = await refiler.RefileAsync(songId, cancellationToken).ConfigureAwait(false);
        var id = songId.ToString(CultureInfo.InvariantCulture);

        return result.Outcome switch
        {
            SongRefileOutcome.Refiled => $"Moved song {id} to its new album's folder",
            SongRefileOutcome.AlreadyThere => $"Song {id} already sits in its album's folder; its tags were rewritten",
            SongRefileOutcome.NothingToMove => $"Song {id} has no managed file to move",
            _ => throw new InvalidOperationException(
                $"Could not move song {id} to its new album's folder: {result.Reason}"),
        };
    }

    /// <summary>The song a <c>RefileSong</c> body names.</summary>
    private static long ReadSongId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The RefileSong command body must name a songId.");
        }

        RefileSongBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<RefileSongBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The RefileSong command body is not valid JSON.", exception);
        }

        return parsed is { SongId: > 0 }
            ? parsed.SongId
            : throw new ArgumentException("The RefileSong command body must name a songId.");
    }

    /// <summary>The body of a re-file: which song.</summary>
    /// <param name="Name">The command name, echoed for readability.</param>
    /// <param name="SongId">The song to re-file.</param>
    private sealed record RefileSongBody(string? Name, long SongId);
}
